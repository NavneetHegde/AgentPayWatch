# Phase 7: Blazor Web UI -- Interactive Dashboard

**Goal:** Full visual demo. Create watches, see real-time status changes, approve/reject matches, view transactions -- all in the browser. No more curl commands needed.

**Prerequisite:** Phase 6 complete (full end-to-end flow working via curl).

---

## Section 1: Project Setup

**File:** `src/AgentPayWatch.Web/AgentPayWatch.Web.csproj`

Blazor Server project referencing only ServiceDefaults. No additional NuGet packages required -- the built-in Blazor framework provides everything needed.

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

---

## Section 2: Program.cs

**File:** `src/AgentPayWatch.Web/Program.cs`

```csharp
using AgentPayWatch.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddHttpClient<ApiClient>(client =>
{
    client.BaseAddress = new Uri("https+http://api");
});

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

---

## Section 3: API Client Service

**File:** `src/AgentPayWatch.Web/Services/ApiClient.cs`

```csharp
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentPayWatch.Web.Services;

public sealed class ApiClient
{
    private readonly HttpClient _httpClient;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public ApiClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<WatchResponse?> CreateWatchAsync(CreateWatchFormModel model)
    {
        var request = new
        {
            productName = model.ProductName,
            maxPrice = model.MaxPrice,
            currency = model.Currency,
            userId = model.UserId,
            preferredSellers = string.IsNullOrWhiteSpace(model.PreferredSellers)
                ? Array.Empty<string>()
                : model.PreferredSellers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        };

        var response = await _httpClient.PostAsJsonAsync("/api/watches", request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<WatchResponse>(JsonOptions);
    }

    public async Task<List<WatchResponse>> GetWatchesAsync(string userId)
    {
        var response = await _httpClient.GetFromJsonAsync<List<WatchResponse>>(
            $"/api/watches?userId={Uri.EscapeDataString(userId)}", JsonOptions);
        return response ?? [];
    }

    public async Task<WatchResponse?> GetWatchAsync(Guid id, string userId)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<WatchResponse>(
                $"/api/watches/{id}?userId={Uri.EscapeDataString(userId)}", JsonOptions);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    public async Task<List<MatchResponse>> GetMatchesAsync(Guid watchId)
    {
        var response = await _httpClient.GetFromJsonAsync<List<MatchResponse>>(
            $"/api/matches/{watchId}", JsonOptions);
        return response ?? [];
    }

    public async Task<List<TransactionResponse>> GetTransactionsAsync(string userId)
    {
        var response = await _httpClient.GetFromJsonAsync<List<TransactionResponse>>(
            $"/api/transactions?userId={Uri.EscapeDataString(userId)}", JsonOptions);
        return response ?? [];
    }

    public async Task<bool> SubmitApprovalAsync(string token, string decision)
    {
        var request = new { token, decision };
        var response = await _httpClient.PostAsJsonAsync("/api/a2p/callback", request);
        return response.IsSuccessStatusCode;
    }

    public async Task<DashboardData> GetDashboardAsync(string userId)
    {
        var watches = await GetWatchesAsync(userId);
        var transactions = await GetTransactionsAsync(userId);

        return new DashboardData
        {
            ActiveWatchCount = watches.Count(w =>
                w.Status is "Active" or "Matched" or "Paused"),
            PendingApprovalCount = watches.Count(w =>
                w.Status is "AwaitingApproval"),
            CompletedPurchaseCount = watches.Count(w =>
                w.Status is "Completed"),
            RecentWatches = watches
                .OrderByDescending(w => w.UpdatedAt)
                .Take(5)
                .ToList(),
            RecentTransactions = transactions
                .OrderByDescending(t => t.InitiatedAt)
                .Take(5)
                .ToList()
        };
    }
}

public sealed class CreateWatchFormModel
{
    public string ProductName { get; set; } = string.Empty;
    public decimal MaxPrice { get; set; }
    public string Currency { get; set; } = "USD";
    public string? PreferredSellers { get; set; }
    public string UserId { get; set; } = "demo-user";
}

public sealed class WatchResponse
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public decimal MaxPrice { get; set; }
    public string Currency { get; set; } = "USD";
    public string[] PreferredSellers { get; set; } = [];
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string PaymentMethodToken { get; set; } = string.Empty;
    public List<StatusChangeResponse> StatusHistory { get; set; } = [];
}

public sealed class StatusChangeResponse
{
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
    public DateTimeOffset ChangedAt { get; set; }
    public string? Reason { get; set; }
}

