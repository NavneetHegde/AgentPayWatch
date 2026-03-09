namespace AgentPayWatch.Api.Contracts;

public sealed record ApprovalCallbackRequest(
    string Token,
    string Decision
);
