# Phase 2: Data Layer -- Cosmos DB + Repositories

**Goal:** CRUD operations against Cosmos DB emulator work end-to-end, testable via API endpoints.

**Prerequisite:** Phase 1 complete -- solution compiles and all projects exist.

---

## Section 1: NuGet Packages

Add the Aspire Cosmos component to the Infrastructure project.

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
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\AgentPayWatch.Domain\AgentPayWatch.Domain.csproj" />
  </ItemGroup>

</Project>
```

Remove the `Placeholder.cs` file that was created in Phase 1 -- it is no longer needed once real code is added.

---

## Section 2: Cosmos Repositories

### 2.1 WatchRequest Repository

**File:** `src/AgentPayWatch.Infrastructure/Cosmos/CosmosWatchRequestRepository.cs`

```csharp
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentPayWatch.Domain.Entities;
using AgentPayWatch.Domain.Enums;
using AgentPayWatch.Domain.Interfaces;
using Microsoft.Azure.Cosmos;

namespace AgentPayWatch.Infrastructure.Cosmos;

public sealed class CosmosWatchRequestRepository : IWatchRequestRepository
{
    private readonly Container _container;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public CosmosWatchRequestRepository(CosmosClient cosmosClient)
    {
        _container = cosmosClient.GetContainer("agentpaywatch", "watches");
    }

    public async Task<WatchRequest> CreateAsync(WatchRequest watchRequest)
    {
        var json = JsonSerializer.Serialize(watchRequest, JsonOptions);
        var document = JsonDocument.Parse(json);
        var root = new Dictionary<string, object>();

        foreach (var property in document.RootElement.EnumerateObject())
        {
            root[property.Name] = property.Value;
        }

        root["id"] = watchRequest.Id.ToString();
        root["userId"] = watchRequest.UserId;

        var response = await _container.CreateItemAsync(
            root,
            new PartitionKey(watchRequest.UserId));

        watchRequest.ETag = response.ETag;
        return watchRequest;
    }

    public async Task<WatchRequest?> GetByIdAsync(Guid id, string userId)
    {
        try
        {
            var response = await _container.ReadItemAsync<JsonElement>(
                id.ToString(),
                new PartitionKey(userId));

            var watchRequest = JsonSerializer.Deserialize<WatchRequest>(
                response.Resource.GetRawText(), JsonOptions);

            if (watchRequest is not null)
            {
                watchRequest.ETag = response.ETag;
            }

            return watchRequest;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<WatchRequest>> GetByUserIdAsync(string userId)
    {
        var query = new QueryDefinition("SELECT * FROM c WHERE c.userId = @userId")
            .WithParameter("@userId", userId);

        var iterator = _container.GetItemQueryIterator<JsonElement>(
            query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(userId) });

        var results = new List<WatchRequest>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            foreach (var item in response)
            {
                var watchRequest = JsonSerializer.Deserialize<WatchRequest>(
                    item.GetRawText(), JsonOptions);
                if (watchRequest is not null)
                {
                    watchRequest.ETag = response.ETag;
                    results.Add(watchRequest);
                }
            }
        }

        return results;
    }

    public async Task<IReadOnlyList<WatchRequest>> GetByStatusAsync(WatchStatus status)
    {
        var statusString = JsonSerializer.Serialize(status, JsonOptions).Trim('"');

        var query = new QueryDefinition("SELECT * FROM c WHERE c.status = @status")
            .WithParameter("@status", statusString);

        var iterator = _container.GetItemQueryIterator<JsonElement>(
            query,
            requestOptions: new QueryRequestOptions { MaxItemCount = 100 });

        var results = new List<WatchRequest>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            foreach (var item in response)
            {
                var watchRequest = JsonSerializer.Deserialize<WatchRequest>(
                    item.GetRawText(), JsonOptions);
                if (watchRequest is not null)
                {
                    watchRequest.ETag = response.ETag;
                    results.Add(watchRequest);
                }
            }
        }

