using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Azure.Cosmos;
using Xunit;

namespace AgentPayWatch.Infrastructure.Tests;

/// <summary>
/// Shared xUnit fixture that connects to the local Cosmos DB emulator.
/// Tests using this fixture are skipped if the emulator is not reachable.
///
/// Connection string resolution order:
///   1. Auto-detected from Docker (host port mapped to container port 8081) — preferred because
///      Aspire's env var contains a Docker-internal IP (172.x.x.x) unreachable from the host.
///   2. ConnectionStrings__cosmos environment variable
///   3. COSMOS_CONNECTION_STRING environment variable
///   4. Standalone emulator default: https://localhost:8081
/// </summary>
public sealed class CosmosFixture : IAsyncLifetime
{
    private const string EmulatorKey =
        "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";

    private const string DatabaseId = "agentpaywatch";

    public CosmosClient Client { get; private set; } = null!;

    // One property per container, matching the schema in CosmosDbInitializer.
    public Container Container { get; private set; } = null!;           // watches  (pk: /userId)
    public Container MatchesContainer { get; private set; } = null!;    // matches  (pk: /watchRequestId)
    public Container ApprovalsContainer { get; private set; } = null!;  // approvals (pk: /watchRequestId)
    public Container TransactionsContainer { get; private set; } = null!; // transactions (pk: /userId)

    public bool IsAvailable { get; private set; }
    public string UnavailableReason { get; private set; } = string.Empty;

    /// <summary>
    /// Aspire injects extra non-standard properties (DisableServerCertificateValidation=True,
    /// Database=agentpaywatch) that the Cosmos SDK doesn't recognise. Strip them.
    /// </summary>
    private static string SanitizeConnectionString(string cs) =>
        string.Join(";", cs.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p.StartsWith("AccountEndpoint=", StringComparison.OrdinalIgnoreCase) ||
                        p.StartsWith("AccountKey=", StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Runs <c>docker ps</c> to find the host port mapped to the Cosmos emulator's
    /// internal port 8081. Works with Docker Desktop (0.0.0.0) and Linux Docker (127.0.0.1).
    /// Returns null if Docker is unavailable or no matching container is found.
    /// </summary>
    private static string? TryDetectDockerPort()
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo("docker", "ps --format {{.Ports}}")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(3_000);

            var m = Regex.Match(output, @"(?:127\.0\.0\.1|0\.0\.0\.0):(\d+)->8081");
            return m.Success ? m.Groups[1].Value : null;
        }
        catch
        {
            return null;
        }
    }

    private static string ResolveConnectionString()
    {
        // Docker detection runs FIRST. Aspire's env var contains the Docker-internal IP
        // (e.g. https://172.19.0.2:8081) which is unreachable from the host. The host-mapped
        // port from `docker ps` is always reachable, so prefer it when the emulator is running.
        var port = TryDetectDockerPort();
        if (port is not null)
        {
            Console.WriteLine($"[CosmosFixture] Auto-detected Cosmos emulator on host port {port}");
            // The Linux preview emulator (RunAsPreviewEmulator) serves plain HTTP on port 8081,
            // not HTTPS. Using https:// causes "corrupted frame" TLS errors.
            return $"AccountEndpoint=http://localhost:{port}/;AccountKey={EmulatorKey}";
        }

        // No Docker container found — fall back to env var (real Azure Cosmos or standalone emulator).
        var raw =
            Environment.GetEnvironmentVariable("ConnectionStrings__cosmos") ??
            Environment.GetEnvironmentVariable("COSMOS_CONNECTION_STRING");

        if (raw is not null)
        {
            Console.WriteLine("[CosmosFixture] Using connection string from environment variable");
            return SanitizeConnectionString(raw);
        }

        Console.WriteLine("[CosmosFixture] No Docker container or env var found; falling back to localhost:8081");
        return $"AccountEndpoint=https://localhost:8081/;AccountKey={EmulatorKey}";
    }

