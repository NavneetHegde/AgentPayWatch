#:sdk  Aspire.AppHost.Sdk@13.1.1
#:project ../src/AgentPayWatch.Api
#:project ../src/AgentPayWatch.Agents.ProductWatch
#:project ../src/AgentPayWatch.Agents.Approval
#:project ../src/AgentPayWatch.Agents.Payment
#:project ../src/AgentPayWatch.Web
#:package Aspire.Hosting.Azure.CosmosDB@13.1.1

var builder = DistributedApplication.CreateBuilder(args);

var cosmosAccount = builder.AddAzureCosmosDB("cosmos")
    .RunAsEmulator();

cosmosAccount.AddCosmosDatabase("agentpaywatch");

var api = builder.AddProject<Projects.AgentPayWatch_Api>("api")
    .WithReference(cosmosAccount)
    .WaitFor(cosmosAccount)
    .WithExternalHttpEndpoints();

builder.AddProject<Projects.AgentPayWatch_Agents_ProductWatch>("product-watch-agent")
    .WithReference(cosmosAccount)
    .WaitFor(cosmosAccount);

builder.AddProject<Projects.AgentPayWatch_Agents_Approval>("approval-agent")
    .WithReference(cosmosAccount)
    .WaitFor(cosmosAccount);

builder.AddProject<Projects.AgentPayWatch_Agents_Payment>("payment-agent")
    .WithReference(cosmosAccount)
    .WaitFor(cosmosAccount);

builder.AddProject<Projects.AgentPayWatch_Web>("web")
    .WithReference(api)
    .WithExternalHttpEndpoints();

builder.Build().Run();
