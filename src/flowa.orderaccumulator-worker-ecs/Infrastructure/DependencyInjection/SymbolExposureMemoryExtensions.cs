using Flowa.OrderAccumulator.Application.Exposures.Interfaces;

namespace Flowa.OrderAccumulator.Infrastructure.DependencyInjection;

internal static class SymbolExposureMemoryExtensions
{
    public static async Task LoadSymbolExposureMemoryAsync(this IServiceProvider orderAccumulatorServiceProvider, CancellationToken cancellationToken = default)
    {
        await using var exposureReadScope = orderAccumulatorServiceProvider.CreateAsyncScope();
        var storedSymbolExposures = await exposureReadScope.ServiceProvider.GetRequiredService<ISymbolExposureReadRepository>().GetSymbolExposuresAsync(cancellationToken);
        orderAccumulatorServiceProvider.GetRequiredService<ISymbolExposureMemoryPort>().LoadStoredExposures(storedSymbolExposures);
    }
}
