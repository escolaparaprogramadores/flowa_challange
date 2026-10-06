using Base.OrderAccumulator.Application.ErrorHandling;
using Base.OrderAccumulator.Application.Exposures.Interfaces;
using Base.OrderAccumulator.Commons.Responses;
using Base.OrderAccumulator.Domain.Exposures.ValueObjects;

namespace Base.OrderAccumulator.Application.Exposures.UseCases;

public sealed class GetExposuresUseCase
{
    public const string ExposuresReadMessage = "Exposição dos símbolos lida.";

    private readonly ISymbolExposureReadRepository symbolExposureReadRepository;

    public GetExposuresUseCase(ISymbolExposureReadRepository symbolExposureReadRepository)
    {
        this.symbolExposureReadRepository = symbolExposureReadRepository ?? throw new ArgumentNullException(nameof(symbolExposureReadRepository));
    }

    public async Task<DataMessage<IReadOnlyList<SymbolExposure>>> GetExposuresAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return DataMessage<IReadOnlyList<SymbolExposure>>.CreateSuccessMessage(
                await symbolExposureReadRepository.GetSymbolExposuresAsync(cancellationToken), ExposuresReadMessage);
        }
        catch (Exception exposuresReadFailure)
        {
            return UseCaseFailureDataMessageMapper.MapUseCaseFailureToDataMessage<IReadOnlyList<SymbolExposure>>(exposuresReadFailure);
        }
    }
}
