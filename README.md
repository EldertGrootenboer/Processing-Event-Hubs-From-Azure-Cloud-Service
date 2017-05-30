# Processing Event Hubs From Azure Cloud Service
This sample will show how you can use an Azure cloud service as a worker role for retrieving data from Event Hubs using the Event Processor Host library. We will save the retrieved data in an Azure Table Storage, which is a great service for working with large amounts of structured, non-relational data. Azure Table Storage is very fast, and cost efficient especially when working with lots of data, which makes it ideal for our scenario. For the full scenario and details with this sample you can check [this blogpost](https://blog.eldert.net/iot-integration-of-things-processing-event-hubs-from-azure-cloud-service/).

## Description
The Event Processor Host library will be used to retrieve the data from our event hub, and load it into Azure Table Storage. In my blogpost more details can be found on how to setup the necesarry Azure entities. The Event Processor Host library will distribute Event Hubs partitions accross our instances of the worker role, keeping track of leases and snapshots. This library really makes working with Event Hubs from .NET code a breeze to go through. When working with Azure Table Storage, we need a class which represents the object which will be saved to the table. This class should derive from the TableEntity class, so it can be mapped into Table Storage. We will start with creating a Class Library project for this.

```csharp
using Microsoft.WindowsAzure.Storage.Table; 
 
namespace Eldert.IoT.Data.DataTypes 
{ 
    /// <summary> 
    /// Represents an engine information object for Azure Table Storage. 
    /// </summary> 
    public class EngineInformation : TableEntity 
    { 
        public Guid Identifier { get; set; } 
         
        public string ShipName { get; set; } 
         
        public string EngineName { get; set; } 
 
        public double Temperature { get; set; } 
 
        public double RPM { get; set; } 
 
        public bool Warning { get; set; } 
 
        public int EngineWarning { get; set; } 
 
        public DateTime CreatedDateTime { get; set; } 
 
        public void SetKeys() 
        { 
            PartitionKey = ShipName; 
            RowKey = Identifier.ToString(); 
        } 
    } 
}
```

Next we are going to create a Cloud Service project containing a worker role, which we’re going to publish to Azure later on.

![](https://code.msdn.microsoft.com/site/view/file/148468/1/Capture.PNG)

When the project is created, we will have two projects, one for the worker role, the other for the cloud service itself. Let’s start by setting up our worker role. As mentioned, we will implement the event processor library. For this we will create a new class, which implements the IEventProcessor interface.

```C#
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
 
internal class ShipEventProcessor : IEventProcessor 
{ 
 
 
}
``` 

This interface implements three methods. The first is OpenAsync, which gets called when the event processors are started for the partitions in our event hub. In this call we will also create the table to which we want to save our data in case it does not yet exist.

```C#
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
```

The second one is CloseAsync, which is called when the event processor is being shutdown. We will set a checkpoint here in case we are shutting down, so next time the event processor is started up for this partition, processing will resume from the last processed message.

```C#
public async Task CloseAsync(PartitionContext context, CloseReason reason) 
{ 
    Trace.WriteLine($"EventProcessor is shutting down: Partition: {context.Lease.PartitionId} Reason: {reason}"); 
 
    // Place a checkpoint in case the event processor is shutting down 
    if (reason == CloseReason.Shutdown) 
    { 
        await context.CheckpointAsync(); 
    } 
}
```

The last method is ProcessEventsAsync, which is where the processing of the messages that are being received is done. We will be receiving UTF8 serialized JSON strings, so we will use the JSON library from Newtonsoft to deserialize the messages. We will loop through the messages we received from our event hub, get the message string, and deserialize it to an EngineInformation object. Because event hubs works in a streaming manner, where we set checkpoints every x messages, we could receive messages more then once. To handle this, we will have to have some idempotency in place. I choose to just replace the existing object in the database, but for different scenarios this is something to think about.

```C#
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
```

We are now done with implementing the interface, the next step is to hook it into our WorkerRole class.

```C#
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
        . 
        . 
        . 
    } 
}
```

When the worker role starts, we want the Event Processor Host to be initialized, so we will add this to the OnStart method. We need to set an identifier for the host, which will be used by the library to keep track which host is handling each partition. This will be used in loadbalancing the processing of the partitions, as each partition can be handled by a different host. We also need to specify the connection settings, which we just stored in the settings of the cloud service. Using CloudConfigurationManager.GetSetting() we can retrieve the settings to create an instance of the event processor host.

```C#
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
```

When the application starts running, we have to register the ShipEventProcessor we created earlier with the Event Processor Host, so it knows what actions to take on the messages. We’ll add this to the Run method.

```C#
public override void Run() 
{ 
    Trace.TraceInformation("Eldert.IoT.Azure.EventHubsProcessor.WorkerRole is running"); 
 
    try 
    { 
        // Register the processor, at this point one or more partitions will be registered with the client, and it will start processing 
        _eventProcessorHost.RegisterEventProcessorAsync().Wait(); 
 
        RunAsync(_cancellationTokenSource.Token).Wait(); 
    } 
    finally 
    { 
        _runCompleteEvent.Set(); 
    } 
}
```

And finally, when the application is being stopped, we have to unregister the event processor, to let it clean up and make the necesarry checkpoints. This should be added in the OnStop method.

```C#
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
```

That's it, the application can now be deployed to Azure, where it will start receiving our messages from Event Hubs and storing it in our Azure Table Storage.

## More Information
Details on how to publish can be found in [my blogpost](https://blog.eldert.net/iot-integration-of-things-processing-event-hubs-from-azure-cloud-service/), along with more information on how to setup the logging. This sample is part of a series I have written around IoT, which can be found on my blog.
