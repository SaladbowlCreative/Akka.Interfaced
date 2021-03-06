﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

#pragma warning disable SA1130 // Use lambda syntax

namespace Akka.Interfaced
{
    internal class RequestHandlerBuilder
    {
        private Type _type;
        private FilterHandlerBuilder _filterHandlerBuilder;
        private Dictionary<Type, RequestHandlerItem> _table;

        public Dictionary<Type, RequestHandlerItem> Build(Type type, FilterHandlerBuilder filterHandlerBuilder)
        {
            _type = type;
            _filterHandlerBuilder = filterHandlerBuilder;
            _table = new Dictionary<Type, RequestHandlerItem>();

            BuildRegularInterfaceHandlers();
            BuildExtendedInterfaceHandlers();

            return _table;
        }

        private void BuildRegularInterfaceHandlers()
        {
            foreach (var ifs in _type.GetInterfaces())
            {
                if (ifs.GetInterfaces().All(t => t != typeof(IInterfacedActor) && t != typeof(IInterfacedActorSync)))
                    continue;

                var primaryInterface = ifs;

                var alternativeInterfaceAttribute = ifs.GetCustomAttribute<AlternativeInterfaceAttribute>();
                if (alternativeInterfaceAttribute != null)
                {
                    primaryInterface = alternativeInterfaceAttribute.Type.IsGenericType
                        ? alternativeInterfaceAttribute.Type.MakeGenericType(ifs.GetGenericArguments())
                        : alternativeInterfaceAttribute.Type;
                }

                var interfaceMap = _type.GetInterfaceMap(ifs);
                var methodItems = interfaceMap.InterfaceMethods.Zip(interfaceMap.TargetMethods, Tuple.Create)
                                              .OrderBy(p => p.Item1, new MethodInfoComparer())
                                              .ToArray();
                var payloadTypeTable = GetInterfacePayloadTypeTable(primaryInterface);

                for (var i = 0; i < methodItems.Length; i++)
                {
                    var targetMethod = methodItems[i].Item2;
                    var invokePayloadType = payloadTypeTable[i, 0];
                    var returnPayloadType = payloadTypeTable[i, 1];
                    var filterChain = _filterHandlerBuilder.Build(targetMethod, FilterChainKind.Request);
                    var isSyncHandler = alternativeInterfaceAttribute != null && filterChain.AsyncFilterExists == false;
                    var isReentrant = isSyncHandler == false && HandlerBuilderHelpers.IsReentrantMethod(targetMethod);

                    AddHandler(ifs, targetMethod, invokePayloadType, returnPayloadType, filterChain, isSyncHandler, isReentrant);
                }
            }
        }

