using Base.OrderAccumulator.Application.ErrorHandling;
using Base.OrderAccumulator.Application.Exposures.Interfaces;
using Base.OrderAccumulator.Commons.Logging;
using Base.OrderAccumulator.Commons.Observability;
using Base.OrderAccumulator.Commons.Responses;
using Base.OrderAccumulator.Domain.Exposures.ValueObjects;

namespace Base.OrderAccumulator.Application.Exposures.UseCases;

public sealed class GetExposuresUseCase
{
    public const string ExposuresReadMessage = "Exposição dos símbolos lida.";
    public const string OperationName = "exposures.get-exposures";

    private readonly ISymbolExposureReadRepository symbolExposureReadRepository;
    private readonly IOperationMonitoring operationMonitoring;
    private readonly IApplicationLogger<GetExposuresUseCase> exposuresLogger;

    public GetExposuresUseCase(
        ISymbolExposureReadRepository symbolExposureReadRepository, IOperationMonitoring operationMonitoring, IApplicationLogger<GetExposuresUseCase> exposuresLogger)
    {
        this.symbolExposureReadRepository = symbolExposureReadRepository ?? throw new ArgumentNullException(nameof(symbolExposureReadRepository));
        this.operationMonitoring = operationMonitoring ?? throw new ArgumentNullException(nameof(operationMonitoring));
        this.exposuresLogger = exposuresLogger ?? throw new ArgumentNullException(nameof(exposuresLogger));
    }

    public async Task<DataMessage<IReadOnlyList<SymbolExposure>>> GetExposuresAsync(CancellationToken cancellationToken = default)
    {
        using var exposuresReadMonitoring = operationMonitoring.StartOperationMonitoring(OperationName);
        try
        {
            var symbolExposures = await symbolExposureReadRepository.GetSymbolExposuresAsync(cancellationToken);
            exposuresLogger.LogInformation("Symbol exposures read.", new { SymbolCount = symbolExposures.Count });
            return DataMessage<IReadOnlyList<SymbolExposure>>.CreateSuccessMessage(symbolExposures, ExposuresReadMessage);
        }
        catch (Exception exposuresReadFailure)
        {
            exposuresReadMonitoring.RecordOperationResult(OperationResults.Failed);
            return UseCaseFailureDataMessageMapper.MapUseCaseFailureToDataMessage<IReadOnlyList<SymbolExposure>>(exposuresReadFailure);
        }
    }
}
