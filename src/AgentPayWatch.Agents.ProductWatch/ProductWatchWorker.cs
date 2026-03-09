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

        await _matchRepo.CreateAsync(productMatch, ct);

        watch.UpdateStatus(WatchStatus.Matched);
        await _watchRepo.UpdateAsync(watch, ct);

        var matchEvent = new ProductMatchFound(
            MessageId: Guid.NewGuid(),
            CorrelationId: watch.Id,
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
