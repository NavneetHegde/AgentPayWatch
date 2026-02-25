# Phase 3: Event Backbone -- Service Bus Integration

**Goal:** Events flow through Azure Service Bus topics. Verifiable via Aspire distributed traces.

**Prerequisite:** Phase 2 complete -- Cosmos DB repositories are working, API CRUD endpoints respond correctly.

---

## Section 1: NuGet Packages

Add the Aspire Service Bus component to Infrastructure.

### Updated Infrastructure .csproj

**File:** `src/AgentPayWatch.Infrastructure/AgentPayWatch.Infrastructure.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>AgentPayWatch.Infrastructure</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Aspire.Microsoft.Azure.Cosmos" Version="10.1.0" />
    <PackageReference Include="Aspire.Azure.Messaging.ServiceBus" Version="10.1.0" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\AgentPayWatch.Domain\AgentPayWatch.Domain.csproj" />
  </ItemGroup>

</Project>
```

---

## Section 2: Service Bus Messaging

### 2.1 Topic Names

**File:** `src/AgentPayWatch.Infrastructure/Messaging/TopicNames.cs`

```csharp
namespace AgentPayWatch.Infrastructure.Messaging;

public static class TopicNames
{
    public const string ProductMatchFound = "product-match-found";
    public const string ApprovalDecided = "approval-decided";
    public const string PaymentCompleted = "payment-completed";
    public const string PaymentFailed = "payment-failed";
}
```

### 2.2 Service Bus Event Publisher

**File:** `src/AgentPayWatch.Infrastructure/Messaging/ServiceBusEventPublisher.cs`

```csharp
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentPayWatch.Domain.Interfaces;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;

namespace AgentPayWatch.Infrastructure.Messaging;

public sealed class ServiceBusEventPublisher : IEventPublisher, IAsyncDisposable
{
    private readonly ServiceBusClient _client;
    private readonly ILogger<ServiceBusEventPublisher> _logger;
    private readonly ConcurrentDictionary<string, ServiceBusSender> _senders = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public ServiceBusEventPublisher(
        ServiceBusClient client,
        ILogger<ServiceBusEventPublisher> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task PublishAsync<T>(T message, string topicName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(topicName);

        var sender = _senders.GetOrAdd(topicName, name => _client.CreateSender(name));

        var json = JsonSerializer.Serialize(message, JsonOptions);

        var serviceBusMessage = new ServiceBusMessage(json)
        {
            ContentType = "application/json",
            Subject = typeof(T).Name
        };

        // Extract MessageId and CorrelationId from the message if they exist
        if (message is not null)
        {
            var messageIdProperty = typeof(T).GetProperty("MessageId");
            if (messageIdProperty?.GetValue(message) is Guid messageId)
            {
                serviceBusMessage.MessageId = messageId.ToString();
            }

            var correlationIdProperty = typeof(T).GetProperty("CorrelationId");
            if (correlationIdProperty?.GetValue(message) is Guid correlationId)
            {
                serviceBusMessage.CorrelationId = correlationId.ToString();
            }
        }

        _logger.LogInformation(
            "Publishing {EventType} to topic '{TopicName}'. MessageId={MessageId}, CorrelationId={CorrelationId}",
            typeof(T).Name,
            topicName,
            serviceBusMessage.MessageId,
            serviceBusMessage.CorrelationId);

        await sender.SendMessageAsync(serviceBusMessage, ct);

        _logger.LogInformation(
            "Successfully published {EventType} to topic '{TopicName}'.",
            typeof(T).Name,
            topicName);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var sender in _senders.Values)
        {
            await sender.DisposeAsync();
        }

        _senders.Clear();
    }
}
```

---

## Section 3: Updated DI Registration

**File:** `src/AgentPayWatch.Infrastructure/DependencyInjection.cs`