        return results;
    }

    public async Task UpdateAsync(WatchRequest watchRequest)
    {
        var json = JsonSerializer.Serialize(watchRequest, JsonOptions);
        var document = JsonDocument.Parse(json);
        var root = new Dictionary<string, object>();

        foreach (var property in document.RootElement.EnumerateObject())
        {
            root[property.Name] = property.Value;
        }

        root["id"] = watchRequest.Id.ToString();
        root["userId"] = watchRequest.UserId;

        var options = new ItemRequestOptions();
        if (!string.IsNullOrEmpty(watchRequest.ETag))
        {
            options.IfMatchEtag = watchRequest.ETag;
        }

        var response = await _container.ReplaceItemAsync(
            root,
            watchRequest.Id.ToString(),
            new PartitionKey(watchRequest.UserId),
            options);

        watchRequest.ETag = response.ETag;
    }
}
```

### 2.2 ProductMatch Repository

**File:** `src/AgentPayWatch.Infrastructure/Cosmos/CosmosProductMatchRepository.cs`

```csharp
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentPayWatch.Domain.Entities;
using AgentPayWatch.Domain.Interfaces;
using Microsoft.Azure.Cosmos;

namespace AgentPayWatch.Infrastructure.Cosmos;

public sealed class CosmosProductMatchRepository : IProductMatchRepository
{
    private readonly Container _container;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public CosmosProductMatchRepository(CosmosClient cosmosClient)
    {
        _container = cosmosClient.GetContainer("agentpaywatch", "matches");
    }

    public async Task<ProductMatch> CreateAsync(ProductMatch match)
    {
        var json = JsonSerializer.Serialize(match, JsonOptions);
        var document = JsonDocument.Parse(json);
        var root = new Dictionary<string, object>();

        foreach (var property in document.RootElement.EnumerateObject())
        {
            root[property.Name] = property.Value;
        }

        root["id"] = match.Id.ToString();
        root["watchRequestId"] = match.WatchRequestId.ToString();

        await _container.CreateItemAsync(
            root,
            new PartitionKey(match.WatchRequestId.ToString()));

        return match;
    }

    public async Task<ProductMatch?> GetByIdAsync(Guid id, Guid watchRequestId)
    {
        try
        {
            var response = await _container.ReadItemAsync<JsonElement>(
                id.ToString(),
                new PartitionKey(watchRequestId.ToString()));

            return JsonSerializer.Deserialize<ProductMatch>(
                response.Resource.GetRawText(), JsonOptions);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<ProductMatch>> GetByWatchRequestIdAsync(Guid watchRequestId)
    {
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.watchRequestId = @watchRequestId")
            .WithParameter("@watchRequestId", watchRequestId.ToString());

        var iterator = _container.GetItemQueryIterator<JsonElement>(
            query,
            requestOptions: new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(watchRequestId.ToString())
            });

        var results = new List<ProductMatch>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            foreach (var item in response)
            {
                var match = JsonSerializer.Deserialize<ProductMatch>(
                    item.GetRawText(), JsonOptions);
                if (match is not null)
                {
                    results.Add(match);
                }
            }
        }

        return results;
    }
}
```

### 2.3 Approval Repository

**File:** `src/AgentPayWatch.Infrastructure/Cosmos/CosmosApprovalRepository.cs`

```csharp
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentPayWatch.Domain.Entities;
using AgentPayWatch.Domain.Interfaces;
using Microsoft.Azure.Cosmos;

namespace AgentPayWatch.Infrastructure.Cosmos;

public sealed class CosmosApprovalRepository : IApprovalRepository
{
    private readonly Container _container;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public CosmosApprovalRepository(CosmosClient cosmosClient)
    {
        _container = cosmosClient.GetContainer("agentpaywatch", "approvals");
    }

