using System.Text.Json;
using Flowa.OrderGenerator.Commons.Responses;

namespace Flowa.OrderGenerator.Application.Exposures.Interfaces;

public interface ISymbolExposuresPort
{
    Task<DataMessage<JsonElement>> GetSymbolExposuresAsync(CancellationToken cancellationToken);
}
