using Microsoft.Extensions.DependencyInjection.Extensions;
using OrderAccumulator.Exposure;
using OrderAccumulator.Persistence;
using StatsdClient;

namespace OrderAccumulator.Observabilidade;

public static class OrderMetricsSetup
{
    // O agente do Datadog roda como sidecar na mesma task (contrato da F5).
    public const string DatadogAgentHost = "localhost";
    public const int DatadogAgentDogStatsdPort = 8125;

    public static IServiceCollection AddOrderMetrics(this IServiceCollection orderAccumulatorServices, IConfiguration orderAccumulatorConfiguration)
    {
        orderAccumulatorServices.AddSingleton<IDogStatsd>(_ =>
            CreateOrderMetricsClient(DatadogAgentDogStatsdPort, orderAccumulatorConfiguration));
        orderAccumulatorServices.AddSingleton<SymbolExposureMemory>();
        orderAccumulatorServices.AddSingleton<PostgresOrderProcessor>();
        orderAccumulatorServices.Replace(ServiceDescriptor.Singleton<IOrderProcessor>(orderAccumulatorServiceProvider =>
            new OrderProcessorWithMetrics(
                orderAccumulatorServiceProvider.GetRequiredService<PostgresOrderProcessor>(),
                orderAccumulatorServiceProvider.GetRequiredService<IDogStatsd>(),
                orderAccumulatorServiceProvider.GetRequiredService<SymbolExposureMemory>())));
        orderAccumulatorServices.AddHostedService<SymbolExposureGaugeService>();
        return orderAccumulatorServices;
    }

    // env, service e version vêm das variáveis DD_* que a task define; fora da AWS ficam sem valor.
    public static DogStatsdService CreateOrderMetricsClient(int dogStatsdPort, IConfiguration orderAccumulatorConfiguration)
    {
        var orderMetricsClient = new DogStatsdService();
        orderMetricsClient.Configure(
            new StatsdConfig
            {
                StatsdServerName = DatadogAgentHost,
                StatsdPort = dogStatsdPort,
                Environment = orderAccumulatorConfiguration["DD_ENV"],
                ServiceName = orderAccumulatorConfiguration["DD_SERVICE"],
                ServiceVersion = orderAccumulatorConfiguration["DD_VERSION"]
            },
            IgnoreDogStatsdSendFailure);
        return orderMetricsClient;
    }

    // Uma única leitura na subida, antes do acceptor FIX abrir; depois disso o gauge só lê a memória.
    public static async Task LoadSymbolExposureMemoryAsync(this IServiceProvider orderAccumulatorServiceProvider, CancellationToken cancellationToken = default)
    {
        var storedSymbolExposures = await orderAccumulatorServiceProvider.GetRequiredService<IExposureReader>().GetSymbolExposuresAsync(cancellationToken);
        orderAccumulatorServiceProvider.GetRequiredService<SymbolExposureMemory>().LoadStoredExposures(storedSymbolExposures);
    }

    // Sem agente escutando (local, testes, agente desligado) o UDP falha a cada envio. Métrica é
    // acessória: a ordem segue e o log não ganha uma linha por ordem.
    private static void IgnoreDogStatsdSendFailure(Exception dogStatsdSendFailure) { }
}
