using System.Text.Json;
using Base.OrderGenerator.Commons;

namespace Base.OrderGenerator.Application.Exposures.GetExposures;

// The exposure of the three symbols, as the OrderAccumulator reads it.
public sealed class GetExposuresUseCase(IOrderAccumulatorHttpClient orderAccumulatorHttpClient)
{
    public Task<DataMessage<JsonElement>> GetExposuresAsync(CancellationToken cancellationToken) =>
        orderAccumulatorHttpClient.GetSymbolExposuresAsync(cancellationToken);
}
