using System.ComponentModel.DataAnnotations;

namespace Base.OrderGenerator.Infrastructure.Orders.Options;

internal sealed class OrderAccumulatorOptions
{
    public const string SectionName = "OrderAccumulator";

    [Required, Url]
    public string BaseUrl { get; init; } = string.Empty;
}
