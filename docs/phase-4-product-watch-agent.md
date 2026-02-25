# Phase 4: Product Watch Agent -- Autonomous Monitoring

> **Goal:** The Product Watch Agent autonomously finds product matches and publishes events. Create a watch via the API, wait 15-30 seconds, and see it auto-match.

**Prerequisite:** Phase 3 complete (Cosmos DB repositories + Service Bus event publishing working end-to-end).

---

## Section 1: Mock Product Source

**File:** `src/AgentPayWatch.Infrastructure/Mocks/MockProductSource.cs`

This implements `IProductSource` with a hardcoded catalog of ~10 products across categories. Each call applies price jitter and returns 0-3 matching results so the agent behaves realistically without external dependencies.

```csharp
using AgentPayWatch.Domain.Enums;
using AgentPayWatch.Domain.Interfaces;
using AgentPayWatch.Domain.Models;

namespace AgentPayWatch.Infrastructure.Mocks;

public sealed class MockProductSource : IProductSource
{
    private static readonly List<CatalogEntry> Catalog =
    [
        // Electronics
        new("iPhone 15 Pro", 949.00m, "USD", "TechZone", ProductAvailability.InStock),
        new("iPhone 15 Pro Max", 1099.00m, "USD", "MegaDeals", ProductAvailability.InStock),
        new("Samsung Galaxy S24 Ultra", 879.00m, "USD", "GadgetWorld", ProductAvailability.LimitedStock),
        new("Sony WH-1000XM5 Headphones", 298.00m, "USD", "AudioHaven", ProductAvailability.InStock),

        // Books
        new("Clean Architecture Book", 32.00m, "USD", "BookNest", ProductAvailability.InStock),
        new("Designing Data-Intensive Applications", 38.00m, "USD", "PageTurner", ProductAvailability.InStock),

        // Gaming
        new("PlayStation 5 Console", 449.00m, "USD", "GameVault", ProductAvailability.LimitedStock),
        new("Xbox Series X", 429.00m, "USD", "TechZone", ProductAvailability.InStock),
        new("Nintendo Switch OLED", 299.00m, "USD", "GameVault", ProductAvailability.InStock),

        // Kitchen
        new("KitchenAid Stand Mixer", 329.00m, "USD", "HomeEssentials", ProductAvailability.InStock),
    ];

    public Task<IReadOnlyList<ProductListing>> SearchAsync(string productName, CancellationToken ct)
    {
        var results = new List<ProductListing>();
        var searchTerm = productName.Trim();

        foreach (var entry in Catalog)
        {
            if (!entry.Name.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Seed from product name hash + current minute so results vary over time
            // but are deterministic within the same minute for the same product.
            int seed = HashCode.Combine(entry.Name, DateTime.UtcNow.Minute);
            var rng = new Random(seed);

            // About 70% chance we return this product at all (simulates availability)
            if (rng.NextDouble() > 0.70)
            {
                continue;
            }

            // Apply +/- 15% price jitter to the base price
            double jitterFactor = 1.0 + ((rng.NextDouble() * 0.30) - 0.15); // range: 0.85 to 1.15
            decimal jitteredPrice = Math.Round(entry.BasePrice * (decimal)jitterFactor, 2);

            string slug = entry.Name.ToLowerInvariant()
                .Replace(' ', '-')
                .Replace(".", "");

            var listing = new ProductListing(
                Name: entry.Name,
                Price: jitteredPrice,
                Currency: entry.Currency,
                Seller: entry.Seller,
                Url: $"https://store.example.com/products/{slug}",
                Availability: entry.Availability
            );

            results.Add(listing);
        }

        // Cap at 3 results maximum
        if (results.Count > 3)
        {
            results = results.Take(3).ToList();
        }

        return Task.FromResult<IReadOnlyList<ProductListing>>(results);
    }

    private sealed record CatalogEntry(
        string Name,
        decimal BasePrice,
        string Currency,
        string Seller,
        ProductAvailability Availability
    );
}
```

### Register in DependencyInjection.cs

Open `src/AgentPayWatch.Infrastructure/DependencyInjection.cs` and add the following registration inside the `AddInfrastructureServices` method:

