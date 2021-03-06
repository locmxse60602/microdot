#region Copyright 
// Copyright 2017 Gigya Inc.  All rights reserved.
// 
// Licensed under the Apache License, Version 2.0 (the "License"); 
// you may not use this file except in compliance with the License.  
// You may obtain a copy of the License at
// 
//     http://www.apache.org/licenses/LICENSE-2.0
// 
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDER AND CONTRIBUTORS "AS IS"
// AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
// IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
// ARE DISCLAIMED.  IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE
// LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR
// CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF
// SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS
// INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN
// CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
// ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE
// POSSIBILITY OF SUCH DAMAGE.
#endregion

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Gigya.Microdot.Common.Tests;
using Gigya.Microdot.Fakes.KernelUtils;
using Gigya.Microdot.Hosting.Environment;
using Gigya.Microdot.Hosting.Validators;
using Gigya.Microdot.Ninject;
using Gigya.Microdot.Orleans.Hosting.UnitTests.Microservice.WarmupTestService;
using Gigya.Microdot.Orleans.Ninject.Host;
using Gigya.Microdot.SharedLogic;
using Gigya.Microdot.SharedLogic.HttpService;
using Ninject;
using Orleans;

namespace Gigya.Microdot.Orleans.Hosting.UnitTests.Microservice.CalculatorService
{
    public class CalculatorServiceHost : MicrodotOrleansServiceHost
    {
        public override ILoggingModule GetLoggingModule()
        {
            return new FakesLoggersModules();
        }

        public IKernel Kernel;

        public CalculatorServiceHost() : base(new HostEnvironment(new TestHostEnvironmentSource()), new Version())
        {
        }

        protected override void PreConfigure(IKernel kernel, ServiceArguments Arguments)
        {
            base.PreConfigure(kernel, Arguments);
            kernel.Rebind<ServiceValidator>().To<MockServiceValidator>().InSingletonScope();
            kernel.Rebind<ISingletonDependency>().To<SingletonDependency>().InSingletonScope();
            Func<GrainLoggingConfig> writeGrainLog = () => new GrainLoggingConfig{LogMicrodotGrains = true, LogRatio = 1, LogServiceGrains = true, LogOrleansGrains = true};
            kernel.Rebind<Func<GrainLoggingConfig>>().ToConstant(writeGrainLog);
            kernel.Rebind<ICertificateLocator>().To<DummyCertificateLocator>().InSingletonScope();
            kernel.RebindForTests();
            Kernel = kernel;

        }

        protected override Task AfterOrleansStartup(IGrainFactory grainFactory)
        {
            if (grainFactory == null) throw new NullReferenceException("AfterOrleansStartup no grainFactory");
            return base.AfterOrleansStartup(grainFactory);
        }

        public class MockServiceValidator : ServiceValidator
        {

            public MockServiceValidator()
                : base(new List<IValidator>().ToArray())
            {

            }
        }
    }
}