namespace AgentPayWatch.Domain.Interfaces;

public interface IEventPublisher
{
    Task PublishAsync<T>(T message, string topicName, CancellationToken ct);
}
