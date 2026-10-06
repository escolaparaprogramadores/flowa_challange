using System.Text.Json;
using Base.OrderGenerator.Commons.Responses;

namespace Base.OrderGenerator.Application.Exposures.Interfaces;

public interface ISymbolExposuresPort
{
    Task<DataMessage<JsonElement>> GetSymbolExposuresAsync(CancellationToken cancellationToken);
}
