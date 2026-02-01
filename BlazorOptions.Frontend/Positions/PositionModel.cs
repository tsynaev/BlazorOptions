using System;
using System.Collections.ObjectModel;

namespace BlazorOptions.ViewModels;

public class PositionModel : Bindable
{
    private Guid _id = Guid.NewGuid();
    private string _baseAsset = "ETH";
    private string _quoteAsset = "USDT";
    private string _name = "Position";
    private string _notes = string.Empty;
    private ObservableCollection<LegsCollectionModel> _collections = new();
    private ClosedModel _closed = new();

    public Guid Id
    {
        get => _id;
        set => SetField(ref _id, value);
    }

    public string BaseAsset
    {
        get => _baseAsset;
        set => SetField(ref _baseAsset, value);
    }

    public string QuoteAsset
    {
        get => _quoteAsset;
        set => SetField(ref _quoteAsset, value);
    }

    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    public string Notes
    {
        get => _notes;
        set => SetField(ref _notes, value);
    }

    public ObservableCollection<LegsCollectionModel> Collections
    {
        get => _collections;
        set => SetField(ref _collections, value);
    }

    public ClosedModel Closed
    {
        get => _closed;
        set => SetField(ref _closed, value);
    }
}

public class ClosedModel: Bindable
{
    private bool _include;
    private ObservableCollection<ClosedPositionModel> _positions = new();
    private decimal _totalClosePnl;
    private decimal _totalFee;

    public ObservableCollection<ClosedPositionModel> Positions
    {
        get => _positions;
        init
        {
            SetField(ref _positions, value);
        }
    }

    public bool Include
    {
        get => _include;
        set => SetField(ref _include, value);
    }

    public decimal TotalNet => TotalClosePnl - TotalFee;

    public decimal TotalClosePnl
    {
        get => _totalClosePnl;
        set
        {
            if (SetField(ref _totalClosePnl, value))
            {
                OnPropertyChanged(nameof(TotalNet));
            }
        }
    }

    public decimal TotalFee
    {
        get => _totalFee;
        set
        {
            if (SetField(ref _totalFee, value))
            {
                OnPropertyChanged(nameof(TotalNet));
            }
        }
    }
}