    public async Task<ApprovalRecord> CreateAsync(ApprovalRecord approval)
    {
        var json = JsonSerializer.Serialize(approval, JsonOptions);
        var document = JsonDocument.Parse(json);
        var root = new Dictionary<string, object>();

        foreach (var property in document.RootElement.EnumerateObject())
        {
            root[property.Name] = property.Value;
        }

        root["id"] = approval.Id.ToString();
        root["watchRequestId"] = approval.WatchRequestId.ToString();

        await _container.CreateItemAsync(
            root,
            new PartitionKey(approval.WatchRequestId.ToString()));

        return approval;
    }

    public async Task<ApprovalRecord?> GetByIdAsync(Guid id, Guid watchRequestId)
    {
        try
        {
            var response = await _container.ReadItemAsync<JsonElement>(
                id.ToString(),
                new PartitionKey(watchRequestId.ToString()));

            return JsonSerializer.Deserialize<ApprovalRecord>(
                response.Resource.GetRawText(), JsonOptions);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<ApprovalRecord?> GetByTokenAsync(string token)
    {
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.approvalToken = @token")
            .WithParameter("@token", token);

        var iterator = _container.GetItemQueryIterator<JsonElement>(query);

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            foreach (var item in response)
            {
                return JsonSerializer.Deserialize<ApprovalRecord>(
                    item.GetRawText(), JsonOptions);
            }
        }

        return null;
    }

    public async Task UpdateAsync(ApprovalRecord approval)
    {
        var json = JsonSerializer.Serialize(approval, JsonOptions);
        var document = JsonDocument.Parse(json);
        var root = new Dictionary<string, object>();

        foreach (var property in document.RootElement.EnumerateObject())
        {
            root[property.Name] = property.Value;
        }

        root["id"] = approval.Id.ToString();
        root["watchRequestId"] = approval.WatchRequestId.ToString();

        await _container.ReplaceItemAsync(
            root,
            approval.Id.ToString(),
            new PartitionKey(approval.WatchRequestId.ToString()));
    }

    public async Task<IReadOnlyList<ApprovalRecord>> GetPendingExpiredAsync(DateTimeOffset now)
    {
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.decision = @decision AND c.expiresAt < @now")
            .WithParameter("@decision", "pending")
            .WithParameter("@now", now.ToString("o"));

        var iterator = _container.GetItemQueryIterator<JsonElement>(query);

        var results = new List<ApprovalRecord>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            foreach (var item in response)
            {
                var approval = JsonSerializer.Deserialize<ApprovalRecord>(
                    item.GetRawText(), JsonOptions);
                if (approval is not null)
                {
                    results.Add(approval);
                }
            }
        }

        return results;
    }
}
```

### 2.4 PaymentTransaction Repository

**File:** `src/AgentPayWatch.Infrastructure/Cosmos/CosmosPaymentTransactionRepository.cs`

```csharp
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentPayWatch.Domain.Entities;
using AgentPayWatch.Domain.Interfaces;
using Microsoft.Azure.Cosmos;

namespace AgentPayWatch.Infrastructure.Cosmos;

public sealed class CosmosPaymentTransactionRepository : IPaymentTransactionRepository
{
    private readonly Container _container;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public CosmosPaymentTransactionRepository(CosmosClient cosmosClient)
    {
        _container = cosmosClient.GetContainer("agentpaywatch", "transactions");
    }

    public async Task<PaymentTransaction> CreateAsync(PaymentTransaction transaction)
    {
        var json = JsonSerializer.Serialize(transaction, JsonOptions);
        var document = JsonDocument.Parse(json);
        var root = new Dictionary<string, object>();

        foreach (var property in document.RootElement.EnumerateObject())
        {
            root[property.Name] = property.Value;
        }

        root["id"] = transaction.Id.ToString();
        root["userId"] = transaction.UserId;

        await _container.CreateItemAsync(
            root,
            new PartitionKey(transaction.UserId));

        return transaction;
    }

