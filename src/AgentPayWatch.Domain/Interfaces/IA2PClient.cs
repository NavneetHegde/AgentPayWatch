namespace AgentPayWatch.Domain.Interfaces;

public interface IA2PClient
{
    /// <summary>
    /// Sends an approval request message to the user via A2P (Application-to-Person) messaging.
    /// Returns true if the message was accepted for delivery, false otherwise.
    /// </summary>
    Task<bool> SendApprovalRequestAsync(
        string phoneNumber,
        string productName,
        decimal price,
        string seller,
        string approvalToken,
        CancellationToken ct);
}