```csharp
using AgentPayWatch.Infrastructure.Mocks;

// Inside AddInfrastructureServices(), add:
builder.Services.AddSingleton<IProductSource, MockProductSource>();
```

The full updated `DependencyInjection.cs` should include this alongside the existing Cosmos and Service Bus registrations:

```csharp
using AgentPayWatch.Domain.Interfaces;
using AgentPayWatch.Infrastructure.Cosmos;
using AgentPayWatch.Infrastructure.Messaging;
using AgentPayWatch.Infrastructure.Mocks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AgentPayWatch.Infrastructure;

public static class DependencyInjection
{
    public static TBuilder AddInfrastructureServices<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        // Cosmos DB
        builder.AddAzureCosmosClient("cosmos");
        builder.Services.AddHostedService<CosmosDbInitializer>();
        builder.Services.AddSingleton<IWatchRequestRepository, CosmosWatchRequestRepository>();
        builder.Services.AddSingleton<IProductMatchRepository, CosmosProductMatchRepository>();
        builder.Services.AddSingleton<IApprovalRepository, CosmosApprovalRepository>();
        builder.Services.AddSingleton<IPaymentTransactionRepository, CosmosPaymentTransactionRepository>();

        // Service Bus
        builder.AddAzureServiceBusClient("messaging");
        builder.Services.AddSingleton<IEventPublisher, ServiceBusEventPublisher>();

        // Mocks
        builder.Services.AddSingleton<IProductSource, MockProductSource>();

        return builder;
    }
}
```

---

## Section 2: Matching Service

**File:** `src/AgentPayWatch.Agents.ProductWatch/MatchingService.cs`

A simple static helper that determines whether a product listing satisfies a watch request's criteria.

```csharp
using AgentPayWatch.Domain.Entities;
using AgentPayWatch.Domain.Models;

namespace AgentPayWatch.Agents.ProductWatch;

public static class MatchingService
{
    /// <summary>
    /// Determines whether a product listing satisfies the watch request criteria.
    /// A listing is a match if:
    ///   1. Its price is at or below the watch's max price, AND
    ///   2. The watch has no preferred sellers, OR the listing's seller is in the preferred list.
    /// </summary>
    public static bool IsMatch(WatchRequest watch, ProductListing listing)
    {
        if (listing.Price > watch.MaxPrice)
        {
            return false;
        }

        if (watch.PreferredSellers is null || watch.PreferredSellers.Length == 0)
        {
            return true;
        }

        return watch.PreferredSellers.Any(
            seller => string.Equals(seller, listing.Seller, StringComparison.OrdinalIgnoreCase)
        );
    }
}
```

---

## Section 3: Product Watch Worker

**File:** `src/AgentPayWatch.Agents.ProductWatch/ProductWatchWorker.cs`

This is the core `BackgroundService` that periodically scans all active watches, searches for products, evaluates matches, persists results, and publishes events.