    public async Task<PaymentTransaction?> GetByIdAsync(Guid id, string userId)
    {
        try
        {
            var response = await _container.ReadItemAsync<JsonElement>(
                id.ToString(),
                new PartitionKey(userId));

            return JsonSerializer.Deserialize<PaymentTransaction>(
                response.Resource.GetRawText(), JsonOptions);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<PaymentTransaction>> GetByUserIdAsync(string userId)
    {
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.userId = @userId")
            .WithParameter("@userId", userId);

        var iterator = _container.GetItemQueryIterator<JsonElement>(
            query,
            requestOptions: new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(userId)
            });

        var results = new List<PaymentTransaction>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            foreach (var item in response)
            {
                var transaction = JsonSerializer.Deserialize<PaymentTransaction>(
                    item.GetRawText(), JsonOptions);
                if (transaction is not null)
                {
                    results.Add(transaction);
                }
            }
        }

        return results;
    }

    public async Task UpdateAsync(PaymentTransaction transaction)
    {
        var json = JsonSerializer.Serialize(transaction, JsonOptions);
        var document = JsonDocument.Parse(json);
        var root = new Dictionary<string, object>();

        foreach (var property in document.RootElement.EnumerateObject())
        {
            root[property.Name] = property.Value;
        }

        root["id"] = transaction.Id.ToString();
        root["userId"] = transaction.UserId;

        await _container.ReplaceItemAsync(
            root,
            transaction.Id.ToString(),
            new PartitionKey(transaction.UserId));
    }
}
```

### 2.5 Cosmos DB Initializer

**File:** `src/AgentPayWatch.Infrastructure/Cosmos/CosmosDbInitializer.cs`

```csharp
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentPayWatch.Infrastructure.Cosmos;

public sealed class CosmosDbInitializer : IHostedService
{
    private readonly CosmosClient _cosmosClient;
    private readonly ILogger<CosmosDbInitializer> _logger;

    private const string DatabaseName = "agentpaywatch";

    private static readonly (string Name, string PartitionKeyPath)[] Containers =
    [
        ("watches", "/userId"),
        ("matches", "/watchRequestId"),
        ("approvals", "/watchRequestId"),
        ("transactions", "/userId")
    ];

