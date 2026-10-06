using System.Globalization;
using Flowa.OrderAccumulator.Application.Orders.Responses;
using Flowa.OrderAccumulator.Application.Orders.UseCases;
using Flowa.OrderAccumulator.Commons.Responses;
using Flowa.OrderAccumulator.Domain.Orders.ValueObjects;
using Flowa.OrderAccumulator.Entrypoint.ErrorHandling;
using Flowa.OrderAccumulator.Infrastructure.Orders.Repositories;
using Microsoft.Extensions.Primitives;

namespace Flowa.OrderAccumulator.Entrypoint.Orders.Endpoints;

public static class OrdersEndpoints
{
    public static IEndpointRouteBuilder MapOrdersEndpoints(this IEndpointRouteBuilder orderAccumulatorRoutes)
    {
        orderAccumulatorRoutes.MapGet("/api/orders", async (HttpRequest orderListRequest, ListOrdersUseCase listOrdersUseCase, CancellationToken cancellationToken) =>
        {
            if (!TryReadOrderListPageNumber(orderListRequest.Query["page"], out var orderListPageNumber))
            {
                return DataMessage<OrderPageResponse>.CreateErrorMessage(
                    "Página inválida.", ResultStatus.InvalidInput,
                    [$"A página deve ser um número inteiro de {OrderListPagePolicy.FirstPageNumber} a {OrderListPagePolicy.MaxPageNumber}."], "invalid-page")
                    .ConvertToHttpResponse();
            }

            return (await listOrdersUseCase.ListOrdersAsync(orderListPageNumber, cancellationToken)).ConvertToHttpResponse(storedOrderPage =>
                OrderPageResponse.MapFromStoredOrderPage(orderListPageNumber, OrderListReadRepository.OrdersPerPage, storedOrderPage));
        });

        orderAccumulatorRoutes.MapDelete("/api/orders", async (DeleteAllOrdersUseCase deleteAllOrdersUseCase, CancellationToken cancellationToken) =>
        {
            var deleteAllOrdersMessage = await deleteAllOrdersUseCase.DeleteAllOrdersAsync(cancellationToken);
            return deleteAllOrdersMessage.Success ? Results.NoContent() : deleteAllOrdersMessage.ConvertToHttpResponse();
        });

        return orderAccumulatorRoutes;
    }

    private static bool TryReadOrderListPageNumber(StringValues pageQueryValues, out int orderListPageNumber)
    {
        orderListPageNumber = OrderListPagePolicy.FirstPageNumber;
        if (pageQueryValues.Count == 0)
            return true;

        return pageQueryValues.Count == 1
            && int.TryParse(pageQueryValues[0], NumberStyles.None, CultureInfo.InvariantCulture, out orderListPageNumber)
            && OrderListPagePolicy.IsAllowedPageNumber(orderListPageNumber);
    }
}
