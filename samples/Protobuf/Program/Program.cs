﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Configuration;
using Akka.Interfaced;
using Protobuf.Interface;

namespace Protobuf.Program
{
    class Program
    {
        static void Main(string[] args)
        {
            // force interface assembly to be loaded before creating ProtobufSerializer

            var type = typeof(IHelloWorld);
            if (type == null)
                throw new InvalidProgramException("!");

            // Serialization debug options (serialize-messages = on) cannot be used,
            // because ProtobufSerializer supports Request/ResponseMessage only (not internal messages).
            // To test serialization all testing actors will be created on server system and
            // every requests will be delivered from client system.

            var commonConfig = ConfigurationFactory.ParseString(@"
                akka {
                  actor {
                    provider = ""Akka.Remote.RemoteActorRefProvider, Akka.Remote""
                    serializers {
                      proto = ""Akka.Interfaced.ProtobufSerializer.ProtobufSerializer, Akka.Interfaced.ProtobufSerializer""
                    }
                    serialization-bindings {
                      ""Akka.Interfaced.NotificationMessage, Akka.Interfaced"" = proto
                      ""Akka.Interfaced.RequestMessage, Akka.Interfaced"" = proto
                      ""Akka.Interfaced.ResponseMessage, Akka.Interfaced"" = proto
                    }
                  }
                  remote {
                    helios.tcp {
                      hostname = localhost
                    }
                  }
                }");

            var serverSystem = CreateServer(commonConfig);
            var clientSystem = CreateClient(commonConfig);

            TestHelloWorld(serverSystem, clientSystem).Wait();
            TestSurrogate(serverSystem, clientSystem).Wait();
            TestPedantic(serverSystem, clientSystem).Wait();
            
            Console.WriteLine("\nEnter to quit.");
            Console.ReadLine();
        }

        private static ActorSystem CreateServer(Config commonConfig)
        {
            var config = commonConfig.WithFallback("akka.remote.helios.tcp.port = 9001");
            var system = ActorSystem.Create("Server", config);
            DeadRequestProcessingActor.Install(system);
            return system;
        }

        private static ActorSystem CreateClient(Config commonConfig)
        {
            var config = commonConfig.WithFallback("akka.remote.helios.tcp.port = 9002");
            var system = ActorSystem.Create("Client", config);
            DeadRequestProcessingActor.Install(system);
            return system;
        }

        private async static Task TestHelloWorld(ActorSystem serverSystem, ActorSystem clientSystem)
        {
            Console.WriteLine("\n*** HelloWorld ***");

            var serverActor = serverSystem.ActorOf<HelloWorldActor>("actor");
            var clientActor = clientSystem.ActorSelection("akka.tcp://Server@localhost:9001/user/actor").ResolveOne(TimeSpan.Zero).Result;
            var helloWorld = new HelloWorldRef(clientActor);

            Console.WriteLine(await helloWorld.SayHello("World"));
            Console.WriteLine(await helloWorld.SayHello("Dlrow"));

            try
            {
                Console.WriteLine(await helloWorld.SayHello(""));
            }
            catch (Exception e)
            {
                Console.WriteLine("!EXCEPTION! " + e.GetType().Name + " " + e.Message);
            }
            
            Console.WriteLine(await helloWorld.GetHelloCount());
        }

        private async static Task TestSurrogate(ActorSystem serverSystem, ActorSystem clientSystem)
        {
            Console.WriteLine("\n*** Surrogate ***");

            var serverActor = serverSystem.ActorOf<SurrogateActor>("surrogate");
            var clientActor = clientSystem.ActorSelection("akka.tcp://Server@localhost:9001/user/surrogate").ResolveOne(TimeSpan.Zero).Result;
            var surrogate = new SurrogateRef(clientActor);

            Console.WriteLine(await surrogate.GetPath(clientActor.Path));
            Console.WriteLine(await surrogate.GetAddress(clientActor.Path.Address));
            Console.WriteLine((await surrogate.GetSelf()).Path);
        }

        private async static Task TestPedantic(ActorSystem serverSystem, ActorSystem clientSystem)
        {
            Console.WriteLine("\n*** Pedantic ***");

            var serverActor = serverSystem.ActorOf<PedanticActor>("pedantic");
            var clientActor = clientSystem.ActorSelection("akka.tcp://Server@localhost:9001/user/pedantic").ResolveOne(TimeSpan.Zero).Result;
            var pedantic = new PedanticRef(clientActor);

            await pedantic.TestCall();
            Console.WriteLine(await pedantic.TestOptional(null));
            Console.WriteLine(await pedantic.TestOptional(1));
            Console.WriteLine(await pedantic.TestTuple(Tuple.Create(1, "One")));
            Console.WriteLine(await pedantic.TestParams(1, 2, 3));
            Console.WriteLine(await pedantic.TestPassClass(new TestParam { Name = "Mouse", Price = 10 }));
            Console.WriteLine(await pedantic.TestReturnClass(1, 2));
        }
    }
}
