using System.Reflection;
using Flowa.OrderAccumulator.Application.Orders.UseCases;
using Flowa.Commons.Database;
using Flowa.Commons.DependencyInjection;
using Flowa.Commons.Logging;
using Flowa.OrderAccumulator.Domain.DomainServices;
using Flowa.OrderAccumulator.Entrypoint.BackgroundService;
using Flowa.OrderAccumulator.Entrypoint.Fix;
using Flowa.OrderAccumulator.Entrypoint.Observability;
using Flowa.OrderAccumulator.Infrastructure.DependencyInjection;
using Flowa.OrderAccumulator.Infrastructure.Fix;
using Flowa.OrderAccumulator.Infrastructure.Orders.Options;

var orderAccumulatorBuilder = Host.CreateApplicationBuilder(args);
Program.AddOrderAccumulatorServices(orderAccumulatorBuilder);

using var orderAccumulatorHost = orderAccumulatorBuilder.Build();
await Program.StartOrderAccumulatorAsync(orderAccumulatorHost);
await orderAccumulatorHost.WaitForShutdownAsync();

public partial class Program
{
    public static void AddOrderAccumulatorServices(HostApplicationBuilder orderAccumulatorBuilder)
    {
        orderAccumulatorBuilder.Logging.AddJsonLogsWithTraceId();
        orderAccumulatorBuilder.Services.AddApplicationLogger();
        orderAccumulatorBuilder.Services.AddOperationMonitoring(
            OrderAccumulatorUseCaseDurationMetric.MeterName, OrderAccumulatorUseCaseDurationMetric.MetricName);

        var flowaConnectionString = orderAccumulatorBuilder.Configuration.GetConnectionString(OrderAccumulatorConfigurationKeys.OrderDatabaseConnectionStringName)
            ?? throw new InvalidOperationException("Set ConnectionStrings__Flowa to the PostgreSQL connection.");
        orderAccumulatorBuilder.Services.AddOrderAccumulatorInfrastructure(flowaConnectionString, orderAccumulatorBuilder.Configuration);
        orderAccumulatorBuilder.Services.AddScoped<OrderDecisionDomainService>();
        orderAccumulatorBuilder.Services.AddScoped<DecideIncomingOrderUseCase>();

        orderAccumulatorBuilder.Services.AddSingleton<FixSessionLogFactory>();
        orderAccumulatorBuilder.Services.AddSingleton<NewOrderSingleConsumer>();
        orderAccumulatorBuilder.Services.AddHostedService<FixAcceptorBackgroundService>();
    }

    public static async Task StartOrderAccumulatorAsync(IHost orderAccumulatorHost)
    {
        var buildCommitSha = ReadBuildCommitSha() is { Length: 40 } shaFromBuild
            ? shaFromBuild
            : throw new InvalidOperationException(
                "The build did not record the commit. Build inside the git repository or pass -p:SourceRevisionId=<full sha>.");

        await orderAccumulatorHost.Services.GetRequiredService<IDatabaseConnectionSource>().ApplyOrderAccumulatorSchemaAsync();

        var orderAccumulatorEnvironmentName = orderAccumulatorHost.Services.GetRequiredService<IHostEnvironment>().EnvironmentName;
        var orderAccumulatorStartLogger = orderAccumulatorHost.Services.GetRequiredService<IApplicationLogger<Program>>();
        orderAccumulatorHost.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStarted.Register(() =>
            orderAccumulatorStartLogger.LogInformation(
                "Application started.", new { Environment = orderAccumulatorEnvironmentName, BuildCommitSha = buildCommitSha }));

        await orderAccumulatorHost.StartAsync();
    }

    private static string? ReadBuildCommitSha()
    {
        var informationalVersion = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var commitSeparatorIndex = informationalVersion?.IndexOf('+') ?? -1;
        return commitSeparatorIndex >= 0 ? informationalVersion![(commitSeparatorIndex + 1)..] : null;
    }
}
