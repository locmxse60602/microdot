﻿using Gigya.Microdot.Configuration;
using Gigya.Microdot.Hosting;
using Gigya.Microdot.Hosting.HttpService;
using Gigya.Microdot.Hosting.Service;
using Gigya.Microdot.Interfaces.Configuration;
using Gigya.Microdot.Interfaces.Logging;
using Gigya.Microdot.Interfaces.SystemWrappers;
using Gigya.Microdot.SharedLogic;
using Gigya.Microdot.SharedLogic.Measurement.Workload;
using System.ServiceProcess;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Gigya.Microdot.Hosting.Environment;
using Gigya.Microdot.Interfaces;

namespace Gigya.Microdot.Ninject.Host
{
    public sealed class HostEventArgs : EventArgs
    {

    }

    public sealed class ConfigurationVerificationResult
    {

    }

    public abstract class ServiceHostBase : IDisposable
    {
        private bool disposed;
        private object syncRoot = new object();
        
        public event EventHandler<HostEventArgs> OnStarting = (o, a) => { };
        public event EventHandler<HostEventArgs> OnStarted  = (o, a) => { };
        public event EventHandler<HostEventArgs> OnStopping = (o, a) => { };
        public event EventHandler<HostEventArgs> OnStopped  = (o, a) => { };
        public event EventHandler<HostEventArgs> OnCrashing = (o, a) => { };
        public event EventHandler<HostEventArgs> OnCrashed  = (o, a) => { };

        public ServiceArguments Arguments { get; private set; }

        private DelegatingServiceBase WindowsService { get; set; }
        private ManualResetEvent StopEvent { get; }
        protected TaskCompletionSource<object> ServiceStartedEvent { get; set; }
        private TaskCompletionSource<StopResult> ServiceGracefullyStopped { get; set; }
        private Process MonitoredShutdownProcess { get; set; }
        protected ICrashHandler CrashHandler { get; set; }

        public HostEnvironment HostEnvironment { get; }
        public Version InfraVersion { get; }

        private IRequestListener requestListener;


        public ServiceHostBase(
            HostEnvironment environment,
            Version infraVersion)
        {
            if (IntPtr.Size != 8)
                throw new Exception("You must run in 64-bit mode. Please make sure you unchecked the 'Prefer 32-bit' checkbox from the build section of the project properties.");


            StopEvent = new ManualResetEvent(true);
            ServiceStartedEvent = new TaskCompletionSource<object>();
            ServiceGracefullyStopped = new TaskCompletionSource<StopResult>();
            ServiceGracefullyStopped.SetResult(StopResult.None);

            this.HostEnvironment = environment ?? throw new ArgumentNullException(nameof(environment));
            //this.HandlingPipeline = handlingPipeline ?? throw new ArgumentNullException(nameof(handlingPipeline));
            //this.RequestListener = requestListener ?? throw new ArgumentNullException(nameof(requestListener));
            this.InfraVersion = infraVersion ?? throw new ArgumentNullException(nameof(infraVersion));
        }

        private HostEventArgs CreateEventArgs()
            => new HostEventArgs();



        protected virtual void OnStop()
        {
           
        }

