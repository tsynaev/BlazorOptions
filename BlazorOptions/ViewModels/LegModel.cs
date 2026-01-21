using System;
namespace BlazorOptions.ViewModels;

public enum LegType
{
    Call,
    Put,
    Future
}

public class LegModel
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public bool IsIncluded { get; set; } = true;

    public bool IsReadOnly { get; set; }

    public LegType Type { get; set; } = LegType.Call;

    public double? Strike { get; set; } 

    public DateTime? ExpirationDate { get; set; }

    public double Size { get; set; } = 1;

    public double? Price { get; set; }

    public double? ImpliedVolatility { get; set; }

    public string? Symbol { get; set; }
}


