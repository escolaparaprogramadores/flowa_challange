using System.Text.Json;
using Base.OrderGenerator.Commons.Responses;

namespace Base.OrderGenerator.Application.Orders.Interfaces;

public interface IStoredOrdersPort
{
    Task<DataMessage<JsonElement>> GetStoredOrdersPageAsync(string? requestedPageNumber, CancellationToken cancellationToken);

    Task DeleteAllStoredOrdersAsync(CancellationToken cancellationToken);
}
