# Phase 1: Foundation -- Domain + Solution Scaffold

**Goal:** All projects exist, solution compiles, Aspire dashboard starts with all resources visible.

**Prerequisite:** Existing repo with `AgentPayWatch.ServiceDefaults` project and bare `appHost/apphost.cs` using `#:sdk Aspire.AppHost.Sdk@13.1.1` directives. The solution file `AgentPayWatch.slnx` currently references only ServiceDefaults.

---

## Section 1: Domain Library

### 1.1 Project File

**File:** `src/AgentPayWatch.Domain/AgentPayWatch.Domain.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">gem

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>AgentPayWatch.Domain</RootNamespace>
  </PropertyGroup>

</Project>
```

### 1.2 Enums
n
**File:** `src/AgentPayWatch.Domain/Enums/WatchStatus.cs`

```csharp
namespace AgentPayWatch.Domain.Enums;

public enum WatchStatus
{
    Active,
    Paused,
    Matched,
    AwaitingApproval,
    Approved,
    Purchasing,
    Completed,
    Expired,
    Cancelled
}
```

**File:** `src/AgentPayWatch.Domain/Enums/ApprovalDecision.cs`

```csharp
namespace AgentPayWatch.Domain.Enums;

public enum ApprovalDecision
{
    Pending,
    Approved,
    Rejected,
    Expired
}
```

**File:** `src/AgentPayWatch.Domain/Enums/PaymentStatus.cs`

```csharp
namespace AgentPayWatch.Domain.Enums;

public enum PaymentStatus
{
    Initiated,
    Processing,
    Succeeded,
    Failed,
    Reversed
}
```

**File:** `src/AgentPayWatch.Domain/Enums/ProductAvailability.cs`

```csharp
namespace AgentPayWatch.Domain.Enums;

public enum ProductAvailability
{
    InStock,
    LimitedStock,
    PreOrder
}
```

**File:** `src/AgentPayWatch.Domain/Enums/ApprovalMode.cs`

```csharp
namespace AgentPayWatch.Domain.Enums;

public enum ApprovalMode
{
    AlwaysAsk,
    AutoApproveUnder
}
```

**File:** `src/AgentPayWatch.Domain/Enums/NotificationChannel.cs`

```csharp
namespace AgentPayWatch.Domain.Enums;

public enum NotificationChannel
{
    A2P_RCS,
    A2P_SMS
}
```

### 1.3 Value Objects

**File:** `src/AgentPayWatch.Domain/ValueObjects/StatusChange.cs`

```csharp
using AgentPayWatch.Domain.Enums;

namespace AgentPayWatch.Domain.ValueObjects;

public sealed record StatusChange(
    WatchStatus From,
    WatchStatus To,
    DateTimeOffset ChangedAt,
    string? Reason);
```

### 1.4 Entities

**File:** `src/AgentPayWatch.Domain/Entities/WatchRequest.cs`

```csharp
using AgentPayWatch.Domain.Enums;
using AgentPayWatch.Domain.ValueObjects;

namespace AgentPayWatch.Domain.Entities;

public sealed class WatchRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserId { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public decimal MaxPrice { get; set; }
    public string Currency { get; set; } = "USD";
    public string[] PreferredSellers { get; set; } = [];
    public ApprovalMode ApprovalMode { get; set; } = ApprovalMode.AlwaysAsk;
    public decimal? AutoApproveThreshold { get; set; }
    public string PaymentMethodToken { get; set; } = string.Empty;
    public NotificationChannel NotificationChannel { get; set; } = NotificationChannel.A2P_SMS;
    public string PhoneNumber { get; set; } = string.Empty;
    public WatchStatus Status { get; set; } = WatchStatus.Active;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<StatusChange> StatusHistory { get; set; } = [];
    public string ETag { get; set; } = string.Empty;

    private static readonly Dictionary<WatchStatus, HashSet<WatchStatus>> AllowedTransitions = new()
    {
        [WatchStatus.Active] = [WatchStatus.Paused, WatchStatus.Matched, WatchStatus.Expired, WatchStatus.Cancelled],
        [WatchStatus.Paused] = [WatchStatus.Active, WatchStatus.Cancelled],
        [WatchStatus.Matched] = [WatchStatus.AwaitingApproval, WatchStatus.Active, WatchStatus.Cancelled],
        [WatchStatus.AwaitingApproval] = [WatchStatus.Approved, WatchStatus.Active, WatchStatus.Cancelled],
        [WatchStatus.Approved] = [WatchStatus.Purchasing, WatchStatus.Cancelled],
        [WatchStatus.Purchasing] = [WatchStatus.Completed, WatchStatus.Active],
        [WatchStatus.Completed] = [],
        [WatchStatus.Expired] = [],
        [WatchStatus.Cancelled] = []
    };

    public void UpdateStatus(WatchStatus newStatus, string? reason = null)
    {
        if (Status == newStatus)
        {
            return;
        }

        if (!AllowedTransitions.TryGetValue(Status, out var allowed) || !allowed.Contains(newStatus))
        {
            throw new InvalidOperationException(
                $"Cannot transition from {Status} to {newStatus}.");
        }

        var change = new StatusChange(Status, newStatus, DateTimeOffset.UtcNow, reason);
        StatusHistory.Add(change);
        Status = newStatus;
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}
```

