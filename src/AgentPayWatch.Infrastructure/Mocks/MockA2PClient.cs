using AgentPayWatch.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace AgentPayWatch.Infrastructure.Mocks;

public sealed class MockA2PClient : IA2PClient
{
    private readonly ILogger<MockA2PClient> _logger;

    public MockA2PClient(ILogger<MockA2PClient> logger)
    {
        _logger = logger;
    }

    public Task<bool> SendApprovalRequestAsync(
        string phoneNumber,
        string productName,
        decimal price,
        string seller,
        string approvalToken,
        CancellationToken ct)
    {
        _logger.LogInformation(
            "A2P Message to {PhoneNumber}: '{ProductName}' found at ${Price} from {Seller}. " +
            "Reply BUY to approve. Token: {Token}",
            phoneNumber,
            productName,
            price,
            seller,
            approvalToken);

        return Task.FromResult(true);
    }
}