    public CosmosDbInitializer(CosmosClient cosmosClient, ILogger<CosmosDbInitializer> logger)
    {
        _cosmosClient = cosmosClient;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        const int maxRetries = 5;
        var delay = TimeSpan.FromSeconds(3);

        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                _logger.LogInformation(
                    "Initializing Cosmos DB (attempt {Attempt}/{MaxRetries})...",
                    attempt, maxRetries);

                var databaseResponse = await _cosmosClient.CreateDatabaseIfNotExistsAsync(
                    DatabaseName, cancellationToken: cancellationToken);

                var database = databaseResponse.Database;

                foreach (var (name, partitionKeyPath) in Containers)
                {
                    await database.CreateContainerIfNotExistsAsync(
                        new ContainerProperties(name, partitionKeyPath),
                        cancellationToken: cancellationToken);

                    _logger.LogInformation(
                        "Ensured container '{ContainerName}' with partition key '{PartitionKey}'.",
                        name, partitionKeyPath);
                }

                _logger.LogInformation("Cosmos DB initialization completed successfully.");
                return;
            }
            catch (Exception ex) when (attempt < maxRetries)
            {
                _logger.LogWarning(ex,
                    "Cosmos DB initialization attempt {Attempt} failed. Retrying in {Delay}s...",
                    attempt, delay.TotalSeconds);

                await Task.Delay(delay, cancellationToken);
                delay *= 2;
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
```

---

## Section 3: DI Registration

**File:** `src/AgentPayWatch.Infrastructure/DependencyInjection.cs`

```csharp
using AgentPayWatch.Domain.Interfaces;
using AgentPayWatch.Infrastructure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AgentPayWatch.Infrastructure;

public static class DependencyInjection
{
    public static IHostApplicationBuilder AddInfrastructureServices(
        this IHostApplicationBuilder builder)
    {
        builder.AddAzureCosmosClient("cosmos");

        builder.Services.AddScoped<IWatchRequestRepository, CosmosWatchRequestRepository>();
        builder.Services.AddScoped<IProductMatchRepository, CosmosProductMatchRepository>();
        builder.Services.AddScoped<IApprovalRepository, CosmosApprovalRepository>();
        builder.Services.AddScoped<IPaymentTransactionRepository, CosmosPaymentTransactionRepository>();

        builder.Services.AddHostedService<CosmosDbInitializer>();

        return builder;
    }
}
```

---

## Section 4: API Watch Endpoints

### 4.1 Contracts

**File:** `src/AgentPayWatch.Api/Contracts/CreateWatchRequest.cs`

```csharp
namespace AgentPayWatch.Api.Contracts;

public sealed record CreateWatchRequest(
    string ProductName,
    decimal MaxPrice,
    string Currency = "USD",
    string[]? PreferredSellers = null);
```

**File:** `src/AgentPayWatch.Api/Contracts/WatchResponse.cs`

```csharp
using AgentPayWatch.Domain.Entities;
using AgentPayWatch.Domain.Enums;

namespace AgentPayWatch.Api.Contracts;

public sealed record WatchResponse(
    Guid Id,
    string UserId,
    string ProductName,
    decimal MaxPrice,
    string Currency,
    string[] PreferredSellers,
    ApprovalMode ApprovalMode,
    decimal? AutoApproveThreshold,
    NotificationChannel NotificationChannel,
    WatchStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static WatchResponse FromEntity(WatchRequest entity) => new(
        entity.Id,
        entity.UserId,
        entity.ProductName,
        entity.MaxPrice,
        entity.Currency,
        entity.PreferredSellers,
        entity.ApprovalMode,
        entity.AutoApproveThreshold,
        entity.NotificationChannel,
        entity.Status,
        entity.CreatedAt,
        entity.UpdatedAt);
}
```

### 4.2 Watch Endpoints

**File:** `src/AgentPayWatch.Api/Endpoints/WatchEndpoints.cs`

```csharp
using AgentPayWatch.Api.Contracts;
using AgentPayWatch.Domain.Entities;
using AgentPayWatch.Domain.Enums;
using AgentPayWatch.Domain.Interfaces;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AgentPayWatch.Api.Endpoints;

public static class WatchEndpoints
{
    public static IEndpointRouteBuilder MapWatchEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/watches")
            .WithTags("Watches");

        group.MapPost("/", CreateWatch)
            .WithName("CreateWatch")
            .WithDescription("Create a new watch request.");

        group.MapGet("/", ListWatches)
            .WithName("ListWatches")
            .WithDescription("List watch requests for a user.");

        group.MapGet("/{id:guid}", GetWatch)
            .WithName("GetWatch")
            .WithDescription("Get a single watch request by ID.");

        group.MapPut("/{id:guid}/pause", PauseWatch)
            .WithName("PauseWatch")
            .WithDescription("Pause an active watch.");

        group.MapPut("/{id:guid}/resume", ResumeWatch)
            .WithName("ResumeWatch")
            .WithDescription("Resume a paused watch.");

        group.MapDelete("/{id:guid}", CancelWatch)
            .WithName("CancelWatch")
            .WithDescription("Cancel a watch request.");

        return routes;
    }

    private static async Task<Results<Created<WatchResponse>, BadRequest<string>>> CreateWatch(
        CreateWatchRequest request,
        string userId,
        IWatchRequestRepository repository)
    {
        if (string.IsNullOrWhiteSpace(request.ProductName))
        {
            return TypedResults.BadRequest("ProductName is required.");
        }

        if (request.MaxPrice <= 0)
        {
            return TypedResults.BadRequest("MaxPrice must be greater than zero.");
        }

        if (string.IsNullOrWhiteSpace(userId))
        {
            return TypedResults.BadRequest("userId query parameter is required.");
        }

        var entity = new WatchRequest
        {
            UserId = userId,
            ProductName = request.ProductName,
            MaxPrice = request.MaxPrice,
            Currency = request.Currency,
            PreferredSellers = request.PreferredSellers ?? []
        };

        var created = await repository.CreateAsync(entity);
        var response = WatchResponse.FromEntity(created);

        return TypedResults.Created($"/api/watches/{response.Id}", response);
    }

    private static async Task<Results<Ok<IReadOnlyList<WatchResponse>>, BadRequest<string>>> ListWatches(
        string userId,
        IWatchRequestRepository repository)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return TypedResults.BadRequest("userId query parameter is required.");
        }