**File:** `src/AgentPayWatch.Domain/Entities/ProductMatch.cs`

```csharp
using AgentPayWatch.Domain.Enums;

namespace AgentPayWatch.Domain.Entities;

public sealed class ProductMatch
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WatchRequestId { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public string Currency { get; set; } = "USD";
    public string Seller { get; set; } = string.Empty;
    public string ProductUrl { get; set; } = string.Empty;
    public DateTimeOffset MatchedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
    public ProductAvailability Availability { get; set; } = ProductAvailability.InStock;
}
```

**File:** `src/AgentPayWatch.Domain/Entities/ApprovalRecord.cs`

```csharp
using AgentPayWatch.Domain.Enums;

namespace AgentPayWatch.Domain.Entities;

public sealed class ApprovalRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MatchId { get; set; }
    public Guid WatchRequestId { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string ApprovalToken { get; set; } = string.Empty;
    public DateTimeOffset SentAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RespondedAt { get; set; }
    public ApprovalDecision Decision { get; set; } = ApprovalDecision.Pending;
    public NotificationChannel Channel { get; set; } = NotificationChannel.A2P_SMS;
}
```

**File:** `src/AgentPayWatch.Domain/Entities/PaymentTransaction.cs`

```csharp
using AgentPayWatch.Domain.Enums;

namespace AgentPayWatch.Domain.Entities;

public sealed class PaymentTransaction
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MatchId { get; set; }
    public Guid ApprovalId { get; set; }
    public Guid WatchRequestId { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = Guid.NewGuid().ToString("N");
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "USD";
    public string Merchant { get; set; } = string.Empty;
    public PaymentStatus Status { get; set; } = PaymentStatus.Initiated;
    public string PaymentProviderRef { get; set; } = string.Empty;
    public DateTimeOffset InitiatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public string? FailureReason { get; set; }
}
```

### 1.5 Events

**File:** `src/AgentPayWatch.Domain/Events/ProductMatchFound.cs`

```csharp
namespace AgentPayWatch.Domain.Events;

public sealed record ProductMatchFound(
    Guid MessageId,
    Guid CorrelationId,
    DateTimeOffset Timestamp,
    string Source,
    Guid MatchId,
    string ProductName,
    decimal Price,
    string Currency,
    string Seller);
```

**File:** `src/AgentPayWatch.Domain/Events/ApprovalDecided.cs`

```csharp
using AgentPayWatch.Domain.Enums;

namespace AgentPayWatch.Domain.Events;

public sealed record ApprovalDecided(
    Guid MessageId,
    Guid CorrelationId,
    DateTimeOffset Timestamp,
    string Source,
    Guid ApprovalId,
    Guid MatchId,
    ApprovalDecision Decision);
```

**File:** `src/AgentPayWatch.Domain/Events/PaymentCompleted.cs`