```csharp
using AgentPayWatch.Domain.Interfaces;
using AgentPayWatch.Infrastructure.Cosmos;
using AgentPayWatch.Infrastructure.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AgentPayWatch.Infrastructure;

public static class DependencyInjection
{
    public static IHostApplicationBuilder AddInfrastructureServices(
        this IHostApplicationBuilder builder)
    {
        // Cosmos DB
        builder.AddAzureCosmosClient("cosmos");

        builder.Services.AddScoped<IWatchRequestRepository, CosmosWatchRequestRepository>();
        builder.Services.AddScoped<IProductMatchRepository, CosmosProductMatchRepository>();
        builder.Services.AddScoped<IApprovalRepository, CosmosApprovalRepository>();
        builder.Services.AddScoped<IPaymentTransactionRepository, CosmosPaymentTransactionRepository>();

        builder.Services.AddHostedService<CosmosDbInitializer>();

        // Service Bus
        builder.AddAzureServiceBusClient("messaging");

        builder.Services.AddSingleton<IEventPublisher, ServiceBusEventPublisher>();

        return builder;
    }
}
```

---

## Section 4: AppHost -- Add Service Bus Emulator

### Updated apphost.cs

**File:** `appHost/apphost.cs`

```csharp
#:sdk  Aspire.AppHost.Sdk@13.1.1
#:project ../src/AgentPayWatch.ServiceDefaults
#:project ../src/AgentPayWatch.Domain
#:project ../src/AgentPayWatch.Infrastructure
#:project ../src/AgentPayWatch.Api
#:project ../src/AgentPayWatch.Agents.ProductWatch
#:project ../src/AgentPayWatch.Agents.Approval
#:project ../src/AgentPayWatch.Agents.Payment
#:project ../src/AgentPayWatch.Web

var builder = DistributedApplication.CreateBuilder(args);

// --- Data ---
var cosmos = builder.AddAzureCosmosDB("cosmos")
    .RunAsEmulator()
    .AddDatabase("agentpaywatch");

// --- Messaging ---
var messaging = builder.AddAzureServiceBus("messaging")
    .RunAsEmulator(emulator =>
    {
        emulator.WithTopics(
            TopicNames.ProductMatchFound,
            TopicNames.ApprovalDecided,
            TopicNames.PaymentCompleted,
            TopicNames.PaymentFailed);
    })
    .AddTopic(TopicNames.ProductMatchFound, topic =>
    {
        topic.AddSubscription("approval-agent");
    })
    .AddTopic(TopicNames.ApprovalDecided, topic =>
    {
        topic.AddSubscription("payment-agent");
    })
    .AddTopic(TopicNames.PaymentCompleted, topic =>
    {
        topic.AddSubscription("notification-handler");
    })
    .AddTopic(TopicNames.PaymentFailed, topic =>
    {
        topic.AddSubscription("notification-handler");
    });

// --- Services ---
var api = builder.AddProject<Projects.AgentPayWatch_Api>("api")
    .WithReference(cosmos)
    .WaitFor(cosmos)
    .WithReference(messaging)
    .WaitFor(messaging);

var productWatchAgent = builder.AddProject<Projects.AgentPayWatch_Agents_ProductWatch>("product-watch-agent")
    .WithReference(cosmos)
    .WaitFor(cosmos)
    .WithReference(messaging)
    .WaitFor(messaging);

var approvalAgent = builder.AddProject<Projects.AgentPayWatch_Agents_Approval>("approval-agent")
    .WithReference(cosmos)
    .WaitFor(cosmos)
    .WithReference(messaging)
    .WaitFor(messaging);

var paymentAgent = builder.AddProject<Projects.AgentPayWatch_Agents_Payment>("payment-agent")
    .WithReference(cosmos)
    .WaitFor(cosmos)
    .WithReference(messaging)
    .WaitFor(messaging);

var web = builder.AddProject<Projects.AgentPayWatch_Web>("web");

builder.Build().Run();

// --- Static import for topic name constants ---
public static class TopicNames
{
    public const string ProductMatchFound = "product-match-found";
    public const string ApprovalDecided = "approval-decided";
    public const string PaymentCompleted = "payment-completed";
    public const string PaymentFailed = "payment-failed";
}
```

