using Flowa.OrderGenerator.Application.ErrorHandling;
using Flowa.OrderGenerator.Application.Exposures.Interfaces;
using Flowa.OrderGenerator.Application.Exposures.Responses;
using Flowa.Commons.Logging;
using Flowa.Commons.Observability;
using Flowa.Commons.Responses;
using Flowa.OrderGenerator.Domain.Exposures.ValueObjects;

namespace Flowa.OrderGenerator.Application.Exposures.UseCases;

public sealed class GetExposuresUseCase
{
    public const string OperationName = "exposures.get-exposures";
    public const string ExposuresReadMessage = "Exposição dos símbolos lida.";

    private readonly ISymbolExposureRepository _symbolExposureRepository;
    private readonly IOperationMonitoring _operationMonitoring;
    private readonly IApplicationLogger<GetExposuresUseCase> _logger;

    public GetExposuresUseCase(ISymbolExposureRepository symbolExposureRepository, IOperationMonitoring operationMonitoring, IApplicationLogger<GetExposuresUseCase> logger)
    {
        _symbolExposureRepository = symbolExposureRepository ?? throw new ArgumentNullException(nameof(symbolExposureRepository));
        _operationMonitoring = operationMonitoring ?? throw new ArgumentNullException(nameof(operationMonitoring));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<DataMessage<ExposuresResponse>> GetExposuresAsync(CancellationToken cancellationToken)
    {
        using var exposuresReading = _operationMonitoring.StartOperationMonitoring(OperationName);
        try
        {
            var symbolExposures = await _symbolExposureRepository.ExposureTableExistsAsync(cancellationToken)
                ? await _symbolExposureRepository.GetSymbolExposuresAsync(cancellationToken)
                : SymbolExposure.CreateZeroSymbolExposures();

            exposuresReading.RecordOperationResult(OperationResults.Succeeded);
            _logger.LogInformation("Symbol exposures read.");
            return DataMessage<ExposuresResponse>.CreateSuccessMessage(ExposuresResponse.MapFromSymbolExposures(symbolExposures), ExposuresReadMessage);
        }
        catch (Exception exposuresReadingFailure)
        {
            if (UseCaseFailureDataMessageMapper.WasCancelledByTheCaller(exposuresReadingFailure, cancellationToken))
            {
                exposuresReading.RecordOperationResult(OperationResults.CancelledByTheCaller);
                throw;
            }

            exposuresReading.RecordOperationResult(OperationResults.Failed);
            return UseCaseFailureDataMessageMapper.MapFailureToDataMessage<ExposuresResponse>(exposuresReadingFailure);
        }
    }
}