        var entities = await repository.GetByUserIdAsync(userId);
        var responses = entities.Select(WatchResponse.FromEntity).ToList() as IReadOnlyList<WatchResponse>;

        return TypedResults.Ok(responses);
    }

    private static async Task<Results<Ok<WatchResponse>, BadRequest<string>, NotFound>> GetWatch(
        Guid id,
        string userId,
        IWatchRequestRepository repository)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return TypedResults.BadRequest("userId query parameter is required.");
        }

        var entity = await repository.GetByIdAsync(id, userId);
        if (entity is null)
        {
            return TypedResults.NotFound();
        }

        return TypedResults.Ok(WatchResponse.FromEntity(entity));
    }

    private static async Task<Results<Ok<WatchResponse>, BadRequest<string>, NotFound>> PauseWatch(
        Guid id,
        string userId,
        IWatchRequestRepository repository)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return TypedResults.BadRequest("userId query parameter is required.");
        }

        var entity = await repository.GetByIdAsync(id, userId);
        if (entity is null)
        {
            return TypedResults.NotFound();
        }

        entity.UpdateStatus(WatchStatus.Paused, "User requested pause.");
        await repository.UpdateAsync(entity);

        return TypedResults.Ok(WatchResponse.FromEntity(entity));
    }

    private static async Task<Results<Ok<WatchResponse>, BadRequest<string>, NotFound>> ResumeWatch(
        Guid id,
        string userId,
        IWatchRequestRepository repository)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return TypedResults.BadRequest("userId query parameter is required.");
        }

        var entity = await repository.GetByIdAsync(id, userId);
        if (entity is null)
        {
            return TypedResults.NotFound();
        }

        entity.UpdateStatus(WatchStatus.Active, "User requested resume.");
        await repository.UpdateAsync(entity);

        return TypedResults.Ok(WatchResponse.FromEntity(entity));
    }

    private static async Task<Results<NoContent, BadRequest<string>, NotFound>> CancelWatch(
        Guid id,
        string userId,
        IWatchRequestRepository repository)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return TypedResults.BadRequest("userId query parameter is required.");
        }

        var entity = await repository.GetByIdAsync(id, userId);
        if (entity is null)
        {
            return TypedResults.NotFound();
        }

        entity.UpdateStatus(WatchStatus.Cancelled, "User requested cancellation.");
        await repository.UpdateAsync(entity);

        return TypedResults.NoContent();
    }
}
```

### 4.3 Updated API Program.cs

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

app.Run();
```

---

## Section 5: AppHost -- Add Cosmos Emulator

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

var cosmos = builder.AddAzureCosmosDB("cosmos")
    .RunAsEmulator()
    .AddDatabase("agentpaywatch");

var api = builder.AddProject<Projects.AgentPayWatch_Api>("api")
    .WithReference(cosmos)
    .WaitFor(cosmos);

var productWatchAgent = builder.AddProject<Projects.AgentPayWatch_Agents_ProductWatch>("product-watch-agent")
    .WithReference(cosmos)
    .WaitFor(cosmos);

var approvalAgent = builder.AddProject<Projects.AgentPayWatch_Agents_Approval>("approval-agent")
    .WithReference(cosmos)
    .WaitFor(cosmos);