        private void BuildExtendedInterfaceHandlers()
        {
            var targetMethods =
                _type.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                     .Where(m => m.GetCustomAttribute<ExtendedHandlerAttribute>() != null)
                     .Select(m => Tuple.Create(m, m.GetCustomAttribute<ExtendedHandlerAttribute>()))
                     .ToList();

            var extendedInterfaces =
                _type.GetInterfaces()
                     .Where(t => t.FullName.StartsWith("Akka.Interfaced.IExtendedInterface"))
                     .SelectMany(t => t.GenericTypeArguments)
                     .Where(t => t.GetInterfaces().Any(i => i == typeof(IInterfacedActor)))
                     .ToArray();

            // includes base interfaces
            var extendedAllInterfaces = extendedInterfaces
                .Concat(extendedInterfaces.SelectMany(t => t.GetInterfaces().Where(u => u != typeof(IInterfacedActor))))
                .Distinct().ToArray();

            foreach (var ifs in extendedAllInterfaces)
            {
                var payloadTypeTable = GetInterfacePayloadTypeTable(ifs);
                var interfaceMethods = ifs.GetMethods().OrderBy(m => m, new MethodInfoComparer()).ToArray();

                for (var i = 0; i < interfaceMethods.Length; i++)
                {
                    var interfaceMethod = interfaceMethods[i];
                    var invokePayloadType = payloadTypeTable[i, 0];
                    var returnPayloadType = payloadTypeTable[i, 1];
                    var name = interfaceMethod.Name;
                    var parameters = interfaceMethod.GetParameters();

                    // find a method which can handle this invoke payload

                    MethodInfo targetMethod = null;
                    foreach (var method in targetMethods)
                    {
                        if (method.Item2.Type != null || method.Item2.Method != null)
                        {
                            // check tagged method
                            if (method.Item2.Type != null && method.Item2.Type != ifs)
                                continue;
                            if (method.Item2.Method != null && method.Item2.Method != name)
                                continue;
                        }
                        else if (method.Item1.Name != name)
                        {
                            // check method
                            continue;
                        }

                        if (HandlerBuilderHelpers.AreParameterTypesEqual(method.Item1.GetParameters(), parameters))
                        {
                            if (targetMethod != null)
                            {
                                throw new InvalidOperationException(
                                    $"Ambiguous handlers for {ifs.FullName}.{interfaceMethod.Name} method.\n" +
                                    $" {targetMethod.Name}\n {method.Item1.Name}\n");
                            }
                            targetMethod = method.Item1;
                        }
                    }
                    if (targetMethod == null)
                    {
                        throw new InvalidOperationException(
                            $"Cannot find handler for {ifs.FullName}.{interfaceMethod.Name}");
                    }
                    targetMethods.RemoveAll(x => x.Item1 == targetMethod);

                    // build handler

                    var isAsyncMethod = targetMethod.ReturnType.Name.StartsWith("Task");
                    var filterChain = _filterHandlerBuilder.Build(targetMethod, FilterChainKind.Request);
                    var isSyncHandler = isAsyncMethod == false && filterChain.AsyncFilterExists == false;
                    var isReentrant = isSyncHandler == false && HandlerBuilderHelpers.IsReentrantMethod(targetMethod);

                    if (isAsyncMethod == false && targetMethod.GetCustomAttribute<AsyncStateMachineAttribute>() != null)
                        throw new InvalidOperationException($"Async void handler is not supported. ({_type.FullName}.{targetMethod.Name})");

                    AddHandler(ifs, targetMethod, invokePayloadType, returnPayloadType, filterChain, isSyncHandler, isReentrant);
                }
            }
        }

        private void AddHandler(Type interfaceType, MethodInfo method, Type invokePayloadType, Type returnPayloadType, FilterChain filterChain, bool isSyncHandler, bool isReentrant)
        {
            if (method.IsGenericMethod == false)
            {
                if (isSyncHandler)
                {
                    _table.Add(invokePayloadType, new RequestHandlerItem
                    {
                        InterfaceType = interfaceType,
                        IsReentrant = isReentrant,
                        Handler = BuildHandler(_type, invokePayloadType, returnPayloadType, method, filterChain)
                    });
                }
                else
                {
                    _table.Add(invokePayloadType, new RequestHandlerItem
                    {
                        InterfaceType = interfaceType,
                        IsReentrant = isReentrant,
                        AsyncHandler = BuildAsyncHandler(_type, invokePayloadType, returnPayloadType, method, filterChain)
                    });
                }
            }
            else
            {
                // because a generic method needs parameter types to construct handler
                // so factory method is built to generate the handler when paramter types are ready

                if (isSyncHandler)
                {
                    _table.Add(invokePayloadType, new RequestHandlerItem
                    {
                        InterfaceType = interfaceType,
                        IsReentrant = isReentrant,
                        IsGeneric = true,
                        GenericHandlerBuilder = t => new RequestHandlerItem
                        {
                            InterfaceType = interfaceType,
                            IsReentrant = isReentrant,
                            Handler = BuildGenericHandler(_type, t, returnPayloadType, method, filterChain)
                        }
                    });
                }
                else
                {
                    _table.Add(invokePayloadType, new RequestHandlerItem
                    {
                        InterfaceType = interfaceType,
                        IsReentrant = isReentrant,
                        IsGeneric = true,
                        GenericHandlerBuilder = t => new RequestHandlerItem
                        {
                            InterfaceType = interfaceType,
                            IsReentrant = isReentrant,
                            AsyncHandler = BuildGenericAsyncHandler(_type, t, returnPayloadType, method, filterChain)
                        }
                    });
                }
            }
        }