        /// <summary>
        /// Start the service, auto detecting between Windows service and command line. Always blocks until service is stopped.
        /// </summary>
        public void Run(ServiceArguments argumentsOverride = null)
        {
            ServiceGracefullyStopped = new TaskCompletionSource<StopResult>();
            Arguments = argumentsOverride ?? new ServiceArguments(Environment.GetCommandLineArgs().Skip(1).ToArray());



            if (Arguments.ServiceStartupMode == ServiceStartupMode.WindowsService)
            {
                Trace.WriteLine("Service starting as a Windows service...");
                WindowsService = new DelegatingServiceBase(this.HostEnvironment.ApplicationInfo.Name, OnWindowsServiceStart, OnWindowsServiceStop);

                if (argumentsOverride == null)
                    Arguments = null; // Ensures OnWindowsServiceStart reloads parameters passed from Windows Service Manager.

                ServiceBase.Run(WindowsService); // This calls OnWindowsServiceStart() on a different thread and blocks until the service stops.
            }
            else if (Arguments.ServiceStartupMode == ServiceStartupMode.VerifyConfigurations)
            {
                OnVerifyConfiguration();
            }
            else
            {
                if (Arguments.ShutdownWhenPidExits != null)
                {
                    try
                    {
                        MonitoredShutdownProcess = Process.GetProcessById(Arguments.ShutdownWhenPidExits.Value);
                    }
                    catch (ArgumentException e)
                    {
                        Console.WriteLine($"Service cannot start because monitored PID {Arguments.ShutdownWhenPidExits} is not running. Exception: {e}");
                        Environment.ExitCode = 1;
                        ServiceGracefullyStopped.SetResult(StopResult.None);
                        return;
                    }

                    Console.WriteLine($"Will perform graceful shutdown when PID {Arguments.ShutdownWhenPidExits} exits.");
                    MonitoredShutdownProcess.Exited += (s, a) =>
                    {
                        Console.WriteLine($"PID {Arguments.ShutdownWhenPidExits} has exited, shutting down...");
                        Stop();
                    };

                    MonitoredShutdownProcess.EnableRaisingEvents = true;
                }

                try
                {
                    OnStart();
                }
                catch (Exception e)
                {
                    ServiceStartedEvent.TrySetException(e);

                    throw;
                }

                if (Arguments.ServiceStartupMode == ServiceStartupMode.CommandLineInteractive)
                {
                    Thread.Sleep(10); // Allow any startup log messages to flush to Console.

                    Console.Title = this.HostEnvironment.ApplicationInfo.Name;

                    if (Arguments.ConsoleOutputMode == ConsoleOutputMode.Color)
                    {
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.Write("Service initialized in interactive mode (command line). Press ");
                        Console.ForegroundColor = ConsoleColor.White;
                        Console.BackgroundColor = ConsoleColor.DarkCyan;
                        Console.Write("[Alt+S]");
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.BackgroundColor = ConsoleColor.Black;
                        Console.WriteLine(" to stop the service gracefully.");
                        Console.ForegroundColor = ConsoleColor.Gray;
                    }
                    else
                    {
                        Console.WriteLine("Service initialized in interactive mode (command line). Press [Alt+S] to stop the service gracefully.");
                    }

                    Task.Factory.StartNew(() =>
                    {
                        while (true)
                        {
                            var key = Console.ReadKey(true);

                            if (key.Key == ConsoleKey.S && key.Modifiers == ConsoleModifiers.Alt)
                            {
                                Stop();
                                break;
                            }
                        }
                    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                }
                else
                {
                    Console.WriteLine("Service initialized in non-interactive mode (command line). Waiting for stop request...");
                }

                StopEvent.Reset();
                ServiceStartedEvent.SetResult(null);
                StopEvent.WaitOne();

                Console.WriteLine("   ***   Shutting down...   ***   ");

                var maxShutdownTime = TimeSpan.FromSeconds((Arguments.OnStopWaitTimeSec ?? 0) + (Arguments.ServiceDrainTimeSec ?? 0));
                bool isServiceGracefullyStopped = Task.Run(() => OnStop()).Wait(maxShutdownTime);

                if (isServiceGracefullyStopped == false)
                    Console.WriteLine($"   ***  Service failed to stop gracefully in the allotted time ({maxShutdownTime}), continuing with forced shutdown.   ***   ");

                ServiceStartedEvent = new TaskCompletionSource<object>();

                ServiceGracefullyStopped.SetResult(isServiceGracefullyStopped ? StopResult.Graceful : StopResult.Force);
                MonitoredShutdownProcess?.Dispose();

                if (Arguments.ServiceStartupMode == ServiceStartupMode.CommandLineInteractive)
                {
                    if (Arguments.ConsoleOutputMode == ConsoleOutputMode.Color)
                    {
                        Console.BackgroundColor = ConsoleColor.White;
                        Console.ForegroundColor = ConsoleColor.Black;
                        Console.WriteLine("   ***   Shutdown complete. Press any key to exit.   ***   ");
                        Console.BackgroundColor = ConsoleColor.Black;
                        Console.ForegroundColor = ConsoleColor.Gray;
                    }
                    else
                    {
                        Console.WriteLine("   ***   Shutdown complete. Press any key to exit.   ***   ");
                    }

                    Console.ReadKey(true);
                }
            }
        }

        protected abstract void OnVerifyConfiguration();

        protected void VerifyConfiguration(ConfigurationVerificator ConfigurationVerificator)
        { 
            if (ConfigurationVerificator == null)
            {
                Environment.ExitCode = 2;
                Console.Error.WriteLine("ERROR: The configuration verification is not properly implemented. " +
                                        "To implement you need to override OnVerifyConfiguration base method and call to base.");
            }
            else
            {
                try
                {
                    var results = ConfigurationVerificator.Verify();
                    Environment.ExitCode = results.All(r => r.Success) ? 0 : 1;

                    if (Arguments.ConsoleOutputMode == ConsoleOutputMode.Color)
                    {
                        var (restoreFore, restoreBack) = (Console.ForegroundColor, Console.BackgroundColor);
                        foreach (var result in results)
                        {
                            Console.BackgroundColor = result.Success ? ConsoleColor.Black : ConsoleColor.White;
                            Console.ForegroundColor = result.Success ? ConsoleColor.White : ConsoleColor.Red;
                            Console.WriteLine(result);
                        }
                        Console.BackgroundColor = restoreBack;
                        Console.ForegroundColor = restoreFore;
                    }
                    else
                    {
                        foreach (var result in results)
                            Console.WriteLine(result);
                    }
                    // Avoid extra messages in machine to machine mode or when disabled
                    if (!(Console.IsOutputRedirected || Arguments.ConsoleOutputMode == ConsoleOutputMode.Disabled))
                        Console.WriteLine("   ***   Shutting down [configuration verification mode].   ***   ");
                }
                catch (Exception ex)
                {
                    Environment.ExitCode = 3;
                    Console.Error.WriteLine(ex.Message);
                    Console.Error.WriteLine(ex.StackTrace);
                }
            }
        }

        /// <summary>
        /// Waits for the service to finish starting. Mainly used from tests.
        /// </summary>
        public Task WaitForServiceStartedAsync()
        {
            return ServiceStartedEvent.Task;
        }

        public Task<StopResult> WaitForServiceGracefullyStoppedAsync()
        {
            return ServiceGracefullyStopped.Task;
        }


        /// <summary>
        /// Signals the service to stop.
        /// </summary>
        public virtual void Stop()
        {
            StopEvent.Set();
        }

        protected abstract void OnStart();

        protected void OnCrash()
        {
            this.OnCrashing(this, this.CreateEventArgs());
            Stop();
            WaitForServiceGracefullyStoppedAsync().Wait(5000);
            Dispose();
            this.OnCrashed(this, this.CreateEventArgs());
        }


        private void OnWindowsServiceStart(string[] args)
        {
            if (Arguments == null)
            {
                Arguments = new ServiceArguments(args);
            }

            try
            {
                if (Arguments.ServiceStartupMode != ServiceStartupMode.WindowsService)
                    throw new InvalidOperationException($"Cannot start in {Arguments.ServiceStartupMode} mode when starting as a Windows service.");

                if (Environment.UserInteractive == false)
                {
                    throw new InvalidOperationException(
                        "This Windows service requires to be run with 'user interactive' enabled to correctly read certificates. " +
                        "Either the service wasn't configure with the 'Allow service to interact with desktop' option enabled " +
                        "or the OS is ignoring the checkbox due to a registry settings. " +
                        "Make sure both the checkbox is checked and following registry key is set to DWORD '0':\n" +
                        @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Windows\NoInteractiveServices");
                }

                WindowsService.RequestAdditionalTime(60000);

                OnStart();
            }
            catch
            {
                WindowsService.ExitCode = 1064; // "An exception occurred in the service when handling the control request." (net helpmsg 1064)
                throw;
            }
        }


        private void OnWindowsServiceStop()
        {
            WindowsService.RequestAdditionalTime(60000);

            try
            {
                OnStop();
            }
            catch
            {
                WindowsService.ExitCode = 1064; // "An exception occurred in the service when handling the control request." (net helpmsg 1064)
                throw;
            }

        }


        protected virtual void Dispose(bool disposing)
        {
            SafeDispose(StopEvent);
            SafeDispose(WindowsService);
            SafeDispose(MonitoredShutdownProcess);
        }


        public void Dispose()
        {
            lock (this.syncRoot)
            {
                try
                {
                    if (this.disposed)
                        return;
                    
                    Dispose(false);
                }

                finally
                {
                    this.disposed = true;
                }
            }
        }

        protected void SafeDispose(IDisposable disposable)
        {
            try
            {
                disposable?.Dispose();
            }
            catch (Exception e)
            {
                Trace.TraceError(e.ToString());
            }
        }


        private class DelegatingServiceBase : ServiceBase
        {
            private readonly Action<string[]> _onStart;
            private readonly Action _onStop;


            public DelegatingServiceBase(string serviceName, Action<string[]> onStart, Action onStop)
            {
                ServiceName = serviceName; // Required for auto-logging to event viewer of start/stop event and exceptions.
                _onStart = onStart;
                _onStop = onStop;
            }


            protected override void OnStart(string[] args)
            {
                _onStart(args);
            }


            protected override void OnStop()
            {
                _onStop();
            }
        }
    }
}