```csharp
namespace AgentPayWatch.Domain.Events;

public sealed record PaymentCompleted(
    Guid MessageId,
    Guid CorrelationId,
    DateTimeOffset Timestamp,
    string Source,
    Guid TransactionId,
    decimal Amount,
    string Currency,
    string Merchant);
```

**File:** `src/AgentPayWatch.Domain/Events/PaymentFailed.cs`

```csharp
namespace AgentPayWatch.Domain.Events;

public sealed record PaymentFailed(
    Guid MessageId,
    Guid CorrelationId,
    DateTimeOffset Timestamp,
    string Source,
    Guid TransactionId,
    string Reason);
```

### 1.6 Models

**File:** `src/AgentPayWatch.Domain/Models/ProductListing.cs`

```csharp
using AgentPayWatch.Domain.Enums;

namespace AgentPayWatch.Domain.Models;

public sealed record ProductListing(
    string Name,
    decimal Price,
    string Currency,
    string Seller,
    string Url,
    ProductAvailability Availability);
```

### 1.7 Interfaces

**File:** `src/AgentPayWatch.Domain/Interfaces/IWatchRequestRepository.cs`

```csharp
using AgentPayWatch.Domain.Entities;
using AgentPayWatch.Domain.Enums;

namespace AgentPayWatch.Domain.Interfaces;

public interface IWatchRequestRepository
{
    Task<WatchRequest> CreateAsync(WatchRequest watchRequest);
    Task<WatchRequest?> GetByIdAsync(Guid id, string userId);
    Task<IReadOnlyList<WatchRequest>> GetByUserIdAsync(string userId);
    Task<IReadOnlyList<WatchRequest>> GetByStatusAsync(WatchStatus status);
    Task UpdateAsync(WatchRequest watchRequest);
}
```

**File:** `src/AgentPayWatch.Domain/Interfaces/IProductMatchRepository.cs`

```csharp
using AgentPayWatch.Domain.Entities;

namespace AgentPayWatch.Domain.Interfaces;

public interface IProductMatchRepository
{
    Task<ProductMatch> CreateAsync(ProductMatch match);
    Task<ProductMatch?> GetByIdAsync(Guid id, Guid watchRequestId);
    Task<IReadOnlyList<ProductMatch>> GetByWatchRequestIdAsync(Guid watchRequestId);
}
```

**File:** `src/AgentPayWatch.Domain/Interfaces/IApprovalRepository.cs`

```csharp
using AgentPayWatch.Domain.Entities;

namespace AgentPayWatch.Domain.Interfaces;

public interface IApprovalRepository
{
    Task<ApprovalRecord> CreateAsync(ApprovalRecord approval);
    Task<ApprovalRecord?> GetByIdAsync(Guid id, Guid watchRequestId);
    Task<ApprovalRecord?> GetByTokenAsync(string token);
    Task UpdateAsync(ApprovalRecord approval);
    Task<IReadOnlyList<ApprovalRecord>> GetPendingExpiredAsync(DateTimeOffset now);
}
```

**File:** `src/AgentPayWatch.Domain/Interfaces/IPaymentTransactionRepository.cs`

```csharp
using AgentPayWatch.Domain.Entities;

namespace AgentPayWatch.Domain.Interfaces;

public interface IPaymentTransactionRepository
{
    Task<PaymentTransaction> CreateAsync(PaymentTransaction transaction);
    Task<PaymentTransaction?> GetByIdAsync(Guid id, string userId);
    Task<IReadOnlyList<PaymentTransaction>> GetByUserIdAsync(string userId);
    Task UpdateAsync(PaymentTransaction transaction);
}
```

**File:** `src/AgentPayWatch.Domain/Interfaces/IProductSource.cs`

```csharp
using AgentPayWatch.Domain.Models;

namespace AgentPayWatch.Domain.Interfaces;

public interface IProductSource
{
    Task<IReadOnlyList<ProductListing>> SearchAsync(string productName, CancellationToken ct);
}
```

**File:** `src/AgentPayWatch.Domain/Interfaces/IEventPublisher.cs`

```csharp
namespace AgentPayWatch.Domain.Interfaces;

public interface IEventPublisher
{
    Task PublishAsync<T>(T message, string topicName, CancellationToken ct);
}
```

---

