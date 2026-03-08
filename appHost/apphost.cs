#:sdk  Aspire.AppHost.Sdk@13.1.1
#:project ../src/AgentPayWatch.Api
#:project ../src/AgentPayWatch.Agents.ProductWatch
#:project ../src/AgentPayWatch.Agents.Approval
#:project ../src/AgentPayWatch.Agents.Payment
#:project ../src/AgentPayWatch.Web
#:package Aspire.Hosting.Azure.CosmosDB@13.1.1
#:package Aspire.Hosting.Azure.ServiceBus@13.1.1

var builder = DistributedApplication.CreateBuilder(args);

// --- Data ---
var cosmosAccount = builder.AddAzureCosmosDB("cosmos")
    .RunAsEmulator();

cosmosAccount.AddCosmosDatabase("agentpaywatch");

// --- Messaging ---
var messaging = builder.AddAzureServiceBus("messaging")
    .RunAsEmulator()
    .AddTopic(TopicNames.ProductMatchFound, topic =>
    {
        topic.AddSubscription("approval-agent");
    })
    .AddTopic(TopicNames.ApprovalDecided, topic =>
    {
        topic.AddSubscription("payment-agent");
    })
    .AddTopic(TopicNames.PaymentCompleted, topic =>
    {
        topic.AddSubscription("notification-handler");
    })
    .AddTopic(TopicNames.PaymentFailed, topic =>
    {
        topic.AddSubscription("notification-handler");
    });

// --- Services ---
var api = builder.AddProject<Projects.AgentPayWatch_Api>("api")
    .WithReference(cosmosAccount)
    .WaitFor(cosmosAccount)
    .WithReference(messaging)
    .WaitFor(messaging)
    .WithExternalHttpEndpoints();

builder.AddProject<Projects.AgentPayWatch_Agents_ProductWatch>("product-watch-agent")
    .WithReference(cosmosAccount)
    .WaitFor(cosmosAccount)
    .WithReference(messaging)
    .WaitFor(messaging);

builder.AddProject<Projects.AgentPayWatch_Agents_Approval>("approval-agent")
    .WithReference(cosmosAccount)
    .WaitFor(cosmosAccount)
    .WithReference(messaging)
    .WaitFor(messaging);

builder.AddProject<Projects.AgentPayWatch_Agents_Payment>("payment-agent")
    .WithReference(cosmosAccount)
    .WaitFor(cosmosAccount)
    .WithReference(messaging)
    .WaitFor(messaging);

builder.AddProject<Projects.AgentPayWatch_Web>("web")
    .WithReference(api)
    .WithExternalHttpEndpoints();

builder.Build().Run();

// --- Static import for topic name constants ---
public static class TopicNames
{
    public const string ProductMatchFound = "product-match-found";
    public const string ApprovalDecided = "approval-decided";
    public const string PaymentCompleted = "payment-completed";
    public const string PaymentFailed = "payment-failed";
}
