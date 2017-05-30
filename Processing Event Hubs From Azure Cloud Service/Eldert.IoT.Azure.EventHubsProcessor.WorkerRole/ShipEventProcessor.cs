using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;

using Eldert.IoT.Data.DataTypes;

using Microsoft.Azure;
using Microsoft.ServiceBus.Messaging;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Table;

using Newtonsoft.Json;

namespace Eldert.IoT.Azure.EventHubsProcessor.WorkerRole
{
    internal class ShipEventProcessor : IEventProcessor
    {
        /// <summary>
        /// Counter used to decide if we have to set a checkpoint
        /// </summary>
        private static int _messageCounter;

        /// <summary>
        /// Azure Table Storage where we want to save our data retrieved from Event Hubs.
        /// </summary>
        private readonly CloudTable _table = CloudStorageAccount.Parse(CloudConfigurationManager.GetSetting("IoTStorageConnectionString")).CreateCloudTableClient().GetTableReference("EngineInformation");

        /// <summary>
        /// Event processor has been started for a partition.
        /// </summary>
        public Task OpenAsync(PartitionContext context)
        {
            // Create the table if it doesn't exist yet
            if (_table.CreateIfNotExists())
            {
                Trace.TraceInformation("Table for EngineInformation has been created.");
            }

            Trace.TraceInformation($"EventProcessor started.  Partition: {context.Lease.PartitionId}, Current offset: {context.Lease.Offset}");

            return Task.FromResult<object>(null);
        }

        /// <summary>
        /// Processing of the messages received on the event hub.
        /// </summary>
        public async Task ProcessEventsAsync(PartitionContext context, IEnumerable<EventData> messages)
        {
            try
            {
                // Gather the table operations to be performed
                var tableOperations = new TableBatchOperation();

                // Loop through received messages
                foreach (var eventData in messages)
                {
                    // Get the message received as a JSON string
                    var message = Encoding.UTF8.GetString(eventData.GetBytes());
                    Trace.TraceInformation($"Message received - Partition: {context.Lease.PartitionId} - Machine: {Environment.MachineName} Message: {message}");

                    try
                    {
                        // Deserialize the JSON message to an engine information object
                        var engineInformation = JsonConvert.DeserializeObject<EngineInformation>(message);
                        engineInformation.SetKeys();
                        
                        // We have to take care of idempotency ourself, as we might get the same message multiple times
                        // To do so, we will insert new items, and replace existing items (which will replace the item with the same item)
                        // For extra performance we gather the table operations here, and will apply them later on in a batch
                        Trace.TraceInformation($"Adding {engineInformation.Identifier} to table");
                        tableOperations.Add(TableOperation.InsertOrReplace(engineInformation));
                    }
                    catch (Exception)
                    {
                        Trace.TraceWarning("Could not deserialize as EngineInformation object");
                    }
                }

                // Apply table operations if needed
                if (tableOperations.Count > 0)
                {
                    await _table.ExecuteBatchAsync(tableOperations);
                    Trace.TraceInformation("Saved data to database");
                }

                // Keep track of number of received messages, place a checkpoint after having processed 50 messages.
                // Make sure the messages are persisted in the table before placing the checkpoint
                if (++_messageCounter > 50)
                {
                    Trace.TraceInformation("Checkpoint placed");
                    await context.CheckpointAsync();
                    _messageCounter = 0;
                }
            }
            catch (Exception exception)
            {
                Trace.TraceError(exception.ToString());
                throw;
            }
        }

        public async Task CloseAsync(PartitionContext context, CloseReason reason)
        {
            Trace.TraceInformation($"EventProcessor is shutting down: Partition: {context.Lease.PartitionId} Reason: {reason}");

            // Place a checkpoint in case the event processor is shutting down
            if (reason == CloseReason.Shutdown)
            {
                await context.CheckpointAsync();
            }
        }
    }
}