        private static RequestHandler BuildHandler(
            Type targetType, Type invokePayloadType, Type returnPayloadType, MethodInfo method, FilterChain filterChain)
        {
            var handler = RequestHandlerFuncBuilder.Build(targetType, invokePayloadType, returnPayloadType, method);

            return delegate(object self, RequestMessage request, Action<ResponseMessage, Exception> onCompleted)
            {
                var filterPerInstanceProvider = filterChain.PerInstanceFilterExists ? (IFilterPerInstanceProvider)self : null;

                // Create PerRequest filters

                IFilter[] filterPerRequests = null;
                if (filterChain.PerInvokeFilterFactories.Length > 0)
                {
                    filterPerRequests = new IFilter[filterChain.PerInvokeFilterFactories.Length];
                    for (var i = 0; i < filterChain.PerInvokeFilterFactories.Length; i++)
                    {
                        filterPerRequests[i] = filterChain.PerInvokeFilterFactories[i].CreateInstance(self, request);
                    }
                }

                // Call PreFilters

                ResponseMessage response = null;
                Exception exception = null;

                if (filterChain.PreFilterAccessors.Length > 0)
                {
                    var context = new PreRequestFilterContext
                    {
                        Actor = self,
                        Request = request,
                    };
                    foreach (var filterAccessor in filterChain.PreFilterAccessors)
                    {
                        try
                        {
                            var filter = filterAccessor(filterPerInstanceProvider, filterPerRequests);
                            ((IPreRequestFilter)filter).OnPreRequest(context);
                        }
                        catch (Exception e)
                        {
                            context.Exception = e;
                        }
                    }
                    response = context.Response;
                    exception = context.Exception;
                }

                // Call Handler

                var intercepted = response != null || exception != null;
                if (intercepted == false)
                {
                    try
                    {
                        var returnPayload = handler(self, request.InvokePayload);
                        response = new ResponseMessage
                        {
                            RequestId = request.RequestId,
                            ReturnPayload = returnPayload
                        };
                    }
                    catch (ResponsiveException e)
                    {
                        response = new ResponseMessage
                        {
                            RequestId = request.RequestId,
                            Exception = e.InnerException
                        };
                    }
                    catch (Exception e)
                    {
                        exception = e;
                    }
                }

                // Call PostFilters

                if (filterChain.PostFilterAccessors.Length > 0)
                {
                    var context = new PostRequestFilterContext
                    {
                        Actor = self,
                        Request = request,
                        Response = response,
                        Exception = exception,
                        Intercepted = intercepted
                    };
                    foreach (var filterAccessor in filterChain.PostFilterAccessors)
                    {
                        try
                        {
                            var filter = filterAccessor(filterPerInstanceProvider, filterPerRequests);
                            ((IPostRequestFilter)filter).OnPostRequest(context);
                        }
                        catch (Exception e)
                        {
                            context.Exception = e;
                        }
                    }
                    response = context.Response;
                    exception = context.Exception;
                }

                // Build response for a thrown exception

                if (exception != null && response == null)
                {
                    response = new ResponseMessage
                    {
                        RequestId = request.RequestId,
                        Exception = new RequestFaultException("", exception)
                    };
                }

                // Callback

                onCompleted?.Invoke(response, exception);

                return response;
            };
        }

