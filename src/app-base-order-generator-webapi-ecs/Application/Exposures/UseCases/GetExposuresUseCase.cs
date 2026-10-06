using System.Text.Json;
using Base.OrderGenerator.Application.ErrorHandling;
using Base.OrderGenerator.Application.Exposures.Interfaces;
using Base.OrderGenerator.Commons.Logging;
using Base.OrderGenerator.Commons.Observability;
using Base.OrderGenerator.Commons.Responses;

namespace Base.OrderGenerator.Application.Exposures.UseCases;

public sealed class GetExposuresUseCase
{
    public const string OperationName = "exposures.get-exposures";

    private readonly ISymbolExposuresPort _symbolExposuresPort;
    private readonly IOperationMonitoring _operationMonitoring;
    private readonly IApplicationLogger<GetExposuresUseCase> _logger;

    public GetExposuresUseCase(ISymbolExposuresPort symbolExposuresPort, IOperationMonitoring operationMonitoring, IApplicationLogger<GetExposuresUseCase> logger)
    {
        _symbolExposuresPort = symbolExposuresPort ?? throw new ArgumentNullException(nameof(symbolExposuresPort));
        _operationMonitoring = operationMonitoring ?? throw new ArgumentNullException(nameof(operationMonitoring));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<DataMessage<JsonElement>> GetExposuresAsync(CancellationToken cancellationToken)
    {
        using var exposuresReading = _operationMonitoring.StartOperationMonitoring(OperationName);
        try
        {
            var symbolExposures = await _symbolExposuresPort.GetSymbolExposuresAsync(cancellationToken);

            exposuresReading.RecordOperationResult(OperationResults.Succeeded);
            _logger.LogInformation("Symbol exposures read.");
            return symbolExposures;
        }
        catch (Exception exposuresReadingFailure)
        {
            if (UseCaseFailureDataMessageMapper.WasCancelledByTheCaller(exposuresReadingFailure, cancellationToken))
            {
                exposuresReading.RecordOperationResult(OperationResults.CancelledByTheCaller);
                throw;
            }

            exposuresReading.RecordOperationResult(OperationResults.Failed);
            return UseCaseFailureDataMessageMapper.MapFailureToDataMessage<JsonElement>(exposuresReadingFailure);
        }
    }
}
