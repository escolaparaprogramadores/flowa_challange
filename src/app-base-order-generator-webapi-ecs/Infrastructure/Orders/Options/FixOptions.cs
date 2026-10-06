using System.ComponentModel.DataAnnotations;

namespace Base.OrderGenerator.Infrastructure.Orders.Options;

internal sealed class FixOptions
{
    public const string SectionName = "Fix";

    [Required]
    public string AcceptorHost { get; init; } = string.Empty;

    [Range(1, 65535)]
    public int AcceptorPort { get; init; }
}
