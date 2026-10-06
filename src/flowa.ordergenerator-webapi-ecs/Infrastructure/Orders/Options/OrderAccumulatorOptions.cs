using System.ComponentModel.DataAnnotations;

namespace Flowa.OrderGenerator.Infrastructure.Orders.Options;

internal sealed class OrderAccumulatorOptions
{
    public const string SectionName = "OrderAccumulator";

    [Required, Url]
    public string BaseUrl { get; init; } = string.Empty;
}
