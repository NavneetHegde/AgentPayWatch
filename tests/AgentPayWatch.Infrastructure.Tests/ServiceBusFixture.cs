using System.Diagnostics;
using System.Text.RegularExpressions;
using Azure.Messaging.ServiceBus;
using Xunit;

namespace AgentPayWatch.Infrastructure.Tests;

/// <summary>
/// Shared xUnit fixture that connects to the local Service Bus emulator.
/// Tests using this fixture are skipped if the emulator is not reachable.
///
/// Connection string resolution order:
///   1. Auto-detected from Docker (host port mapped to container port 5672)
///   2. ConnectionStrings__messaging environment variable (set by Aspire)
///   3. SERVICEBUS_CONNECTIONSTRING environment variable
/// </summary>
public sealed class ServiceBusFixture : IAsyncLifetime
{
    // The official Azure Service Bus Emulator uses these well-known credentials.
    private const string EmulatorKey = "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KKam+LaAm3pw==";
    private const string EmulatorKeyName = "RootManageSharedAccessKey";

    public ServiceBusClient? Client { get; private set; }
    public bool IsAvailable { get; private set; }
    public string UnavailableReason { get; private set; } = string.Empty;

    /// <summary>
    /// Runs docker ps to find the host port mapped to the Service Bus emulator's AMQP port 5672.
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

            var m = Regex.Match(output, @"(?:127\.0\.0\.1|0\.0\.0\.0):(\d+)->5672");
            return m.Success ? m.Groups[1].Value : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveConnectionString()
    {
        // 1. Auto-detect from Docker
        var port = TryDetectDockerPort();
        if (port is not null)
        {
            Console.WriteLine($"[ServiceBusFixture] Auto-detected Service Bus emulator on host port {port}");
            return $"Endpoint=sb://localhost;SharedAccessKeyName={EmulatorKeyName};SharedAccessKey={EmulatorKey};UseDevelopmentEmulator=true;";
        }

        // 2. Env var injected by Aspire
        var raw =
            Environment.GetEnvironmentVariable("ConnectionStrings__messaging") ??
            Environment.GetEnvironmentVariable("SERVICEBUS_CONNECTIONSTRING");

        if (raw is not null)
        {
            Console.WriteLine("[ServiceBusFixture] Using connection string from environment variable");
            return raw;
        }

        return null;
    }

    public async Task InitializeAsync()
    {
        var connectionString = ResolveConnectionString();

        if (connectionString is null)
        {
            IsAvailable = false;
            UnavailableReason = "No Service Bus emulator detected. Start Aspire first ('aspire run .\\appHost\\apphost.cs').";
            Console.Error.WriteLine($"[ServiceBusFixture] {UnavailableReason}");
            return;
        }

        try
        {
            Client = new ServiceBusClient(connectionString, new ServiceBusClientOptions
            {
                TransportType = ServiceBusTransportType.AmqpTcp,
                RetryOptions = new ServiceBusRetryOptions
                {
                    MaxRetries = 0,
                    TryTimeout = TimeSpan.FromSeconds(10)
                }
            });

            // Probe connectivity with a short-lived sender
            await using var probe = Client.CreateSender("product-match-found");
            // CreateSender is lazy; send a no-op peek to verify the connection
            var receiver = Client.CreateReceiver("product-match-found", "sub-approval-agent",
                new ServiceBusReceiverOptions { ReceiveMode = ServiceBusReceiveMode.PeekLock });
            await receiver.PeekMessageAsync(cancellationToken: new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
            await receiver.DisposeAsync();

            IsAvailable = true;
            Console.WriteLine("[ServiceBusFixture] Connected to Service Bus emulator successfully.");
        }
        catch (Exception ex)
        {
            IsAvailable = false;
            UnavailableReason = BuildExceptionChain(ex);
            Client?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Client = null;
            Console.Error.WriteLine($"[ServiceBusFixture] Init failed — {UnavailableReason}");
        }
    }

    public async Task DisposeAsync()
    {
        if (Client is not null)
            await Client.DisposeAsync();
    }

    private static string BuildExceptionChain(Exception ex)
    {
        var parts = new List<string>();
        for (var e = ex; e is not null; e = e.InnerException)
            parts.Add($"{e.GetType().Name}: {e.Message}");
        return string.Join(" → ", parts);
    }
}

[CollectionDefinition("ServiceBusIntegration")]
public sealed class ServiceBusIntegrationCollection : ICollectionFixture<ServiceBusFixture> { }
