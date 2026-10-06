using Flowa.Commons.Logging;
using Flowa.Commons.Observability;
using Flowa.Commons.Responses;
using Flowa.DatadogMetrics.Application.ErrorHandling;
using Flowa.DatadogMetrics.Application.Exposures.Interfaces;
using Flowa.DatadogMetrics.Domain.Exposures.ValueObjects;

namespace Flowa.DatadogMetrics.Application.Exposures.UseCases;

public sealed class SendSymbolExposureGaugesUseCase
{
    public const string ExposureGaugesSentMessage = "Exposição dos símbolos enviada ao Datadog.";
    public const string OperationName = "exposures.send-exposure-gauges";

    private readonly ISymbolExposureReadRepository symbolExposureReadRepository;
    private readonly IExposureMetricsPort exposureMetrics;
    private readonly IOperationMonitoring operationMonitoring;
    private readonly IApplicationLogger<SendSymbolExposureGaugesUseCase> exposureGaugesLogger;

    public SendSymbolExposureGaugesUseCase(
        ISymbolExposureReadRepository symbolExposureReadRepository,
        IExposureMetricsPort exposureMetrics,
        IOperationMonitoring operationMonitoring,
        IApplicationLogger<SendSymbolExposureGaugesUseCase> exposureGaugesLogger)
    {
        this.symbolExposureReadRepository = symbolExposureReadRepository ?? throw new ArgumentNullException(nameof(symbolExposureReadRepository));
        this.exposureMetrics = exposureMetrics ?? throw new ArgumentNullException(nameof(exposureMetrics));
        this.operationMonitoring = operationMonitoring ?? throw new ArgumentNullException(nameof(operationMonitoring));
        this.exposureGaugesLogger = exposureGaugesLogger ?? throw new ArgumentNullException(nameof(exposureGaugesLogger));
    }

    public async Task<DataMessage<IReadOnlyList<SymbolExposure>>> SendSymbolExposureGaugesAsync(CancellationToken cancellationToken = default)
    {
        using var exposureGaugesMonitoring = operationMonitoring.StartOperationMonitoring(OperationName);
        try
        {
            var symbolExposures = await symbolExposureReadRepository.GetSymbolExposuresAsync(cancellationToken);
            foreach (var symbolExposure in symbolExposures)
                exposureMetrics.SendSymbolExposureGauge(symbolExposure);

            exposureGaugesLogger.LogInformation("Symbol exposure gauges sent.", new
            {
                SentExposures = string.Join(' ', symbolExposures.Select(symbolExposure =>
                    FormattableString.Invariant($"{symbolExposure.Symbol}={symbolExposure.Exposure}")))
            });
            return DataMessage<IReadOnlyList<SymbolExposure>>.CreateSuccessMessage(symbolExposures, ExposureGaugesSentMessage);
        }
        catch (Exception exposureGaugesFailure)
        {
            exposureGaugesMonitoring.RecordOperationResult(OperationResults.Failed);
            return UseCaseFailureDataMessageMapper.MapUseCaseFailureToDataMessage<IReadOnlyList<SymbolExposure>>(exposureGaugesFailure);
        }
    }
}
