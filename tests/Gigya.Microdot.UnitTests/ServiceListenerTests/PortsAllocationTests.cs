﻿using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Gigya.Microdot.Fakes;
using Gigya.Microdot.Hosting.HttpService;
using Gigya.Microdot.Interfaces;
using Gigya.Microdot.Ninject;
using Gigya.Microdot.ServiceProxy;
using Gigya.Microdot.SharedLogic;
using Gigya.Microdot.SharedLogic.Exceptions;
using Gigya.Microdot.SharedLogic.HttpService;
using Gigya.Microdot.Testing.Shared;
using Gigya.Microdot.UnitTests.ServiceProxyTests;

using Ninject;

using NUnit.Framework;

using RichardSzalay.MockHttp;

using Shouldly;

namespace Gigya.Microdot.UnitTests.ServiceListenerTests
{
    [TestFixture,Parallelizable(ParallelScope.Fixtures)]
    public class PortsAllocationTests
    {
        [Test]
        public async Task For_ServiceProxy_TakeDefaultSlot()
        {
            var kernel = SetUpKernel(new ServiceArguments(slotNumber: 5));
            var serviceProxyFunc = kernel.Get<Func<string, ServiceProxyProvider>>();
            var serviceProxy = serviceProxyFunc(TestingKernel<ConsoleLog>.APPNAME);
            var handlerMock = new MockHttpMessageHandler();
            handlerMock.When("*").Respond(req =>
            {
                req.RequestUri.Port.ShouldBe(40001);
                return HttpResponseFactory.GetResponse(content: "null");
            });

            serviceProxy.HttpMessageHandler = handlerMock;
            await serviceProxy.Invoke(new HttpServiceRequest("myMethod", null, new Dictionary<string, object>()), typeof(int?));
        }

        
        [Test]
        public void ServiceArguments()
        {
            var args = new ServiceArguments(new[] {"--SlotNumber:5"});
            args.SlotNumber.ShouldBe(5);


            args = new ServiceArguments(slotNumber:5);
            args.SlotNumber.ShouldBe(5);
        }

        private TestingKernel<ConsoleLog> SetUpKernel(ServiceArguments serviceArguments, 
            bool isSlotMode=true,
            bool withDefault=true)
        {
            var mockConfig = new Dictionary<string, string>
            {
                {"Discovery.PortAllocation.IsSlotMode",isSlotMode.ToString()},               
            };

            if(withDefault)
            {
                mockConfig.Add($"Discovery.Services.{TestingKernel<ConsoleLog>.APPNAME}.DefaultSlotNumber", "1");
            }

            return  new TestingKernel<ConsoleLog>(
                k =>
                {
                    k.Load<MicrodotHostingModule>();
                    k.Rebind<ServiceArguments>().ToConstant(serviceArguments);
                    k.Rebind<IServiceInterfaceMapper>().ToConstant(new IdentityServiceInterfaceMapper(typeof(IDemoService)));
                },
                mockConfig);
        }

      
        [Test]
        public void Slot_FromCommandLine_Working()
        {
            var kernel = SetUpKernel(new ServiceArguments(slotNumber: 5));
            var serviceEndpointDefinition = kernel.Get<IServiceEndPointDefinition>();

            serviceEndpointDefinition.HttpPort.ShouldBe(40005);
            serviceEndpointDefinition.SiloGatewayPort.ShouldBe(41005);
            serviceEndpointDefinition.SiloNetworkingPort.ShouldBe(42005);
            serviceEndpointDefinition.SiloNetworkingPortOfPrimaryNode.ShouldBe(42001);
            ((IMetricsSettings)serviceEndpointDefinition).MetricsPort.ShouldBe(43005);
        }

        [Test]
        public void DefaultSlot_Working()
        {
            
            var kernel = SetUpKernel(new ServiceArguments());

            var serviceEndpointDefinition = kernel.Get<IServiceEndPointDefinition>();

            serviceEndpointDefinition.HttpPort.ShouldBe(40001);
            serviceEndpointDefinition.SiloGatewayPort.ShouldBe(41001);
            serviceEndpointDefinition.SiloNetworkingPort.ShouldBe(42001);
            serviceEndpointDefinition.SiloNetworkingPortOfPrimaryNode.ShouldBe(42001);
            ((IMetricsSettings)serviceEndpointDefinition).MetricsPort.ShouldBe(43001);
        }

        [Test]
        public void MissingDefaultSlot_Throws()
        {
            var kernel = SetUpKernel(new ServiceArguments(slotNumber: 5),withDefault:false);

            Action act = () =>  {
                             kernel.Get<IServiceEndPointDefinition>();
                         };

            act.ShouldThrow<ConfigurationException>().Message.ShouldContain("DefaultSlotNumber is not set in configuration");
        }

        [Test]        
        public void IsSlotModeFlag_Working()
        {
             int basePort = 5555;
            var kernel = SetUpKernel(new ServiceArguments(){SiloNetworkingPortOfPrimaryNode = basePort},false);

            var serviceEndpointDefinition = kernel.Get<IServiceEndPointDefinition>();

            serviceEndpointDefinition.HttpPort.ShouldBe(basePort);
            serviceEndpointDefinition.SiloNetworkingPortOfPrimaryNode.ShouldBe(serviceEndpointDefinition.SiloNetworkingPort);            

            serviceEndpointDefinition.SiloGatewayPort.ShouldBe(basePort+1);
            serviceEndpointDefinition.SiloNetworkingPort.ShouldBe(basePort+2);
            ((IMetricsSettings)serviceEndpointDefinition).MetricsPort.ShouldBe(basePort+3);
        }

    }
}
