namespace Base.OrderAccumulator.Domain.Orders;

// Reasons sent back in tag 58 and shown on the screen; the texts are part of the contract.
public static class OrderFieldMessages
{
    public const string OrderSymbolRequiredMessage = "Informe o símbolo.";
    public const string OrderSymbolInvalidMessage = "Símbolo inválido. Use PETR4, VALE3 ou VIIA4.";

    public const string OrderSideInvalidMessage = "Lado inválido. Use compra ou venda.";

    public const string OrderQuantityNotIntegerMessage = "A quantidade deve ser um número inteiro.";
    public const string OrderQuantityNotPositiveMessage = "A quantidade deve ser maior que zero.";
    public const string OrderQuantityTooLargeMessage = "A quantidade deve ser menor que 100.000.";

    public const string OrderPriceNotPositiveMessage = "O preço deve ser maior que zero.";
    public const string OrderPriceTooLargeMessage = "O preço deve ser menor que 1.000,00.";
    public const string OrderPriceOffTickMessage = "O preço deve ser múltiplo de 0,01.";
}