var paymentAgent = builder.AddProject<Projects.AgentPayWatch_Agents_Payment>("payment-agent")
    .WithReference(cosmos)
    .WaitFor(cosmos);

var web = builder.AddProject<Projects.AgentPayWatch_Web>("web");

builder.Build().Run();
```

---

## Section 6: Verification

### Step 1: Build

```bash
dotnet build AgentPayWatch.slnx
```

**Expected:** Zero errors. The Aspire Cosmos component compiles with Infrastructure.

### Step 2: Start the AppHost

```bash
dotnet run --project appHost/apphost.cs
```

**Expected:** The Cosmos DB emulator container starts (visible in Docker and in the Aspire dashboard). All resources eventually reach **Running** state. The `CosmosDbInitializer` logs should show successful container creation in the API logs within the dashboard.

### Step 3: Create a watch request

```bash
curl -X POST "http://localhost:<api-port>/api/watches?userId=user-1" \
  -H "Content-Type: application/json" \
  -d '{"productName":"PlayStation 5","maxPrice":450.00,"currency":"USD","preferredSellers":["Amazon","BestBuy"]}'
```

**Expected response (HTTP 201):**

```json
{
  "id": "<guid>",
  "userId": "user-1",
  "productName": "PlayStation 5",
  "maxPrice": 450.00,
  "currency": "USD",
  "preferredSellers": ["Amazon", "BestBuy"],
  "approvalMode": "AlwaysAsk",
  "autoApproveThreshold": null,
  "notificationChannel": "A2P_SMS",
  "status": "Active",
  "createdAt": "2026-02-20T...",
  "updatedAt": "2026-02-20T..."
}
```

Save the returned `id` for subsequent calls (referred to as `<watch-id>` below).

### Step 4: List watches for user

```bash
curl "http://localhost:<api-port>/api/watches?userId=user-1"
```

**Expected response (HTTP 200):** An array containing the watch you just created.

### Step 5: Get a single watch

```bash
curl "http://localhost:<api-port>/api/watches/<watch-id>?userId=user-1"
```

**Expected response (HTTP 200):** The single watch object.

### Step 6: Pause the watch

```bash
curl -X PUT "http://localhost:<api-port>/api/watches/<watch-id>/pause?userId=user-1"
```

**Expected response (HTTP 200):** The watch with `"status": "Paused"`.

### Step 7: Resume the watch

```bash
curl -X PUT "http://localhost:<api-port>/api/watches/<watch-id>/resume?userId=user-1"
```

**Expected response (HTTP 200):** The watch with `"status": "Active"`.

### Step 8: Cancel the watch

```bash
curl -X DELETE "http://localhost:<api-port>/api/watches/<watch-id>?userId=user-1"
```

**Expected response (HTTP 204):** No content.

### Step 9: Verify cancellation

```bash
curl "http://localhost:<api-port>/api/watches/<watch-id>?userId=user-1"
```

**Expected response (HTTP 200):** The watch with `"status": "Cancelled"`.

### Troubleshooting

| Symptom | Fix |
|---------|-----|
| `CosmosException: Connection refused` | The emulator has not finished starting. Check Docker for the container status. Increase the retry delay in `CosmosDbInitializer` if needed. |
| `CosmosException: 409 Conflict` on create | Duplicate ID. Each `WatchRequest` gets a new `Guid.NewGuid()` -- ensure you are not replaying the same request body with a manually set `id`. |
| `CosmosException: 412 Precondition Failed` on update | ETag mismatch (optimistic concurrency). The entity was modified between your read and write. Re-read and retry. |
| API returns 500 with `AddAzureCosmosClient` error | Ensure `Aspire.Microsoft.Azure.Cosmos` version 10.1.0 is referenced in Infrastructure.csproj and `builder.AddInfrastructureServices()` is called in the API's `Program.cs`. |
