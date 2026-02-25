using AgentPayWatch.Domain.Interfaces;
using AgentPayWatch.Infrastructure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AgentPayWatch.Infrastructure;

public static class DependencyInjection
{
    public static IHostApplicationBuilder AddInfrastructureServices(
        this IHostApplicationBuilder builder)
    {
        builder.AddAzureCosmosClient("cosmos");

        builder.Services.AddScoped<IWatchRequestRepository, CosmosWatchRequestRepository>();
        builder.Services.AddScoped<IProductMatchRepository, CosmosProductMatchRepository>();
        builder.Services.AddScoped<IApprovalRepository, CosmosApprovalRepository>();
        builder.Services.AddScoped<IPaymentTransactionRepository, CosmosPaymentTransactionRepository>();

        builder.Services.AddHostedService<CosmosDbInitializer>();

        return builder;
    }
}
