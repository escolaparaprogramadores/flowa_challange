using System.Text.Json;
using Base.OrderGenerator.Application.Orders.Interfaces;
using Base.OrderGenerator.Commons.Responses;

namespace Base.OrderGenerator.Application.Orders.UseCases;

public sealed class ListOrdersUseCase(IStoredOrdersPort storedOrdersPort)
{
    public Task<DataMessage<JsonElement>> ListOrdersAsync(string? requestedPageNumber, CancellationToken cancellationToken) =>
        storedOrdersPort.GetStoredOrdersPageAsync(requestedPageNumber, cancellationToken);
}
