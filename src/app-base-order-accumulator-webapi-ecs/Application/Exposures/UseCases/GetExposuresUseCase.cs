using Base.OrderAccumulator.Application.Exposures.Interfaces;
using Base.OrderAccumulator.Commons.Responses;
using Base.OrderAccumulator.Domain.Exposures.ValueObjects;

namespace Base.OrderAccumulator.Application.Exposures.UseCases;

public sealed class GetExposuresUseCase(ISymbolExposureReadRepository symbolExposureReadRepository)
{
    public const string ExposuresReadMessage = "Exposição dos símbolos lida.";

    public async Task<DataMessage<IReadOnlyList<SymbolExposure>>> GetExposuresAsync(CancellationToken cancellationToken = default) =>
        DataMessage<IReadOnlyList<SymbolExposure>>.CreateSuccessMessage(
            await symbolExposureReadRepository.GetSymbolExposuresAsync(cancellationToken), ExposuresReadMessage);
}