```csharp
using AgentPayWatch.Domain.Entities;
using AgentPayWatch.Domain.Enums;
using AgentPayWatch.Domain.Events;
using AgentPayWatch.Domain.Interfaces;
using AgentPayWatch.Domain.Models;
using AgentPayWatch.Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentPayWatch.Agents.ProductWatch;

public sealed class ProductWatchWorker : BackgroundService
{
    private readonly IWatchRequestRepository _watchRepo;
    private readonly IProductMatchRepository _matchRepo;
    private readonly IProductSource _productSource;
    private readonly IEventPublisher _eventPublisher;
    private readonly ILogger<ProductWatchWorker> _logger;
    private readonly TimeSpan _pollInterval;

    public ProductWatchWorker(
        IWatchRequestRepository watchRepo,
        IProductMatchRepository matchRepo,
        IProductSource productSource,
        IEventPublisher eventPublisher,
        ILogger<ProductWatchWorker> logger,
        IConfiguration configuration)
    {
        _watchRepo = watchRepo;
        _matchRepo = matchRepo;
        _productSource = productSource;
        _eventPublisher = eventPublisher;
        _logger = logger;

        int intervalSeconds = configuration.GetValue<int>("ProductWatch:PollIntervalSeconds", 15);
        _pollInterval = TimeSpan.FromSeconds(intervalSeconds);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "ProductWatchWorker started. Poll interval: {IntervalSeconds}s",
            _pollInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ScanAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Graceful shutdown; exit the loop.
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error during scan cycle. Will retry next interval.");
            }

            try
            {
                await Task.Delay(_pollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogInformation("ProductWatchWorker stopped.");
    }

    private async Task ScanAsync(CancellationToken ct)
    {
        _logger.LogDebug("Starting scan cycle...");

        IReadOnlyList<WatchRequest> activeWatches;
        try
        {
            activeWatches = await _watchRepo.GetByStatusAsync(WatchStatus.Active, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load active watches. Skipping this scan cycle.");
            return;
        }

        int matchCount = 0;

        foreach (var watch in activeWatches)
        {
            try
            {
                bool matched = await ProcessWatchAsync(watch, ct);
                if (matched)
                {
                    matchCount++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Error processing watch {WatchId} for product '{ProductName}'. Skipping.",
                    watch.Id,
                    watch.ProductName);
            }
        }

        _logger.LogInformation(
            "Scan complete. {ActiveCount} watches scanned, {MatchCount} matches found.",
            activeWatches.Count,
            matchCount);
    }

    private async Task<bool> ProcessWatchAsync(WatchRequest watch, CancellationToken ct)
    {
        IReadOnlyList<ProductListing> listings = await _productSource.SearchAsync(watch.ProductName, ct);

        // Filter through matching criteria and find the best (lowest price) match
        ProductListing? bestMatch = listings
            .Where(listing => MatchingService.IsMatch(watch, listing))
            .OrderBy(listing => listing.Price)
            .FirstOrDefault();

        if (bestMatch is null)
        {
            _logger.LogDebug(
                "No match for watch {WatchId}: {ProductName}",
                watch.Id,
                watch.ProductName);
            return false;
        }

        // Create the ProductMatch entity
        var productMatch = new ProductMatch
        {
            Id = Guid.NewGuid(),
            WatchRequestId = watch.Id,
            UserId = watch.UserId,
            ProductName = bestMatch.Name,
            Price = bestMatch.Price,
            Currency = bestMatch.Currency,
            Seller = bestMatch.Seller,
            ProductUrl = bestMatch.Url,
            MatchedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(24),
            Availability = bestMatch.Availability
        };

        // Persist match to Cosmos
        await _matchRepo.CreateAsync(productMatch, ct);

        // Update watch status to Matched
        watch.UpdateStatus(WatchStatus.Matched);
        await _watchRepo.UpdateAsync(watch, ct);

        // Publish ProductMatchFound event
        var matchEvent = new ProductMatchFound(
            MessageId: Guid.NewGuid().ToString(),
            CorrelationId: watch.Id.ToString(),
            Timestamp: DateTimeOffset.UtcNow,
            Source: "product-watch-agent",
            MatchId: productMatch.Id,
            ProductName: productMatch.ProductName,
            Price: productMatch.Price,
            Currency: productMatch.Currency,
            Seller: productMatch.Seller
        );

        await _eventPublisher.PublishAsync(matchEvent, TopicNames.ProductMatchFound, ct);

        _logger.LogInformation(
            "Match found for watch {WatchId}: {ProductName} at {Price} from {Seller}",
            watch.Id,
            productMatch.ProductName,
            productMatch.Price,
            productMatch.Seller);

        return true;
    }
}
```

---

## Section 4: Program.cs

**File:** `src/AgentPayWatch.Agents.ProductWatch/Program.cs`

The worker host wires up Aspire service defaults, infrastructure services, and the `ProductWatchWorker` hosted service.

```csharp
using AgentPayWatch.Agents.ProductWatch;
using AgentPayWatch.Infrastructure;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddInfrastructureServices();

builder.Services.AddHostedService<ProductWatchWorker>();

var host = builder.Build();
await host.RunAsync();
```

---

## Section 5: appsettings.json

**File:** `src/AgentPayWatch.Agents.ProductWatch/appsettings.json`

```json
{
  "ProductWatch": {
    "PollIntervalSeconds": 15
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "AgentPayWatch.Agents.ProductWatch": "Debug"
    }
  }
}
```

---

## Section 6: Project File

**File:** `src/AgentPayWatch.Agents.ProductWatch/AgentPayWatch.Agents.ProductWatch.csproj`

