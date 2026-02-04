using System;
namespace BlazorOptions.ViewModels;

public enum LegType
{
    Call,
    Put,
    Future
}

public enum LegStatus
{
    New,
    Active,
    Missing
}

public class LegModel : Bindable
{
    private string _id = Guid.NewGuid().ToString();
    private bool _isIncluded = true;
    private bool _isReadOnly;
    private LegType _type = LegType.Call;
    private LegStatus _status = LegStatus.New;
    private decimal? _strike;
    private DateTime? _expirationDate;
    private decimal _size = 1;
    private decimal? _price;
    private decimal? _impliedVolatility;
    private string? _symbol;

    public string Id
    {
        get => _id;
        set => SetField(ref _id, value);
    }

    public bool IsIncluded
    {
        get => _isIncluded;
        set => SetField(ref _isIncluded, value);
    }

    public bool IsReadOnly
    {
        get => _isReadOnly;
        set => SetField(ref _isReadOnly, value);
    }

    public LegType Type
    {
        get => _type;
        set => SetField(ref _type, value);
    }

    public LegStatus Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }

    public decimal? Strike
    {
        get => _strike;
        set => SetField(ref _strike, value);
    }

    public DateTime? ExpirationDate
    {
        get => _expirationDate;
        set => SetField(ref _expirationDate, value);
    }

    public decimal Size
    {
        get => _size;
        set => SetField(ref _size, value);
    }

    public decimal? Price
    {
        get => _price;
        set => SetField(ref _price, value);
    }

    public decimal? ImpliedVolatility
    {
        get => _impliedVolatility;
        set => SetField(ref _impliedVolatility, value);
    }

    public string? Symbol
    {
        get => _symbol;
        set => SetField(ref _symbol, value);
    }

    public LegModel Clone()
    {
        return new LegModel
        {
            Id = IsReadOnly ? Id : Guid.NewGuid().ToString(),
            IsIncluded = IsIncluded,
            IsReadOnly = IsReadOnly,
            Type = Type,
            Status = Status,
            Strike = Strike,
            ExpirationDate = ExpirationDate,
            Size = Size,
            Price = Price,
            ImpliedVolatility = ImpliedVolatility,
            Symbol = Symbol
        };
    }
}
