using System;
using System.Diagnostics;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Azure;
using Microsoft.WindowsAzure.ServiceRuntime;

using Microsoft.ServiceBus.Messaging;

namespace Eldert.IoT.Azure.EventHubsProcessor.WorkerRole
{
    public class WorkerRole : RoleEntryPoint
    {
        private EventProcessorHost _eventProcessorHost;

        private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
        private readonly ManualResetEvent _runCompleteEvent = new ManualResetEvent(false);
        
        public override void Run()
        {
            Trace.TraceInformation("Eldert.IoT.Azure.EventHubsProcessor.WorkerRole is running");

            try
            {
                // Register the processor, at this point one or more partitions will be registered with the client, and it will start processing
                _eventProcessorHost.RegisterEventProcessorAsync<ShipEventProcessor>().Wait();

                RunAsync(_cancellationTokenSource.Token).Wait();
            }
            finally
            {
                _runCompleteEvent.Set();
            }
        }

        public override bool OnStart()
        {
            // Create the event processor host
            // Create a unique identifier, which will be used to determine which partitions this processor is handling
            _eventProcessorHost = new EventProcessorHost(Guid.NewGuid().ToString(), CloudConfigurationManager.GetSetting("EventHub"), CloudConfigurationManager.GetSetting("ConsumerGroup"),
                CloudConfigurationManager.GetSetting("EventHubsConnectionString"), CloudConfigurationManager.GetSetting("IoTStorageConnectionString"));

            // Set the maximum number of concurrent connections
            ServicePointManager.DefaultConnectionLimit = 12;
            var result = base.OnStart();
            Trace.TraceInformation("Eldert.IoT.Azure.EventHubsProcessor.WorkerRole has been started");
            return result;
        }

        public override void OnStop()
        {
            Trace.TraceInformation("Eldert.IoT.Azure.EventHubsProcessor.WorkerRole is stopping");
            _cancellationTokenSource.Cancel();
            _runCompleteEvent.WaitOne();

            // Unregister the processor
            _eventProcessorHost.UnregisterEventProcessorAsync().Wait();

            base.OnStop();
            Trace.TraceInformation("Eldert.IoT.Azure.EventHubsProcessor.WorkerRole has stopped");
        }

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(1000, cancellationToken);
            }
        }
    }
}