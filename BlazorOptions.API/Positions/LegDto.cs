namespace BlazorOptions.API.Positions;

public sealed class LegDto
{
    public string Id { get; set; } = string.Empty;

    public bool IsIncluded { get; set; } = true;

    public bool IsReadOnly { get; set; }

    public LegStatus Status { get; set; } = LegStatus.New;

    public LegType Type { get; set; }

    public decimal? Strike { get; set; }

    public DateTime? ExpirationDate { get; set; }

    public decimal Size { get; set; }

    public decimal? Price { get; set; }

    public decimal? ImpliedVolatility { get; set; }

    public string? Symbol { get; set; }
}