## Section 2: Empty Project Shells

### 2.1 Infrastructure (Class Library)

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
    <ProjectReference Include="..\AgentPayWatch.Domain\AgentPayWatch.Domain.csproj" />
  </ItemGroup>

</Project>
```

No `Program.cs` needed -- this is a class library. Create a placeholder file so the project compiles:

**File:** `src/AgentPayWatch.Infrastructure/Placeholder.cs`

```csharp
namespace AgentPayWatch.Infrastructure;

internal static class Placeholder
{
    // This file exists so the project compiles before repositories are added in Phase 2.
}
```

### 2.2 API

**File:** `src/AgentPayWatch.Api/AgentPayWatch.Api.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>AgentPayWatch.Api</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\AgentPayWatch.Domain\AgentPayWatch.Domain.csproj" />
    <ProjectReference Include="..\AgentPayWatch.Infrastructure\AgentPayWatch.Infrastructure.csproj" />
    <ProjectReference Include="..\AgentPayWatch.ServiceDefaults\AgentPayWatch.ServiceDefaults.csproj" />
  </ItemGroup>

</Project>
```

**File:** `src/AgentPayWatch.Api/Program.cs`

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

var app = builder.Build();

app.MapDefaultEndpoints();

app.Run();
```

### 2.3 Product Watch Agent

**File:** `src/AgentPayWatch.Agents.ProductWatch/AgentPayWatch.Agents.ProductWatch.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk.Worker">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>AgentPayWatch.Agents.ProductWatch</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\AgentPayWatch.Domain\AgentPayWatch.Domain.csproj" />
    <ProjectReference Include="..\AgentPayWatch.Infrastructure\AgentPayWatch.Infrastructure.csproj" />
    <ProjectReference Include="..\AgentPayWatch.ServiceDefaults\AgentPayWatch.ServiceDefaults.csproj" />
  </ItemGroup>

</Project>
```

**File:** `src/AgentPayWatch.Agents.ProductWatch/Program.cs`

```csharp
var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

var host = builder.Build();

host.Run();
```

### 2.4 Approval Agent

**File:** `src/AgentPayWatch.Agents.Approval/AgentPayWatch.Agents.Approval.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk.Worker">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>AgentPayWatch.Agents.Approval</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\AgentPayWatch.Domain\AgentPayWatch.Domain.csproj" />
    <ProjectReference Include="..\AgentPayWatch.Infrastructure\AgentPayWatch.Infrastructure.csproj" />
    <ProjectReference Include="..\AgentPayWatch.ServiceDefaults\AgentPayWatch.ServiceDefaults.csproj" />
  </ItemGroup>

</Project>
```

**File:** `src/AgentPayWatch.Agents.Approval/Program.cs`

```csharp
var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

var host = builder.Build();

host.Run();
```

### 2.5 Payment Agent

**File:** `src/AgentPayWatch.Agents.Payment/AgentPayWatch.Agents.Payment.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk.Worker">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>AgentPayWatch.Agents.Payment</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\AgentPayWatch.Domain\AgentPayWatch.Domain.csproj" />
    <ProjectReference Include="..\AgentPayWatch.Infrastructure\AgentPayWatch.Infrastructure.csproj" />
    <ProjectReference Include="..\AgentPayWatch.ServiceDefaults\AgentPayWatch.ServiceDefaults.csproj" />
  </ItemGroup>

</Project>
```

**File:** `src/AgentPayWatch.Agents.Payment/Program.cs`

```csharp
var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

var host = builder.Build();

host.Run();
```

### 2.6 Web (Blazor Server)

**File:** `src/AgentPayWatch.Web/AgentPayWatch.Web.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>AgentPayWatch.Web</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\AgentPayWatch.ServiceDefaults\AgentPayWatch.ServiceDefaults.csproj" />
  </ItemGroup>

</Project>
```

**File:** `src/AgentPayWatch.Web/Program.cs`

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAntiforgery();

app.MapDefaultEndpoints();
app.MapRazorComponents<AgentPayWatch.Web.Components.App>()
    .AddInteractiveServerRenderMode();