> **Note:** The `TopicNames` class is declared at the bottom of `apphost.cs` so that the `#:sdk`-based file-scoped program can reference the constants directly. This mirrors the constants in `Infrastructure/Messaging/TopicNames.cs` but keeps the AppHost self-contained. If the Aspire Service Bus `RunAsEmulator` overload does not accept `WithTopics`, use the `AddTopic` fluent API alone -- the topics and subscriptions will be created by the emulator automatically.

---

## Section 5: Debug Endpoint (Temporary)

This endpoint exists solely to validate Service Bus connectivity end-to-end. It publishes a fake `ProductMatchFound` event to the Service Bus topic.

### Debug Endpoints File

**File:** `src/AgentPayWatch.Api/Endpoints/DebugEndpoints.cs`

```csharp
// ============================================================
// TEMPORARY: Remove this file in Phase 4.
// This endpoint exists to validate Service Bus connectivity.
// ============================================================

using AgentPayWatch.Domain.Events;
using AgentPayWatch.Domain.Interfaces;
using AgentPayWatch.Infrastructure.Messaging;

namespace AgentPayWatch.Api.Endpoints;

public static class DebugEndpoints
{
    public static IEndpointRouteBuilder MapDebugEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/debug")
            .WithTags("Debug");

        group.MapPost("/publish-test-event", PublishTestEvent)
            .WithName("PublishTestEvent")
            .WithDescription("Publishes a fake ProductMatchFound event to Service Bus for connectivity testing.");

        return routes;
    }

    private static async Task<IResult> PublishTestEvent(
        IEventPublisher publisher,
        CancellationToken ct)
    {
        var watchRequestId = Guid.NewGuid();
        var matchId = Guid.NewGuid();

        var testEvent = new ProductMatchFound(
            MessageId: Guid.NewGuid(),
            CorrelationId: watchRequestId,
            Timestamp: DateTimeOffset.UtcNow,
            Source: "debug-endpoint",
            MatchId: matchId,
            ProductName: "Test Product - PlayStation 5",
            Price: 449.99m,
            Currency: "USD",
            Seller: "TestSeller");

        await publisher.PublishAsync(testEvent, TopicNames.ProductMatchFound, ct);

        return Results.Ok(new
        {
            message = "Test event published successfully.",
            topicName = TopicNames.ProductMatchFound,
            messageId = testEvent.MessageId,
            correlationId = testEvent.CorrelationId,
            matchId = testEvent.MatchId
        });
    }
}
```

### Updated API Program.cs

**File:** `src/AgentPayWatch.Api/Program.cs`

```csharp
using AgentPayWatch.Api.Endpoints;
using AgentPayWatch.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddInfrastructureServices();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

var app = builder.Build();

app.UseCors();

app.MapDefaultEndpoints();
app.MapWatchEndpoints();
app.MapDebugEndpoints(); // TEMPORARY: Remove in Phase 4

app.Run();
```

---

## Section 6: Verification

### Step 1: Build

```bash
dotnet build AgentPayWatch.slnx
```

**Expected:** Zero errors. Both `Aspire.Microsoft.Azure.Cosmos` and `Aspire.Azure.Messaging.ServiceBus` resolve and compile.

### Step 2: Start the AppHost

```bash
dotnet run --project appHost/apphost.cs
```

**Expected:** The Aspire dashboard shows:
- **cosmos** emulator container -- Running
- **messaging** (Service Bus emulator) container -- Running
- **api**, **product-watch-agent**, **approval-agent**, **payment-agent**, **web** -- all Running

The Service Bus emulator may take 30-60 seconds to fully start. The agents and API will wait due to `WaitFor(messaging)`.