Ensure the project file references Domain, Infrastructure, and ServiceDefaults:

```xml
<Project Sdk="Microsoft.NET.Sdk.Worker">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\AgentPayWatch.Domain\AgentPayWatch.Domain.csproj" />
    <ProjectReference Include="..\AgentPayWatch.Infrastructure\AgentPayWatch.Infrastructure.csproj" />
    <ProjectReference Include="..\AgentPayWatch.ServiceDefaults\AgentPayWatch.ServiceDefaults.csproj" />
  </ItemGroup>

</Project>
```

---

## Section 7: Verification

### Step 1: Start the System

```bash
dotnet run --project appHost/apphost.cs
```

Wait for the Aspire dashboard to confirm all services are running. The Cosmos emulator and Service Bus emulator containers should be healthy.

### Step 2: Create a Watch for "iPhone"

```bash
curl -X POST http://localhost:5000/api/watches \
  -H "Content-Type: application/json" \
  -d '{
    "productName": "iPhone",
    "maxPrice": 999.00,
    "userId": "demo-user",
    "currency": "USD",
    "phoneNumber": "+15551234567",
    "paymentMethodToken": "tok_demo_visa",
    "notificationChannel": "A2P_SMS",
    "approvalMode": "AlwaysAsk"
  }'
```

Note the `id` returned in the response. The watch status will be `Active`.

### Step 3: Wait 15-30 Seconds

The `ProductWatchWorker` polls every 15 seconds. In the agent logs (visible in the Aspire dashboard), you should see output like:

```
info: AgentPayWatch.Agents.ProductWatch.ProductWatchWorker
      Scan complete. 1 watches scanned, 1 matches found.
info: AgentPayWatch.Agents.ProductWatch.ProductWatchWorker
      Match found for watch {WatchId}: iPhone 15 Pro at 912.43 from TechZone
```

### Step 4: Query the Watch -- Status Should Be "Matched"

```bash
curl http://localhost:5000/api/watches/{id}?userId=demo-user
```

The response should show `"status": "Matched"`.

### Step 5: Query Matches -- Should Have a ProductMatch

```bash
curl http://localhost:5000/api/matches/{watchId}
```

The response should contain the match details including product name, price, seller, URL, and timestamps.

### Step 6: Check Aspire Traces

Open the Aspire dashboard (typically at `http://localhost:15888`). Navigate to:

1. **Structured Logs** -- Filter by `product-watch-agent`. You should see scan cycle logs and match-found entries.
2. **Traces** -- Look for traces showing the Cosmos DB write for the ProductMatch and the Service Bus publish for the `ProductMatchFound` event.
3. **Resources** -- Confirm the `product-watch-agent` service is running without errors.

### Troubleshooting

| Symptom | Likely Cause | Fix |
|---------|-------------|-----|
| No matches after 30+ seconds | Watch product name does not partially match any catalog entry | Use "iPhone", "PlayStation", "KitchenAid", "Galaxy", "Headphones", "Switch", "Xbox", "Clean Architecture", or "Data-Intensive" |
| Watch stays Active | Price jitter pushed all results above MaxPrice | Increase `maxPrice` (e.g., 1200 for iPhone) or wait another cycle |
| Worker not polling | Worker not registered or infrastructure services missing | Verify `Program.cs` has `AddHostedService<ProductWatchWorker>()` and `AddInfrastructureServices()` |
| Cosmos errors | Emulator not ready or containers not created | Check `CosmosDbInitializer` ran successfully in API/agent startup logs |

---

## Architecture Summary

After completing Phase 4, the system flow is:

```
User creates watch (API)
    |
    v
WatchRequest saved to Cosmos (status: Active)
    |
    v
ProductWatchWorker polls every 15s
    |
    v
MockProductSource returns listings with price jitter
    |
    v
MatchingService filters by price + preferred sellers
    |
    v
Best match saved as ProductMatch to Cosmos
    |
    v
WatchRequest updated to Matched
    |
    v
ProductMatchFound event published to Service Bus
    (topic: "product-match-found")
```

The `ProductMatchFound` event on the `product-match-found` topic is what Phase 5 (Approval Agent) will consume via its `approval-agent` subscription.
