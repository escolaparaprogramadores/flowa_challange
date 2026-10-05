using Base.OrderAccumulator.Commons;
using Base.OrderAccumulator.Domain.Exposures;

namespace Base.OrderAccumulator.Application.Exposures.GetExposures;

// The exposure of the three symbols, read from the database for the screen.
public sealed class GetExposuresUseCase(ISymbolExposureReadRepository symbolExposureReadRepository)
{
    public const string ExposuresReadMessage = "Exposição dos símbolos lida.";

    public async Task<DataMessage<IReadOnlyList<SymbolExposure>>> GetExposuresAsync(CancellationToken cancellationToken = default) =>
        DataMessage<IReadOnlyList<SymbolExposure>>.CreateSuccessMessage(
            await symbolExposureReadRepository.GetSymbolExposuresAsync(cancellationToken), ExposuresReadMessage);
}
