using System.Text.Json;
using Base.OrderGenerator.Application.Exposures.Interfaces;
using Base.OrderGenerator.Commons.Responses;

namespace Base.OrderGenerator.Application.Exposures.UseCases;

public sealed class GetExposuresUseCase(ISymbolExposuresPort symbolExposuresPort)
{
    public Task<DataMessage<JsonElement>> GetExposuresAsync(CancellationToken cancellationToken) =>
        symbolExposuresPort.GetSymbolExposuresAsync(cancellationToken);
}
