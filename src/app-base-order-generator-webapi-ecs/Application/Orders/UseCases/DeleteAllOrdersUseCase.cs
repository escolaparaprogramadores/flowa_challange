using Base.OrderGenerator.Application.Orders.Interfaces;
using Base.OrderGenerator.Commons.Responses;

namespace Base.OrderGenerator.Application.Orders.UseCases;

public sealed class DeleteAllOrdersUseCase(IStoredOrdersPort storedOrdersPort)
{
    public const string AllOrdersDeletedMessage = "Todas as ordens foram apagadas.";

    public async Task<DataMessage<bool>> DeleteAllOrdersAsync(CancellationToken cancellationToken)
    {
        await storedOrdersPort.DeleteAllStoredOrdersAsync(cancellationToken);
        return DataMessage<bool>.CreateSuccessMessage(true, AllOrdersDeletedMessage);
    }
}