app.Run();
```

**File:** `src/AgentPayWatch.Web/Components/App.razor`

```razor
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <title>AgentPay Watch</title>
    <base href="/" />
    <HeadOutlet />
</head>
<body>
    <Routes />
    <script src="_framework/blazor.web.js"></script>
</body>
</html>
```

**File:** `src/AgentPayWatch.Web/Components/Routes.razor`

```razor
<Router AppAssembly="typeof(Program).Assembly">
    <Found Context="routeData">
        <RouteView RouteData="routeData" />
        <FocusOnNavigate RouteData="routeData" Selector="h1" />
    </Found>
</Router>
```

**File:** `src/AgentPayWatch.Web/Components/Pages/Home.razor`

```razor
@page "/"

<h1>AgentPay Watch</h1>
<p>MVP Dashboard - Coming soon.</p>
```

---

## Section 3: AppHost + Solution Wiring

### 3.1 Updated apphost.cs

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

var api = builder.AddProject<Projects.AgentPayWatch_Api>("api");

var productWatchAgent = builder.AddProject<Projects.AgentPayWatch_Agents_ProductWatch>("product-watch-agent");

var approvalAgent = builder.AddProject<Projects.AgentPayWatch_Agents_Approval>("approval-agent");

var paymentAgent = builder.AddProject<Projects.AgentPayWatch_Agents_Payment>("payment-agent");

var web = builder.AddProject<Projects.AgentPayWatch_Web>("web");

builder.Build().Run();
```

### 3.2 Updated AgentPayWatch.slnx

**File:** `AgentPayWatch.slnx`

```xml
<Solution>
  <Folder Name="/src/">
    <Project Path="src/AgentPayWatch.ServiceDefaults/AgentPayWatch.ServiceDefaults.csproj" />
    <Project Path="src/AgentPayWatch.Domain/AgentPayWatch.Domain.csproj" />
    <Project Path="src/AgentPayWatch.Infrastructure/AgentPayWatch.Infrastructure.csproj" />
    <Project Path="src/AgentPayWatch.Api/AgentPayWatch.Api.csproj" />
    <Project Path="src/AgentPayWatch.Agents.ProductWatch/AgentPayWatch.Agents.ProductWatch.csproj" />
    <Project Path="src/AgentPayWatch.Agents.Approval/AgentPayWatch.Agents.Approval.csproj" />
    <Project Path="src/AgentPayWatch.Agents.Payment/AgentPayWatch.Agents.Payment.csproj" />
    <Project Path="src/AgentPayWatch.Web/AgentPayWatch.Web.csproj" />
  </Folder>
</Solution>
```

---

## Section 4: Verification

### Step 1: Build the solution

```bash
dotnet build AgentPayWatch.slnx
```

**Expected result:** Build succeeds with `0 Error(s)`. All 8 projects compile. You may see warnings -- zero errors is what matters.

### Step 2: Run the Aspire AppHost

```bash
dotnet run --project appHost/apphost.cs
```

**Expected result:**
- The Aspire dashboard opens (typically at `https://localhost:17225` or a similar port displayed in the console output).
- The dashboard shows 5 resources: `api`, `product-watch-agent`, `approval-agent`, `payment-agent`, `web`.
- All resources show a **Running** state (green indicators).
- Each resource has health check endpoints: `/health` and `/alive`.

### Step 3: Verify health endpoints

```bash
curl -k https://localhost:<api-port>/health
```

**Expected result:** `Healthy` response with HTTP 200 status code.

### Troubleshooting

| Symptom | Fix |
|---------|-----|
| `NETSDK1045: The current .NET SDK does not support targeting .NET 10.0` | Install .NET 10.0 SDK. Verify with `dotnet --list-sdks`. |
| `error CS0234: The type or namespace name 'Projects' does not exist` | Ensure all `#:project` directives in `apphost.cs` point to the correct relative paths. Each referenced project folder must contain a `.csproj` file. |
| Blazor Web app fails with missing component | Ensure `Components/App.razor`, `Components/Routes.razor`, and `Components/Pages/Home.razor` all exist. |
| AppHost shows resources in **Error** state | Check each resource's logs in the Aspire dashboard. Common cause: port conflicts. |
