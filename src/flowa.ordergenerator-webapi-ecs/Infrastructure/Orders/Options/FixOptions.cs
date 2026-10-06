using System.ComponentModel.DataAnnotations;

namespace Flowa.OrderGenerator.Infrastructure.Orders.Options;

internal sealed class FixOptions
{
    public const string SectionName = "Fix";

    [Required]
    public string AcceptorHost { get; init; } = string.Empty;

    [Range(1, 65535)]
    public int AcceptorPort { get; init; }

    [Range(1, 5)]
    public int ExecutionReportTimeoutSeconds { get; init; } = 5;
}
