using Flowa.Commons.DependencyInjection;
using Flowa.Commons.Observability;
using Flowa.DatadogMetrics.Application.Exposures.Interfaces;
using Flowa.DatadogMetrics.Application.Orders.Interfaces;
using Flowa.DatadogMetrics.Infrastructure.Exposures.Adapters;
using Flowa.DatadogMetrics.Infrastructure.Exposures.Repositories;
using Flowa.DatadogMetrics.Infrastructure.Orders.Adapters;
using Flowa.DatadogMetrics.Infrastructure.Orders.Repositories;

namespace Flowa.DatadogMetrics.Infrastructure.DependencyInjection;

internal static class DatadogMetricsInfrastructureExtensions
{
    public const string DatadogAgentHost = "localhost";
    public const int DatadogAgentDogStatsdPort = 8125;

    extension(IServiceCollection datadogMetricsServices)
    {
        public IServiceCollection AddDatadogMetricsInfrastructure(string flowaConnectionString, IConfiguration datadogMetricsConfiguration)
        {
            datadogMetricsServices.AddPostgresDatabase(flowaConnectionString, datadogMetricsConfiguration);
            datadogMetricsServices.AddScoped<ISymbolExposureReadRepository, SymbolExposureReadRepository>();
            datadogMetricsServices.AddScoped<IAnsweredOrderCountReadRepository, AnsweredOrderCountReadRepository>();
            datadogMetricsServices.AddSingleton<IMetricsClient>(_ =>
                CreateDatadogMetricsClient(DatadogAgentDogStatsdPort, datadogMetricsConfiguration));
            datadogMetricsServices.AddSingleton<IExposureMetricsPort, DatadogExposureMetricsAdapter>();
            datadogMetricsServices.AddSingleton<IOrderMetricsPort, DatadogOrderMetricsAdapter>();
            return datadogMetricsServices;
        }
    }

    public static DogStatsdMetricsClient CreateDatadogMetricsClient(int dogStatsdPort, IConfiguration datadogMetricsConfiguration) =>
        new(
            DatadogAgentHost,
            dogStatsdPort,
            datadogMetricsConfiguration[DatadogMetricsConfigurationKeys.DatadogEnvironment],
            datadogMetricsConfiguration[DatadogMetricsConfigurationKeys.DatadogService],
            datadogMetricsConfiguration[DatadogMetricsConfigurationKeys.DatadogVersion]);
}
