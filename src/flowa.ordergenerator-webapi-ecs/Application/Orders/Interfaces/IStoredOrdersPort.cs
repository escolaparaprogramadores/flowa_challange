using System.Text.Json;
using Flowa.OrderGenerator.Commons.Responses;

namespace Flowa.OrderGenerator.Application.Orders.Interfaces;

public interface IStoredOrdersPort
{
    Task<DataMessage<JsonElement>> GetStoredOrdersPageAsync(string? requestedPageNumber, CancellationToken cancellationToken);

    Task DeleteAllStoredOrdersAsync(CancellationToken cancellationToken);
}