public sealed class MatchResponse
{
    public Guid Id { get; set; }
    public Guid WatchRequestId { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public string Currency { get; set; } = "USD";
    public string Seller { get; set; } = string.Empty;
    public string ProductUrl { get; set; } = string.Empty;
    public DateTimeOffset MatchedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public string Availability { get; set; } = string.Empty;
    public string? ApprovalToken { get; set; }
}

public sealed class TransactionResponse
{
    public Guid Id { get; set; }
    public Guid MatchId { get; set; }
    public Guid ApprovalId { get; set; }
    public Guid WatchRequestId { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "USD";
    public string Merchant { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string PaymentProviderRef { get; set; } = string.Empty;
    public DateTimeOffset InitiatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? FailureReason { get; set; }
}

public sealed class DashboardData
{
    public int ActiveWatchCount { get; set; }
    public int PendingApprovalCount { get; set; }
    public int CompletedPurchaseCount { get; set; }
    public List<WatchResponse> RecentWatches { get; set; } = [];
    public List<TransactionResponse> RecentTransactions { get; set; } = [];
}
```

---

## Section 4: App Shell

### 4.1 App.razor

**File:** `src/AgentPayWatch.Web/Components/App.razor`

```razor
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <title>AgentPay Watch</title>
    <base href="/" />
    <link rel="stylesheet" href="css/app.css" />
    <HeadOutlet />
</head>
<body>
    <Routes />
    <script src="_framework/blazor.web.js"></script>
</body>
</html>
```

### 4.2 Routes.razor

**File:** `src/AgentPayWatch.Web/Components/Routes.razor`

```razor
<Router AppAssembly="typeof(Program).Assembly">
    <Found Context="routeData">
        <RouteView RouteData="routeData" DefaultLayout="typeof(Layout.MainLayout)" />
        <FocusOnNavigate RouteData="routeData" Selector="h1" />
    </Found>
    <NotFound>
        <LayoutView Layout="typeof(Layout.MainLayout)">
            <div class="not-found">
                <h2>Page not found</h2>
                <p>The page you requested could not be found.</p>
            </div>
        </LayoutView>
    </NotFound>
</Router>
```

### 4.3 _Imports.razor

**File:** `src/AgentPayWatch.Web/Components/_Imports.razor`

```razor
@using System.Net.Http
@using System.Net.Http.Json
@using Microsoft.AspNetCore.Components.Forms
@using Microsoft.AspNetCore.Components.Routing
@using Microsoft.AspNetCore.Components.Web
@using Microsoft.AspNetCore.Components.Web.Virtualization
@using Microsoft.JSInterop
@using AgentPayWatch.Web
@using AgentPayWatch.Web.Components
@using AgentPayWatch.Web.Components.Layout
@using AgentPayWatch.Web.Services
```

---

## Section 5: Layout

### 5.1 MainLayout.razor

**File:** `src/AgentPayWatch.Web/Components/Layout/MainLayout.razor`

```razor
@inherits LayoutComponentBase

<div class="app-layout">
    <aside class="sidebar">
        <div class="sidebar-header">
            <h2 class="brand-title">AgentPay Watch</h2>
            <span class="brand-user">demo-user</span>
        </div>
        <NavMenu />
    </aside>
    <main class="main-content">
        <header class="top-bar">
            <span class="top-bar-title">Agent-Powered Purchase Automation</span>
        </header>
        <div class="content-area">
            @Body
        </div>
    </main>
</div>
```

### 5.2 NavMenu.razor

**File:** `src/AgentPayWatch.Web/Components/Layout/NavMenu.razor`

```razor
<nav class="nav-menu">
    <ul>
        <li>
            <NavLink href="" Match="NavLinkMatch.All" class="nav-link" ActiveClass="nav-link-active">
                <span class="nav-icon">&#9632;</span> Dashboard
            </NavLink>
        </li>
        <li>
            <NavLink href="create-watch" class="nav-link" ActiveClass="nav-link-active">
                <span class="nav-icon">+</span> Create Watch
            </NavLink>
        </li>
        <li>
            <NavLink href="watches" class="nav-link" ActiveClass="nav-link-active">
                <span class="nav-icon">&#9673;</span> Watches
            </NavLink>
        </li>
        <li>
            <NavLink href="transactions" class="nav-link" ActiveClass="nav-link-active">
                <span class="nav-icon">$</span> Transactions
            </NavLink>
        </li>
    </ul>
</nav>
```

---

## Section 6: Pages

### 6.1 Dashboard.razor

**File:** `src/AgentPayWatch.Web/Components/Pages/Dashboard.razor`

```razor
@page "/"
@using AgentPayWatch.Web.Services
@inject ApiClient Api
@implements IDisposable
@rendermode InteractiveServer

<h1>Dashboard</h1>

@if (_loading)
{
    <p class="loading-text">Loading dashboard...</p>
}
else if (_error is not null)
{
    <p class="error-text">@_error</p>
}
else
{
    <div class="card-row">
        <a href="/watches" class="summary-card card-blue">
            <div class="card-count">@_data.ActiveWatchCount</div>
            <div class="card-label">Active Watches</div>
        </a>
        <a href="/watches" class="summary-card card-orange">
            <div class="card-count">@_data.PendingApprovalCount</div>
            <div class="card-label">Pending Approvals</div>
        </a>
        <a href="/transactions" class="summary-card card-green">
            <div class="card-count">@_data.CompletedPurchaseCount</div>
            <div class="card-label">Completed Purchases</div>
        </a>
    </div>

    @if (_data.RecentWatches.Count > 0)
    {
        <h2>Recent Watches</h2>
        <table class="data-table">
            <thead>
                <tr>
                    <th>Product</th>
                    <th>Max Price</th>
                    <th>Status</th>
                    <th>Updated</th>
                </tr>
            </thead>
            <tbody>
                @foreach (var watch in _data.RecentWatches)
                {
                    <tr>
                        <td><a href="/watches/@watch.Id">@watch.ProductName</a></td>
                        <td>@watch.MaxPrice.ToString("C2") @watch.Currency</td>
                        <td><span class="badge @GetStatusBadgeClass(watch.Status)">@watch.Status</span></td>
                        <td>@watch.UpdatedAt.LocalDateTime.ToString("g")</td>
                    </tr>
                }
            </tbody>
        </table>
    }
}

@code {
    private DashboardData _data = new();
    private bool _loading = true;
    private string? _error;
    private Timer? _refreshTimer;

    protected override async Task OnInitializedAsync()
    {
        await LoadDataAsync();

        _refreshTimer = new Timer(async _ =>
        {
            await LoadDataAsync();
            await InvokeAsync(StateHasChanged);
        }, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    private async Task LoadDataAsync()
    {
        try
        {
            _data = await Api.GetDashboardAsync("demo-user");
            _error = null;
        }
        catch (Exception ex)
        {
            _error = $"Failed to load dashboard: {ex.Message}";
        }
        finally
        {
            _loading = false;
        }
    }

    private static string GetStatusBadgeClass(string status) => status switch
    {
        "Active" => "badge-blue",
        "Paused" => "badge-blue",
        "Matched" => "badge-amber",
        "AwaitingApproval" => "badge-orange",
        "Approved" => "badge-purple",
        "Purchasing" => "badge-purple",
        "Completed" => "badge-green",
        "Expired" => "badge-red",
        "Cancelled" => "badge-red",
        "Failed" => "badge-red",
        _ => "badge-blue"
    };

    public void Dispose()
    {
        _refreshTimer?.Dispose();
    }
}
```

### 6.2 CreateWatch.razor

**File:** `src/AgentPayWatch.Web/Components/Pages/CreateWatch.razor`

```razor
@page "/create-watch"
@using AgentPayWatch.Web.Services
@inject ApiClient Api
@inject NavigationManager Navigation
@rendermode InteractiveServer

<h1>Create Watch</h1>

<div class="form-card">
    <EditForm Model="_model" OnValidSubmit="HandleSubmitAsync" FormName="create-watch">
        <div class="form-group">
            <label for="productName">Product Name *</label>
            <InputText id="productName" @bind-Value="_model.ProductName" class="form-input" placeholder="e.g. iPhone 15 Pro" />
            @if (_submitted && string.IsNullOrWhiteSpace(_model.ProductName))
            {
                <span class="field-error">Product name is required.</span>
            }
        </div>

        <div class="form-group">
            <label for="maxPrice">Max Price *</label>
            <InputNumber id="maxPrice" @bind-Value="_model.MaxPrice" class="form-input" placeholder="999.00" />
            @if (_submitted && _model.MaxPrice <= 0)
            {
                <span class="field-error">Price must be greater than zero.</span>
            }
        </div>

        <div class="form-group">
            <label for="currency">Currency</label>
            <InputSelect id="currency" @bind-Value="_model.Currency" class="form-input">
                <option value="USD">USD</option>
                <option value="EUR">EUR</option>
                <option value="GBP">GBP</option>
            </InputSelect>
        </div>

        <div class="form-group">
            <label for="sellers">Preferred Sellers (comma-separated, optional)</label>
            <InputText id="sellers" @bind-Value="_model.PreferredSellers" class="form-input" placeholder="e.g. Amazon, Best Buy" />
        </div>

        <div class="form-actions">
            <button type="submit" class="btn btn-primary" disabled="@_submitting">
                @if (_submitting)
                {
                    <span>Creating...</span>
                }
                else
                {
                    <span>Create Watch</span>
                }
            </button>
        </div>

        @if (_error is not null)
        {
            <p class="error-text">@_error</p>
        }
    </EditForm>
</div>

@code {
    private CreateWatchFormModel _model = new();
    private bool _submitting;
    private bool _submitted;
    private string? _error;

    private async Task HandleSubmitAsync()
    {
        _submitted = true;
        _error = null;

        if (string.IsNullOrWhiteSpace(_model.ProductName) || _model.MaxPrice <= 0)
        {
            return;
        }

        _submitting = true;

        try
        {
            _model.UserId = "demo-user";
            var result = await Api.CreateWatchAsync(_model);
            if (result is not null)
            {
                Navigation.NavigateTo("/watches");
            }
            else
            {
                _error = "Failed to create watch. The API returned an empty response.";
            }
        }
        catch (Exception ex)
        {
            _error = $"Failed to create watch: {ex.Message}";
        }
        finally
        {
            _submitting = false;
        }
    }
}
```

### 6.3 WatchList.razor

**File:** `src/AgentPayWatch.Web/Components/Pages/WatchList.razor`

```razor
@page "/watches"
@using AgentPayWatch.Web.Services
@inject ApiClient Api
@implements IDisposable
@rendermode InteractiveServer

<h1>Watches</h1>

@if (_loading)
{
    <p class="loading-text">Loading watches...</p>
}
else if (_error is not null)
{
    <p class="error-text">@_error</p>
}
else if (_watches.Count == 0)
{
    <div class="empty-state">
        <p>No watches found. <a href="/create-watch">Create your first watch</a> to get started.</p>
    </div>
}
else
{
    <table class="data-table">
        <thead>
            <tr>
                <th>Product Name</th>
                <th>Max Price</th>
                <th>Status</th>
                <th>Created</th>
                <th>Actions</th>
            </tr>
        </thead>
        <tbody>
            @foreach (var watch in _watches)
            {
                <tr>
                    <td>@watch.ProductName</td>
                    <td>@watch.MaxPrice.ToString("C2") @watch.Currency</td>
                    <td><span class="badge @GetStatusBadgeClass(watch.Status)">@watch.Status</span></td>
                    <td>@watch.CreatedAt.LocalDateTime.ToString("g")</td>
                    <td><a href="/watches/@watch.Id" class="btn btn-small">View</a></td>
                </tr>
            }
        </tbody>
    </table>
}

@code {
    private List<WatchResponse> _watches = [];
    private bool _loading = true;
    private string? _error;
    private Timer? _refreshTimer;

    protected override async Task OnInitializedAsync()
    {
        await LoadWatchesAsync();

        _refreshTimer = new Timer(async _ =>
        {
            await LoadWatchesAsync();
            await InvokeAsync(StateHasChanged);
        }, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    private async Task LoadWatchesAsync()
    {
        try
        {
            _watches = await Api.GetWatchesAsync("demo-user");
            _error = null;
        }
        catch (Exception ex)
        {
            _error = $"Failed to load watches: {ex.Message}";
        }
        finally
        {
            _loading = false;
        }
    }

    private static string GetStatusBadgeClass(string status) => status switch
    {
        "Active" => "badge-blue",
        "Paused" => "badge-blue",
        "Matched" => "badge-amber",
        "AwaitingApproval" => "badge-orange",
        "Approved" => "badge-purple",
        "Purchasing" => "badge-purple",
        "Completed" => "badge-green",
        "Expired" => "badge-red",
        "Cancelled" => "badge-red",
        "Failed" => "badge-red",
        _ => "badge-blue"
    };

    public void Dispose()
    {
        _refreshTimer?.Dispose();
    }
}
```

### 6.4 WatchDetail.razor

**File:** `src/AgentPayWatch.Web/Components/Pages/WatchDetail.razor`

```razor
@page "/watches/{Id:guid}"
@using AgentPayWatch.Web.Services
@inject ApiClient Api
@implements IDisposable
@rendermode InteractiveServer

<h1>Watch Detail</h1>

@if (_loading)
{
    <p class="loading-text">Loading watch details...</p>
}
else if (_watch is null)
{
    <div class="empty-state">
        <p>Watch not found. <a href="/watches">Back to watches</a></p>
    </div>
}
else
{
    <div class="detail-card">
        <div class="detail-header">
            <h2>@_watch.ProductName</h2>
            <span class="badge badge-large @GetStatusBadgeClass(_watch.Status)">@_watch.Status</span>
        </div>

        <div class="detail-grid">
            <div class="detail-item">
                <span class="detail-label">Max Price</span>
                <span class="detail-value">@_watch.MaxPrice.ToString("C2") @_watch.Currency</span>
            </div>
            <div class="detail-item">
                <span class="detail-label">Created</span>
                <span class="detail-value">@_watch.CreatedAt.LocalDateTime.ToString("g")</span>
            </div>
            <div class="detail-item">
                <span class="detail-label">Updated</span>
                <span class="detail-value">@_watch.UpdatedAt.LocalDateTime.ToString("g")</span>
            </div>
            <div class="detail-item">
                <span class="detail-label">User</span>
                <span class="detail-value">@_watch.UserId</span>
            </div>
        </div>
    </div>

    @if (_watch.Status == "AwaitingApproval" && _currentMatch is not null)
    {
        <div class="match-card">
            <h3>Match Found -- Action Required</h3>
            <div class="detail-grid">
                <div class="detail-item">
                    <span class="detail-label">Product</span>
                    <span class="detail-value">@_currentMatch.ProductName</span>
                </div>
                <div class="detail-item">
                    <span class="detail-label">Price</span>
                    <span class="detail-value">@_currentMatch.Price.ToString("C2") @_currentMatch.Currency</span>
                </div>
                <div class="detail-item">
                    <span class="detail-label">Seller</span>
                    <span class="detail-value">@_currentMatch.Seller</span>
                </div>
                <div class="detail-item">
                    <span class="detail-label">Availability</span>
                    <span class="detail-value">@_currentMatch.Availability</span>
                </div>
                @if (!string.IsNullOrEmpty(_currentMatch.ProductUrl))
                {
                    <div class="detail-item">
                        <span class="detail-label">URL</span>
                        <span class="detail-value"><a href="@_currentMatch.ProductUrl" target="_blank">View Product</a></span>
                    </div>
                }
            </div>

            @if (!string.IsNullOrEmpty(_currentMatch.ApprovalToken))
            {
                <div class="approval-actions">
                    <button class="btn btn-approve" disabled="@_actionInProgress" @onclick="ApproveAsync">
                        @if (_actionInProgress)
                        {
                            <span>Processing...</span>
                        }
                        else
                        {
                            <span>Approve Purchase</span>
                        }
                    </button>
                    <button class="btn btn-reject" disabled="@_actionInProgress" @onclick="RejectAsync">
                        @if (_actionInProgress)
                        {
                            <span>Processing...</span>
                        }
                        else
                        {
                            <span>Reject</span>
                        }
                    </button>
                </div>
            }

            @if (_actionError is not null)
            {
                <p class="error-text">@_actionError</p>
            }

            @if (_actionSuccess is not null)
            {
                <p class="success-text">@_actionSuccess</p>
            }
        </div>
    }

    @if (_watch.StatusHistory.Count > 0)
    {
        <div class="timeline-card">
            <h3>Status Timeline</h3>
            <div class="timeline">
                @foreach (var change in _watch.StatusHistory.OrderByDescending(s => s.ChangedAt))
                {
                    <div class="timeline-entry">
                        <div class="timeline-marker"></div>
                        <div class="timeline-content">
                            <div class="timeline-transition">
                                <span class="badge badge-small @GetStatusBadgeClass(change.From)">@change.From</span>
                                <span class="timeline-arrow">&rarr;</span>
                                <span class="badge badge-small @GetStatusBadgeClass(change.To)">@change.To</span>
                            </div>
                            <div class="timeline-time">@change.ChangedAt.LocalDateTime.ToString("g")</div>
                            @if (!string.IsNullOrEmpty(change.Reason))
                            {
                                <div class="timeline-reason">@change.Reason</div>
                            }
                        </div>
                    </div>
                }
            </div>
        </div>
    }
}

@code {
    [Parameter]
    public Guid Id { get; set; }

    private WatchResponse? _watch;
    private MatchResponse? _currentMatch;
    private List<MatchResponse> _matches = [];
    private bool _loading = true;
    private bool _actionInProgress;
    private string? _actionError;
    private string? _actionSuccess;
    private Timer? _refreshTimer;

    protected override async Task OnInitializedAsync()
    {
        await LoadDataAsync();

        _refreshTimer = new Timer(async _ =>
        {
            await LoadDataAsync();
            await InvokeAsync(StateHasChanged);
        }, null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
    }

    private async Task LoadDataAsync()
    {
        try
        {
            _watch = await Api.GetWatchAsync(Id, "demo-user");

            if (_watch is not null)
            {
                _matches = await Api.GetMatchesAsync(Id);
                _currentMatch = _matches
                    .OrderByDescending(m => m.MatchedAt)
                    .FirstOrDefault();
            }
        }
        catch (Exception)
        {
            // Silently handle refresh errors to avoid disrupting the UI
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task ApproveAsync()
    {
        if (_currentMatch?.ApprovalToken is null) return;

        _actionInProgress = true;
        _actionError = null;
        _actionSuccess = null;

        try
        {
            var success = await Api.SubmitApprovalAsync(_currentMatch.ApprovalToken, "BUY");
            if (success)
            {
                _actionSuccess = "Purchase approved. Payment is being processed...";
            }
            else
            {
                _actionError = "Failed to submit approval. The token may have expired.";
            }
        }
        catch (Exception ex)
        {
            _actionError = $"Error submitting approval: {ex.Message}";
        }
        finally
        {
            _actionInProgress = false;
        }
    }

    private async Task RejectAsync()
    {
        if (_currentMatch?.ApprovalToken is null) return;

        _actionInProgress = true;
        _actionError = null;
        _actionSuccess = null;

        try
        {
            var success = await Api.SubmitApprovalAsync(_currentMatch.ApprovalToken, "SKIP");
            if (success)
            {
                _actionSuccess = "Match rejected. The watch will return to active scanning.";
            }
            else
            {
                _actionError = "Failed to submit rejection. The token may have expired.";
            }
        }
        catch (Exception ex)
        {
            _actionError = $"Error submitting rejection: {ex.Message}";
        }
        finally
        {
            _actionInProgress = false;
        }
    }

    private static string GetStatusBadgeClass(string status) => status switch
    {
        "Active" => "badge-blue",
        "Paused" => "badge-blue",
        "Matched" => "badge-amber",
        "AwaitingApproval" => "badge-orange",
        "Approved" => "badge-purple",
        "Purchasing" => "badge-purple",
        "Completed" => "badge-green",
        "Expired" => "badge-red",
        "Cancelled" => "badge-red",
        "Failed" => "badge-red",
        _ => "badge-blue"
    };

    public void Dispose()
    {
        _refreshTimer?.Dispose();
    }
}
```

### 6.5 Transactions.razor

**File:** `src/AgentPayWatch.Web/Components/Pages/Transactions.razor`

```razor
@page "/transactions"
@using AgentPayWatch.Web.Services
@inject ApiClient Api
@implements IDisposable
@rendermode InteractiveServer

<h1>Transactions</h1>

@if (_loading)
{
    <p class="loading-text">Loading transactions...</p>
}
else if (_error is not null)
{
    <p class="error-text">@_error</p>
}
else if (_transactions.Count == 0)
{
    <div class="empty-state">
        <p>No transactions yet. Transactions appear here after approved purchases are processed by the payment agent.</p>
    </div>
}
else
{
    <table class="data-table">
        <thead>
            <tr>
                <th>Date</th>
                <th>Product</th>
                <th>Amount</th>
                <th>Merchant</th>
                <th>Status</th>
                <th>Provider Ref</th>
            </tr>
        </thead>
        <tbody>
            @foreach (var tx in _transactions)
            {
                <tr>
                    <td>@tx.InitiatedAt.LocalDateTime.ToString("g")</td>
                    <td>@tx.WatchRequestId.ToString()[..8]...</td>
                    <td>@tx.Amount.ToString("C2") @tx.Currency</td>
                    <td>@tx.Merchant</td>
                    <td><span class="badge @GetTransactionBadgeClass(tx.Status)">@tx.Status</span></td>
                    <td>
                        @if (!string.IsNullOrEmpty(tx.PaymentProviderRef))
                        {
                            <span class="provider-ref">@tx.PaymentProviderRef[..Math.Min(16, tx.PaymentProviderRef.Length)]...</span>
                        }
                        else if (!string.IsNullOrEmpty(tx.FailureReason))
                        {
                            <span class="failure-reason">@tx.FailureReason</span>
                        }
                        else
                        {
                            <span>--</span>
                        }
                    </td>
                </tr>
            }
        </tbody>
    </table>
}

@code {
    private List<TransactionResponse> _transactions = [];
    private bool _loading = true;
    private string? _error;
    private Timer? _refreshTimer;

    protected override async Task OnInitializedAsync()
    {
        await LoadTransactionsAsync();

        _refreshTimer = new Timer(async _ =>
        {
            await LoadTransactionsAsync();
            await InvokeAsync(StateHasChanged);
        }, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    private async Task LoadTransactionsAsync()
    {
        try
        {
            _transactions = await Api.GetTransactionsAsync("demo-user");
            _error = null;
        }
        catch (Exception ex)
        {
            _error = $"Failed to load transactions: {ex.Message}";
        }
        finally
        {
            _loading = false;
        }
    }

    private static string GetTransactionBadgeClass(string status) => status switch
    {
        "Succeeded" => "badge-green",
        "Failed" => "badge-red",
        "Processing" => "badge-blue",
        "Initiated" => "badge-blue",
        _ => "badge-blue"
    };

    public void Dispose()
    {
        _refreshTimer?.Dispose();
    }
}
```

---

## Section 7: Stylesheet

**File:** `src/AgentPayWatch.Web/wwwroot/css/app.css`

```css
/* ===== Reset & Base ===== */
*,
*::before,
*::after {
    box-sizing: border-box;
    margin: 0;
    padding: 0;
}

html, body {
    height: 100%;
    font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, "Helvetica Neue", Arial, sans-serif;
    font-size: 14px;
    line-height: 1.5;
    color: #1a1a2e;
    background-color: #f0f2f5;
}

a {
    color: #0d6efd;
    text-decoration: none;
}

a:hover {
    text-decoration: underline;
}

/* ===== App Layout ===== */
.app-layout {
    display: flex;
    min-height: 100vh;
}

/* ===== Sidebar ===== */
.sidebar {
    width: 240px;
    background-color: #1a1a2e;
    color: #e0e0e0;
    display: flex;
    flex-direction: column;
    flex-shrink: 0;
}

.sidebar-header {
    padding: 24px 20px 16px;
    border-bottom: 1px solid #2d2d44;
}

.brand-title {
    font-size: 18px;
    font-weight: 700;
    color: #ffffff;
    margin-bottom: 4px;
}

.brand-user {
    font-size: 12px;
    color: #8888aa;
}

/* ===== Navigation ===== */
.nav-menu ul {
    list-style: none;
    padding: 12px 0;
}

.nav-menu li {
    margin: 2px 0;
}

.nav-link {
    display: flex;
    align-items: center;
    gap: 10px;
    padding: 10px 20px;
    color: #b0b0cc;
    text-decoration: none;
    font-size: 14px;
    transition: background-color 0.15s, color 0.15s;
}

.nav-link:hover {
    background-color: #2d2d44;
    color: #ffffff;
    text-decoration: none;
}

.nav-link-active {
    background-color: #0d6efd;
    color: #ffffff;
}

.nav-link-active:hover {
    background-color: #0b5ed7;
}

.nav-icon {
    width: 20px;
    text-align: center;
    font-size: 16px;
}

/* ===== Main Content ===== */
.main-content {
    flex: 1;
    display: flex;
    flex-direction: column;
    overflow-x: hidden;
}

.top-bar {
    background-color: #ffffff;
    padding: 14px 32px;
    border-bottom: 1px solid #dee2e6;
    box-shadow: 0 1px 3px rgba(0, 0, 0, 0.04);
}

.top-bar-title {
    font-size: 14px;
    color: #6c757d;
    font-weight: 500;
}

.content-area {
    padding: 32px;
    flex: 1;
}

/* ===== Headings ===== */
h1 {
    font-size: 24px;
    font-weight: 700;
    color: #1a1a2e;
    margin-bottom: 24px;
}

h2 {
    font-size: 18px;
    font-weight: 600;
    color: #1a1a2e;
    margin-bottom: 16px;
    margin-top: 32px;
}

h3 {
    font-size: 16px;
    font-weight: 600;
    color: #1a1a2e;
    margin-bottom: 12px;
}

/* ===== Summary Cards ===== */
.card-row {
    display: flex;
    gap: 20px;
    margin-bottom: 32px;
    flex-wrap: wrap;
}

.summary-card {
    flex: 1;
    min-width: 180px;
    max-width: 280px;
    padding: 24px;
    border-radius: 10px;
    color: #ffffff;
    text-decoration: none;
    transition: transform 0.15s, box-shadow 0.15s;
    box-shadow: 0 2px 8px rgba(0, 0, 0, 0.1);
}

.summary-card:hover {
    transform: translateY(-2px);
    box-shadow: 0 4px 16px rgba(0, 0, 0, 0.15);
    text-decoration: none;
    color: #ffffff;
}

.card-blue {
    background: linear-gradient(135deg, #0d6efd, #0b5ed7);
}

.card-orange {
    background: linear-gradient(135deg, #fd7e14, #e86b00);
}

.card-green {
    background: linear-gradient(135deg, #198754, #157347);
}

.card-count {
    font-size: 36px;
    font-weight: 700;
    line-height: 1.1;
    margin-bottom: 4px;
}

.card-label {
    font-size: 13px;
    opacity: 0.9;
    font-weight: 500;
}

/* ===== Data Tables ===== */
.data-table {
    width: 100%;
    border-collapse: collapse;
    background-color: #ffffff;
    border-radius: 8px;
    overflow: hidden;
    box-shadow: 0 1px 4px rgba(0, 0, 0, 0.06);
}

.data-table thead {
    background-color: #f8f9fa;
}

.data-table th {
    padding: 12px 16px;
    text-align: left;
    font-weight: 600;
    font-size: 12px;
    text-transform: uppercase;
    letter-spacing: 0.05em;
    color: #6c757d;
    border-bottom: 2px solid #dee2e6;
}

.data-table td {
    padding: 12px 16px;
    border-bottom: 1px solid #f0f0f0;
    font-size: 14px;
}

.data-table tbody tr:hover {
    background-color: #f8f9fa;
}

.data-table tbody tr:last-child td {
    border-bottom: none;
}

/* ===== Badges ===== */
.badge {
    display: inline-block;
    padding: 3px 10px;
    border-radius: 12px;
    font-size: 12px;
    font-weight: 600;
    text-transform: uppercase;
    letter-spacing: 0.03em;
    white-space: nowrap;
}

.badge-large {
    padding: 5px 14px;
    font-size: 13px;
}

.badge-small {
    padding: 2px 8px;
    font-size: 11px;
}

.badge-blue {
    background-color: #e7f1ff;
    color: #0d6efd;
}

.badge-amber {
    background-color: #fff8e1;
    color: #b8860b;
}

.badge-orange {
    background-color: #fff3e0;
    color: #e65100;
}

.badge-purple {
    background-color: #f3e5f5;
    color: #6f42c1;
}

.badge-green {
    background-color: #e8f5e9;
    color: #198754;
}

.badge-red {
    background-color: #fce4ec;
    color: #dc3545;
}

/* ===== Cards ===== */
.detail-card,
.match-card,
.timeline-card,
.form-card {
    background-color: #ffffff;
    border-radius: 10px;
    padding: 24px;
    margin-bottom: 20px;
    box-shadow: 0 1px 4px rgba(0, 0, 0, 0.06);
}

.detail-header {
    display: flex;
    align-items: center;
    justify-content: space-between;
    margin-bottom: 20px;
    padding-bottom: 16px;
    border-bottom: 1px solid #f0f0f0;
}

.detail-header h2 {
    margin: 0;
}

.detail-grid {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(200px, 1fr));
    gap: 16px;
}

.detail-item {
    display: flex;
    flex-direction: column;
    gap: 4px;
}

.detail-label {
    font-size: 12px;
    font-weight: 600;
    text-transform: uppercase;
    letter-spacing: 0.05em;
    color: #6c757d;
}

.detail-value {
    font-size: 14px;
    color: #1a1a2e;
    font-weight: 500;
}

/* ===== Match Card ===== */
.match-card {
    border-left: 4px solid #fd7e14;
}

.match-card h3 {
    color: #e65100;
}

/* ===== Approval Actions ===== */
.approval-actions {
    display: flex;
    gap: 12px;
    margin-top: 20px;
    padding-top: 16px;
    border-top: 1px solid #f0f0f0;
}

/* ===== Timeline ===== */
.timeline {
    padding-left: 20px;
}

.timeline-entry {
    display: flex;
    gap: 16px;
    padding-bottom: 20px;
    position: relative;
}

.timeline-entry:last-child {
    padding-bottom: 0;
}

.timeline-entry::before {
    content: "";
    position: absolute;
    left: -14px;
    top: 20px;
    bottom: -4px;
    width: 2px;
    background-color: #dee2e6;
}

.timeline-entry:last-child::before {
    display: none;
}

.timeline-marker {
    width: 12px;
    height: 12px;
    border-radius: 50%;
    background-color: #0d6efd;
    flex-shrink: 0;
    margin-top: 4px;
    position: relative;
    left: -20px;
    margin-right: -8px;
}

.timeline-content {
    flex: 1;
}

.timeline-transition {
    display: flex;
    align-items: center;
    gap: 8px;
    margin-bottom: 4px;
}

.timeline-arrow {
    color: #6c757d;
    font-size: 14px;
}

.timeline-time {
    font-size: 12px;
    color: #6c757d;
}

.timeline-reason {
    font-size: 13px;
    color: #495057;
    margin-top: 4px;
    font-style: italic;
}

/* ===== Buttons ===== */
.btn {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    padding: 10px 20px;
    border: none;
    border-radius: 6px;
    font-size: 14px;
    font-weight: 600;
    cursor: pointer;
    transition: background-color 0.15s, transform 0.1s;
    text-decoration: none;
}

.btn:hover {
    text-decoration: none;
}

.btn:active {
    transform: scale(0.98);
}

.btn:disabled {
    opacity: 0.6;
    cursor: not-allowed;
}

.btn-primary {
    background-color: #0d6efd;
    color: #ffffff;
}

.btn-primary:hover:not(:disabled) {
    background-color: #0b5ed7;
}

.btn-approve {
    background-color: #198754;
    color: #ffffff;
    padding: 12px 32px;
    font-size: 15px;
}

.btn-approve:hover:not(:disabled) {
    background-color: #157347;
}

.btn-reject {
    background-color: #dc3545;
    color: #ffffff;
    padding: 12px 32px;
    font-size: 15px;
}

.btn-reject:hover:not(:disabled) {
    background-color: #bb2d3b;
}

.btn-small {
    padding: 5px 12px;
    font-size: 12px;
    border-radius: 4px;
    background-color: #e9ecef;
    color: #495057;
}

.btn-small:hover {
    background-color: #dee2e6;
    color: #1a1a2e;
}

/* ===== Forms ===== */
.form-card {
    max-width: 560px;
}

.form-group {
    margin-bottom: 20px;
}

.form-group label {
    display: block;
    margin-bottom: 6px;
    font-weight: 600;
    font-size: 13px;
    color: #495057;
}

.form-input {
    width: 100%;
    padding: 10px 14px;
    border: 1px solid #dee2e6;
    border-radius: 6px;
    font-size: 14px;
    font-family: inherit;
    color: #1a1a2e;
    background-color: #ffffff;
    transition: border-color 0.15s, box-shadow 0.15s;
}

.form-input:focus {
    outline: none;
    border-color: #0d6efd;
    box-shadow: 0 0 0 3px rgba(13, 110, 253, 0.15);
}

.form-input::placeholder {
    color: #adb5bd;
}

.form-actions {
    margin-top: 24px;
}

.field-error {
    display: block;
    margin-top: 4px;
    font-size: 12px;
    color: #dc3545;
}

/* ===== Status Messages ===== */
.loading-text {
    color: #6c757d;
    font-style: italic;
    padding: 20px 0;
}

.error-text {
    color: #dc3545;
    font-weight: 500;
    padding: 8px 0;
}

.success-text {
    color: #198754;
    font-weight: 500;
    padding: 8px 0;
}

/* ===== Empty State ===== */
.empty-state {
    text-align: center;
    padding: 48px 24px;
    background-color: #ffffff;
    border-radius: 10px;
    box-shadow: 0 1px 4px rgba(0, 0, 0, 0.06);
    color: #6c757d;
}

.empty-state p {
    font-size: 15px;
}

/* ===== Transaction Specifics ===== */
.provider-ref {
    font-family: "SFMono-Regular", Consolas, "Liberation Mono", Menlo, monospace;
    font-size: 12px;
    color: #6c757d;
    background-color: #f8f9fa;
    padding: 2px 6px;
    border-radius: 3px;
}

.failure-reason {
    font-size: 12px;
    color: #dc3545;
    font-style: italic;
}

/* ===== Not Found ===== */
.not-found {
    text-align: center;
    padding: 64px 24px;
}

.not-found h2 {
    color: #dc3545;
}

/* ===== Responsive ===== */
@media (max-width: 768px) {
    .app-layout {
        flex-direction: column;
    }

    .sidebar {
        width: 100%;
    }

    .nav-menu ul {
        display: flex;
        padding: 8px;
        gap: 4px;
        overflow-x: auto;
    }

    .nav-link {
        padding: 8px 14px;
        white-space: nowrap;
        border-radius: 6px;
    }

    .content-area {
        padding: 20px;
    }

    .card-row {
        flex-direction: column;
    }

    .summary-card {
        max-width: none;
    }

    .detail-grid {
        grid-template-columns: 1fr;
    }

    .approval-actions {
        flex-direction: column;
    }

    .data-table {
        display: block;
        overflow-x: auto;
    }
}
```

---

## Section 8: Verification -- Visual Demo Script

This section provides a step-by-step walkthrough of the complete visual demo.

### Step 1: Start the System

```bash
dotnet run --project appHost/apphost.cs
```

Wait for all resources to reach Running status in the console output. The Aspire dashboard URL will be printed (typically `https://localhost:17225`).

### Step 2: Open the Blazor Web UI

Open the Aspire dashboard in your browser. Find the `web` resource and click its endpoint URL. This opens the AgentPay Watch Blazor application.

### Step 3: Dashboard Shows All Zeros

The Dashboard page loads showing three summary cards:
- **Active Watches: 0** (blue card)
- **Pending Approvals: 0** (orange card)
- **Completed Purchases: 0** (green card)

No recent watches or transactions are shown.

### Step 4: Create a Watch

1. Click **Create Watch** in the left sidebar navigation.
2. Fill in the form:
   - **Product Name:** iPhone 15 Pro
   - **Max Price:** 999.00
   - **Currency:** USD (default)
   - **Preferred Sellers:** leave empty
3. Click **Create Watch**.
4. You are automatically redirected to the Watches list page.

### Step 5: See New Watch with Active Badge

The Watches table shows the new watch:

| Product Name | Max Price | Status | Created | Actions |
|---|---|---|---|---|
| iPhone 15 Pro | $999.00 USD | **Active** (blue badge) | (current time) | View |

### Step 6: Wait for Match (~15-30 seconds)

Stay on the Watches page. The page auto-refreshes every 5 seconds. Within 15-30 seconds, observe the status badge change:

1. **Active** (blue) -- initial state
2. **Matched** (amber) -- ProductWatchWorker found a match
3. **AwaitingApproval** (orange) -- ApprovalWorker created approval token and sent mock notification

### Step 7: View Watch Detail and Approve

1. Click **View** next to the watch.
2. The Watch Detail page shows:
   - Watch info card with product name, max price, currency, status
   - **Match Found -- Action Required** card (orange left border) showing:
     - Product: iPhone 15 Pro (or similar match)
     - Price: $849.99 USD (or similar below max price)
     - Seller: TechDeals Direct (or similar)
     - Availability: InStock
   - Two prominent buttons: **Approve Purchase** (green) and **Reject** (red)
3. Click **Approve Purchase**.
4. A success message appears: "Purchase approved. Payment is being processed..."

### Step 8: Watch Status Transitions Rapidly

The Watch Detail page auto-refreshes every 3 seconds. Observe the status changes in rapid succession:

1. **Approved** (purple) -- approval recorded
2. **Purchasing** (purple) -- payment agent processing
3. **Completed** (green) -- payment succeeded

The Status Timeline section at the bottom shows every transition with timestamps and reasons:
- Active -> Matched: "Product match found..."
- Matched -> AwaitingApproval: "Approval request sent..."
- AwaitingApproval -> Approved: "User approved purchase"
- Approved -> Purchasing: "Payment initiated"
- Purchasing -> Completed: "Payment succeeded, ref: PAY-..."

### Step 9: View Transactions

1. Click **Transactions** in the left sidebar.
2. The Transactions table shows the completed payment:

| Date | Product | Amount | Merchant | Status | Provider Ref |
|---|---|---|---|---|---|
| (current time) | (watch ID prefix) | $849.99 USD | TechDeals Direct | **Succeeded** (green badge) | PAY-abc123def456... |

### Step 10: Verify in Aspire Dashboard

Open the Aspire dashboard and navigate to **Traces**. You should see the complete distributed trace spanning all services:

1. **api** -- POST /api/watches
2. **product-watch-agent** -- ProductWatchWorker scan, match creation, event publish
3. **approval-agent** -- ApprovalWorker message processing, approval record creation
4. **api** -- POST /api/a2p/callback
5. **payment-agent** -- PaymentWorker message processing, payment execution, transaction creation

**Total demo time from watch creation to completed payment: approximately 60-90 seconds.**

### Additional Demo Scenarios

**Rejection flow:**
1. Create another watch (e.g., "MacBook Pro" at $1999).
2. Wait for it to reach AwaitingApproval.
3. Click into the watch detail and click **Reject**.
4. Observe the watch return to **Active** status.
5. The ProductWatchWorker will find a new match on the next scan cycle (15 seconds).

**Payment failure flow:**
1. Create several watches (5-10) to increase the chance of hitting the 10% failure rate.
2. Approve them all as they reach AwaitingApproval.
3. Check the Transactions page -- most will show **Succeeded** (green), but some will show **Failed** (red) with a failure reason like "Insufficient funds" or "Card declined".
4. For failed payments, the associated watch returns to **Active** and the agent will find new matches automatically.

---

## Troubleshooting

| Symptom | Fix |
|---------|-----|
| Blazor pages show "Loading..." indefinitely | Check that the `api` service is running in the Aspire dashboard. The HttpClient uses service discovery (`https+http://api`) which requires Aspire to be orchestrating both services. |
| "Failed to load dashboard" error | The API may not be ready yet. Wait for Cosmos and Service Bus emulators to fully initialize (can take 30-60 seconds on first run). |
| Approve/Reject buttons do nothing | Check browser developer console for errors. Ensure the approval token has not expired (15-minute TTL). |
| Status badges not updating | Verify the page is using `@rendermode InteractiveServer`. Without this directive, the Timer-based auto-refresh will not work. |
| CSS not loading | Ensure `app.UseStaticFiles()` is called in `Program.cs` before `MapRazorComponents`. Verify the file exists at `wwwroot/css/app.css`. |
| Navigation links not highlighting | The `NavLink` component requires `ActiveClass="nav-link-active"` and the CSS class must be defined in `app.css`. |
| HttpClient timeout errors | Service discovery may take a moment after startup. The standard resilience handler (from ServiceDefaults) will retry automatically. |