### Step 3: Publish a test event

```bash
curl -X POST "http://localhost:<api-port>/api/debug/publish-test-event"
```

**Expected response (HTTP 200):**

```json
{
  "message": "Test event published successfully.",
  "topicName": "product-match-found",
  "messageId": "<guid>",
  "correlationId": "<guid>",
  "matchId": "<guid>"
}
```

### Step 4: Verify in Aspire Traces

1. Open the Aspire dashboard (URL shown in console output, typically `https://localhost:17225`).
2. Navigate to the **Traces** tab.
3. Look for a trace from the `api` resource that shows:
   - An outgoing dependency to Azure Service Bus.
   - The trace should show the `ServiceBusSender.SendMessage` span.
   - The span attributes should include the topic name `product-match-found`.
4. The trace confirms that the API successfully connected to the Service Bus emulator and published a message.

### Step 5: Verify with a second publish

```bash
curl -X POST "http://localhost:<api-port>/api/debug/publish-test-event"
```

Confirm that a second trace appears. The `ServiceBusEventPublisher` reuses the cached sender for the same topic -- the second call should be slightly faster.

### Step 6: Verify Cosmos still works

Run the Cosmos verification from Phase 2 to confirm nothing regressed:

```bash
curl -X POST "http://localhost:<api-port>/api/watches?userId=user-1" \
  -H "Content-Type: application/json" \
  -d '{"productName":"Nintendo Switch 2","maxPrice":399.99}'
```

**Expected:** HTTP 201 with the created watch response. Both Cosmos and Service Bus infrastructure coexist without issues.

### Troubleshooting

| Symptom | Fix |
|---------|-----|
| `Azure.Messaging.ServiceBus.ServiceBusException: ... not found` | The topic has not been created by the emulator yet. Wait for the emulator to fully initialize, or check the emulator container logs for errors. |
| `SocketException: Connection refused` on Service Bus | The emulator is still starting. Increase `WaitFor` timeout or check Docker for the container status. |
| API starts but `/api/debug/publish-test-event` returns 500 | Check the API logs in the Aspire dashboard. Common cause: the `IEventPublisher` was not registered. Verify `DependencyInjection.cs` calls `builder.AddAzureServiceBusClient("messaging")` and registers `ServiceBusEventPublisher`. |
| `InvalidOperationException: No service for type 'IEventPublisher'` | The `AddInfrastructureServices` call is missing from the API's `Program.cs`, or the `ServiceBusEventPublisher` registration line is missing. |
| Emulator container exits immediately | The Service Bus emulator requires Docker with sufficient resources. Ensure Docker Desktop is running and has at least 4 GB of memory allocated. |
| Traces tab shows no Service Bus spans | Ensure OpenTelemetry is configured (via `AddServiceDefaults()`). The `Aspire.Azure.Messaging.ServiceBus` package automatically instruments Service Bus calls when OpenTelemetry is active. |

---

### Summary of Phase 3 File Changes

| File | Action |
|------|--------|
| `src/AgentPayWatch.Infrastructure/AgentPayWatch.Infrastructure.csproj` | Added `Aspire.Azure.Messaging.ServiceBus` package reference |
| `src/AgentPayWatch.Infrastructure/Messaging/TopicNames.cs` | **New** -- static topic name constants |
| `src/AgentPayWatch.Infrastructure/Messaging/ServiceBusEventPublisher.cs` | **New** -- IEventPublisher implementation |
| `src/AgentPayWatch.Infrastructure/DependencyInjection.cs` | Updated -- added Service Bus client and publisher registration |
| `src/AgentPayWatch.Api/Endpoints/DebugEndpoints.cs` | **New** (temporary) -- test event endpoint |
| `src/AgentPayWatch.Api/Program.cs` | Updated -- added `MapDebugEndpoints()` |
| `appHost/apphost.cs` | Updated -- added Service Bus emulator with topics and subscriptions |
