using Base.OrderAccumulator.Application.Exposures.GetExposures;
using Base.OrderAccumulator.Application.Exposures;
using Base.OrderAccumulator.Commons;
using StatsdClient;

namespace Base.OrderAccumulator.Infrastructure.Metrics;

public static class OrderMetricsExtensions
{
    // O agente do Datadog roda como sidecar na mesma task (contrato da F5).
    public const string DatadogAgentHost = "localhost";
    public const int DatadogAgentDogStatsdPort = 8125;

    public static IServiceCollection AddOrderMetrics(this IServiceCollection orderAccumulatorServices, IConfiguration orderAccumulatorConfiguration)
    {
        orderAccumulatorServices.AddSingleton<IDogStatsd>(_ =>
            CreateOrderMetricsClient(DatadogAgentDogStatsdPort, orderAccumulatorConfiguration));
        orderAccumulatorServices.AddSingleton<IOrderMetrics, DatadogOrderMetricsAdapter>();
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
                Environment = orderAccumulatorConfiguration[OrderAccumulatorConfigurationKeys.DatadogEnvironment],
                ServiceName = orderAccumulatorConfiguration[OrderAccumulatorConfigurationKeys.DatadogService],
                ServiceVersion = orderAccumulatorConfiguration[OrderAccumulatorConfigurationKeys.DatadogVersion]
            },
            IgnoreDogStatsdSendFailure);
        return orderMetricsClient;
    }

    // Uma única leitura na subida, antes do acceptor FIX abrir; depois disso o gauge só lê a memória.
    public static async Task LoadSymbolExposureMemoryAsync(this IServiceProvider orderAccumulatorServiceProvider, CancellationToken cancellationToken = default)
    {
        var storedSymbolExposures = await orderAccumulatorServiceProvider.GetRequiredService<ISymbolExposureReadRepository>().GetSymbolExposuresAsync(cancellationToken);
        orderAccumulatorServiceProvider.GetRequiredService<SymbolExposureMemoryService>().LoadStoredExposures(storedSymbolExposures);
    }

    // Sem agente escutando (local, testes, agente desligado) o UDP falha a cada envio. Métrica é
    // acessória: a ordem segue e o log não ganha uma linha por ordem.
    private static void IgnoreDogStatsdSendFailure(Exception dogStatsdSendFailure) { }
}
