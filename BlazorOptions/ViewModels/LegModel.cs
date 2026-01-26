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

    public decimal? Strike { get; set; } 

    public DateTime? ExpirationDate { get; set; }

    public decimal Size { get; set; } = 1;

    public decimal? Price { get; set; }

    public decimal? ImpliedVolatility { get; set; }

    public string? Symbol { get; set; }

    public LegModel Clone()
    {
        return new LegModel
        {
            Id = IsReadOnly ? Id : Guid.NewGuid().ToString(),
            IsIncluded = IsIncluded,
            IsReadOnly = IsReadOnly,
            Type = Type,
            Strike = Strike,
            ExpirationDate = ExpirationDate,
            Size = Size,
            Price = Price,
            ImpliedVolatility = ImpliedVolatility,
            Symbol = Symbol
        };
    }
}