    public async Task InitializeAsync()
    {
        var connectionString = ResolveConnectionString();
        Console.WriteLine($"[CosmosFixture] Connecting to: {Regex.Replace(connectionString, "AccountKey=[^;]+", "AccountKey=***")}");

        // The Linux preview emulator advertises its internal address (127.0.0.1:8081) in responses,
        // but from the host it's reachable only via the Docker-mapped port. Rewrite all SDK
        // requests that target the internal address to use our configured endpoint instead.
        var endpointUri = new Uri(connectionString
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .First(p => p.StartsWith("AccountEndpoint=", StringComparison.OrdinalIgnoreCase))
            ["AccountEndpoint=".Length..]);
        var configuredAuthority = $"{endpointUri.Host}:{endpointUri.Port}"; // e.g. localhost:53156

        try
        {
            Client = new CosmosClient(connectionString, new CosmosClientOptions
            {
                ConnectionMode = ConnectionMode.Gateway,
                HttpClientFactory = () => new HttpClient(
                    new EmulatorRedirectHandler("127.0.0.1:8081", configuredAuthority, new SocketsHttpHandler())),
                RequestTimeout = TimeSpan.FromSeconds(8),
                // Disable rate-limit retries — they add minutes of dead time in test runs.
                MaxRetryAttemptsOnRateLimitedRequests = 0,
                MaxRetryWaitTimeOnRateLimitedRequests = TimeSpan.Zero,
            });

            // Hard deadline: RequestTimeout only caps a single HTTP round-trip; the SDK's
            // AbstractRetryHandler loops far beyond it without an outer CancellationToken.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var db = await Client.CreateDatabaseIfNotExistsAsync(
                DatabaseId, cancellationToken: cts.Token);

            await db.Database.CreateContainerIfNotExistsAsync(
                new ContainerProperties("watches", "/userId"), cancellationToken: cts.Token);
            await db.Database.CreateContainerIfNotExistsAsync(
                new ContainerProperties("matches", "/watchRequestId"), cancellationToken: cts.Token);
            await db.Database.CreateContainerIfNotExistsAsync(
                new ContainerProperties("approvals", "/watchRequestId"), cancellationToken: cts.Token);
            await db.Database.CreateContainerIfNotExistsAsync(
                new ContainerProperties("transactions", "/userId"), cancellationToken: cts.Token);

            Container = Client.GetContainer(DatabaseId, "watches");
            MatchesContainer = Client.GetContainer(DatabaseId, "matches");
            ApprovalsContainer = Client.GetContainer(DatabaseId, "approvals");
            TransactionsContainer = Client.GetContainer(DatabaseId, "transactions");
            IsAvailable = true;
            Console.WriteLine("[CosmosFixture] Connected successfully.");
        }
        catch (Exception ex)
        {
            IsAvailable = false;
            UnavailableReason = BuildExceptionChain(ex);
            Console.Error.WriteLine($"[CosmosFixture] Init failed — {UnavailableReason}");
        }
    }

    public async Task DisposeAsync()
    {
        if (IsAvailable)
            await DeleteAllDocumentsAsync();
        Client?.Dispose();
    }

    private static string BuildExceptionChain(Exception ex)
    {
        var parts = new List<string>();
        for (var e = ex; e is not null; e = e.InnerException)
            parts.Add($"{e.GetType().Name}: {e.Message}");
        return string.Join(" → ", parts);
    }

    /// <summary>
    /// Rewrites request URIs that contain <paramref name="fromAuthority"/> to use
    /// <paramref name="toAuthority"/> instead. Required because the Linux Cosmos preview
    /// emulator advertises its internal container address (127.0.0.1:8081) in responses,
    /// but the host can only reach it via the Docker-mapped port.
    /// </summary>
    private sealed class EmulatorRedirectHandler(string fromAuthority, string toAuthority, HttpMessageHandler inner)
        : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri is not null)
            {
                var uri = request.RequestUri.ToString();
                if (uri.Contains(fromAuthority))
                    request.RequestUri = new Uri(uri.Replace(fromAuthority, toAuthority));
            }
            return base.SendAsync(request, ct);
        }
    }

    /// <summary>Deletes all items in the watches container (for between-test cleanup).</summary>
    public Task DeleteAllDocumentsAsync() =>
        DeleteAllAsync(Container, "SELECT c.id, c.userId FROM c", item => (string)item.userId);

    /// <summary>Deletes all items in the matches container.</summary>
    public Task DeleteAllMatchDocumentsAsync() =>
        DeleteAllAsync(MatchesContainer, "SELECT c.id, c.watchRequestId FROM c", item => (string)item.watchRequestId);

    /// <summary>Deletes all items in the approvals container.</summary>
    public Task DeleteAllApprovalDocumentsAsync() =>
        DeleteAllAsync(ApprovalsContainer, "SELECT c.id, c.watchRequestId FROM c", item => (string)item.watchRequestId);

    /// <summary>Deletes all items in the transactions container.</summary>
    public Task DeleteAllTransactionDocumentsAsync() =>
        DeleteAllAsync(TransactionsContainer, "SELECT c.id, c.userId FROM c", item => (string)item.userId);

    private static async Task DeleteAllAsync(
        Container container,
        string selectSql,
        Func<dynamic, string> partitionKeySelector)
    {
        var query = new QueryDefinition(selectSql);
        var iterator = container.GetItemQueryIterator<dynamic>(query,
            requestOptions: new QueryRequestOptions { MaxItemCount = 100 });

        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync();
            foreach (var item in page)
            {
                string id = item.id;
                string pk = partitionKeySelector(item);
                await container.DeleteItemAsync<dynamic>(id, new PartitionKey(pk));
            }
        }
    }
}
