using System.Text.Json;

namespace Base.OrderGenerator.Commons;

// The HTTP routes of the OrderAccumulator the OrderGenerator forwards. "data" comes back as the OrderAccumulator
// wrote it, so the OrderGenerator answers the same body without a second envelope. A call the OrderAccumulator does
// not answer throws, and the GlobalErrorHandler turns it into the 503.
public interface IOrderAccumulatorHttpClient
{
    Task<DataMessage<JsonElement>> GetSymbolExposuresAsync(CancellationToken cancellationToken);

    Task<DataMessage<JsonElement>> GetStoredOrdersPageAsync(string? requestedPageNumber, CancellationToken cancellationToken);

    Task DeleteAllStoredOrdersAsync(CancellationToken cancellationToken);
}
