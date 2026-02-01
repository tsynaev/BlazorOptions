using System;
using System.Reflection;

namespace BlazorOptions.ViewModels;

public class ClosedPositionModel : Bindable
{
    private Guid _id = Guid.NewGuid();
    private string _symbol = string.Empty;
    private DateTime? _sinceDate;
    private long? _firstTradeTimestamp;
    private long? _lastProcessedTimestamp;
    private List<string> _lastProcessedIdsAtTimestamp = new();
    private decimal _positionSize;
    private decimal _avgPrice;
    private decimal _entryQty;
    private decimal _entryValue;
    private decimal _closeQty;
    private decimal _closeValue;
    private decimal _realized;
    private decimal _feeTotal;

    public Guid Id
    {
        get => _id;
        set => SetField(ref _id, value);
    }

    public string Symbol
    {
        get => _symbol;
        set => SetField(ref _symbol, value);
    }

    public DateTime? SinceDate
    {
        get => _sinceDate;
        set => SetField(ref _sinceDate, value);
    }

    public long? FirstTradeTimestamp
    {
        get => _firstTradeTimestamp;
        set => SetField(ref _firstTradeTimestamp, value);
    }

    public long? LastProcessedTimestamp
    {
        get => _lastProcessedTimestamp;
        set => SetField(ref _lastProcessedTimestamp, value);
    }

    public List<string> LastProcessedIdsAtTimestamp
    {
        get => _lastProcessedIdsAtTimestamp;
        set => SetField(ref _lastProcessedIdsAtTimestamp, value);
    }

    public decimal PositionSize
    {
        get => _positionSize;
        set => SetField(ref _positionSize, value);
    }

    public decimal AvgPrice
    {
        get => _avgPrice;
        set => SetField(ref _avgPrice, value);
    }

    public decimal EntryQty
    {
        get => _entryQty;
        set => SetField(ref _entryQty, value);
    }

    public decimal EntryValue
    {
        get => _entryValue;
        set => SetField(ref _entryValue, value);
    }

    public decimal CloseQty
    {
        get => _closeQty;
        set => SetField(ref _closeQty, value);
    }

    public decimal CloseValue
    {
        get => _closeValue;
        set => SetField(ref _closeValue, value);
    }

    public decimal Realized
    {
        get => _realized;
        set => SetField(ref _realized, value);
    }

    public decimal FeeTotal
    {
        get => _feeTotal;
        set => SetField(ref _feeTotal, value);
    }


    public decimal AvgEntryPrice => EntryQty > 0m ? EntryValue / EntryQty : 0m;

    public decimal AvgClosePrice => CloseQty > 0m ? CloseValue / CloseQty : 0m;

    public void ResetCalculationCache()
    {
        FirstTradeTimestamp = null;
        LastProcessedTimestamp = null;
        LastProcessedIdsAtTimestamp = new List<string>();
        PositionSize = 0m;
        AvgPrice = 0m;
        EntryQty = 0m;
        EntryValue = 0m;
        CloseQty = 0m;
        CloseValue = 0m;
        Realized = 0m;
        FeeTotal = 0m;
    }
}