        private static RequestAsyncHandler BuildAsyncHandler(
            Type targetType, Type invokePayloadType, Type returnPayloadType, MethodInfo method, FilterChain filterChain)
        {
            var isAsyncMethod = method.ReturnType.Name.StartsWith("Task");
            var handler = isAsyncMethod
                ? RequestHandlerAsyncBuilder.Build(targetType, invokePayloadType, returnPayloadType, method)
                : RequestHandlerSyncToAsyncBuilder.Build(targetType, invokePayloadType, returnPayloadType, method);

            // TODO: Optimize this function when without async filter
            return async delegate(object self, RequestMessage request, Action<ResponseMessage, Exception> onCompleted)
            {
                var filterPerInstanceProvider = filterChain.PerInstanceFilterExists ? (IFilterPerInstanceProvider)self : null;

                // Create PerRequest filters

                IFilter[] filterPerRequests = null;
                if (filterChain.PerInvokeFilterFactories.Length > 0)
                {
                    filterPerRequests = new IFilter[filterChain.PerInvokeFilterFactories.Length];
                    for (var i = 0; i < filterChain.PerInvokeFilterFactories.Length; i++)
                    {
                        filterPerRequests[i] = filterChain.PerInvokeFilterFactories[i].CreateInstance(self, request);
                    }
                }

                // Call PreFilters

                ResponseMessage response = null;
                Exception exception = null;

                if (filterChain.PreFilterAccessors.Length > 0)
                {
                    var context = new PreRequestFilterContext
                    {
                        Actor = self,
                        Request = request
                    };
                    foreach (var filterAccessor in filterChain.PreFilterAccessors)
                    {
                        try
                        {
                            var filter = filterAccessor(filterPerInstanceProvider, filterPerRequests);
                            var preFilter = filter as IPreRequestFilter;
                            if (preFilter != null)
                                preFilter.OnPreRequest(context);
                            else
                                await ((IPreRequestAsyncFilter)filter).OnPreRequestAsync(context);
                        }
                        catch (Exception e)
                        {
                            context.Exception = e;
                        }
                    }
                    response = context.Response;
                    exception = context.Exception;
                }

                // Call Handler

                var intercepted = response != null || exception != null;
                if (intercepted == false)
                {
                    try
                    {
                        var returnPayload = await handler(self, request.InvokePayload);
                        response = new ResponseMessage
                        {
                            RequestId = request.RequestId,
                            ReturnPayload = returnPayload
                        };
                    }
                    catch (ResponsiveException e)
                    {
                        response = new ResponseMessage
                        {
                            RequestId = request.RequestId,
                            Exception = e.InnerException
                        };
                    }
                    catch (Exception e)
                    {
                        exception = e;
                    }
                }

                // Call PostFilters

                if (filterChain.PostFilterAccessors.Length > 0)
                {
                    var context = new PostRequestFilterContext
                    {
                        Actor = self,
                        Request = request,
                        Response = response,
                        Exception = exception,
                        Intercepted = intercepted
                    };
                    foreach (var filterAccessor in filterChain.PostFilterAccessors)
                    {
                        try
                        {
                            var filter = filterAccessor(filterPerInstanceProvider, filterPerRequests);
                            var postFilter = filter as IPostRequestFilter;
                            if (postFilter != null)
                                postFilter.OnPostRequest(context);
                            else
                                await ((IPostRequestAsyncFilter)filter).OnPostRequestAsync(context);
                        }
                        catch (Exception e)
                        {
                            context.Exception = e;
                        }
                    }
                    response = context.Response;
                    exception = context.Exception;
                }

                // Build response for a thrown exception

                if (exception != null && response == null)
                {
                    response = new ResponseMessage
                    {
                        RequestId = request.RequestId,
                        Exception = new RequestFaultException("", exception)
                    };
                }

                // Callback

                onCompleted?.Invoke(response, exception);

                return response;
            };
        }

        private static RequestHandler BuildGenericHandler(
            Type targetType, Type invokePayloadType, Type returnPayloadType, MethodInfo method, FilterChain filterChain)
        {
            var argTypes = invokePayloadType.GetGenericArguments();
            var genericReturnPayloadType = returnPayloadType?.MakeGenericType(argTypes);
            var genericMethod = method.MakeGenericMethod(argTypes.Skip(argTypes.Length - method.GetGenericArguments().Length).ToArray());
            return BuildHandler(targetType, invokePayloadType, genericReturnPayloadType, genericMethod, filterChain);
        }

        private static RequestAsyncHandler BuildGenericAsyncHandler(
            Type targetType, Type invokePayloadType, Type returnPayloadType, MethodInfo method, FilterChain filterChain)
        {
            var argTypes = invokePayloadType.GetGenericArguments();
            var genericReturnPayloadType = returnPayloadType?.MakeGenericType(argTypes);
            var genericMethod = method.MakeGenericMethod(argTypes.Skip(argTypes.Length - method.GetGenericArguments().Length).ToArray());
            return BuildAsyncHandler(targetType, invokePayloadType, genericReturnPayloadType, genericMethod, filterChain);
        }

        private static Type[,] GetInterfacePayloadTypeTable(Type interfaceType)
        {
            var payloadTypes = (Type[,])HandlerBuilderHelpers.GetInterfacePayloadTypeTable(interfaceType, PayloadTableKind.Request);
            if (payloadTypes == null || payloadTypes.GetLength(0) != interfaceType.GetMethods().Length)
            {
                throw new InvalidOperationException(
                    $"Mismatched a payload table for {interfaceType.FullName}");
            }

            return payloadTypes;
        }
    }
}
