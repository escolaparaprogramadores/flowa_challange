using System.Text.Json;
using Flowa.OrderGenerator.Application.ErrorHandling;
using Flowa.OrderGenerator.Application.Exposures.Interfaces;
using Flowa.Commons.Logging;
using Flowa.Commons.Observability;
using Flowa.Commons.Responses;

namespace Flowa.OrderGenerator.Application.Exposures.UseCases;

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
