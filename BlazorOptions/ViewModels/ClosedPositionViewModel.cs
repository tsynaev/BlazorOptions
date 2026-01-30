using BlazorOptions.Services;
using System.ComponentModel;

namespace BlazorOptions.ViewModels;

public sealed class ClosedPositionViewModel: Bindable
{
    private readonly TradingHistoryStorageService _storageService;
    private readonly ITelemetryService _telemetryService;
    private readonly IExchangeService _exchangeService;
    private readonly int _defaultLookbackDays = 180;
    private bool _isCalculating;

    
    public Func<ClosedPositionViewModel, Task>? Removed;
    public Func<Task>? UpdateCompleted;


    private ClosedPositionModel _model;


    public ClosedPositionViewModel(
        TradingHistoryStorageService storageService,
        ITelemetryService telemetryService,
        IExchangeService exchangeService
       )
    {
        _storageService = storageService;
        _telemetryService = telemetryService;
        _exchangeService = exchangeService;
       
    }

    public ClosedPositionModel Model    
    {
        get => _model;
        init 
        {
            if (Equals(value, _model))
            {
                return;
            }

            value.PropertyChanged += ModelPropertyChanged;

            _model = value;
            OnPropertyChanged();
        }
    }

    private void ModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(Model));
    }

    public bool IsCalculating
    {
        get => _isCalculating;
        set => SetField(ref _isCalculating, value);
    }

    public async Task RecalculateAsync(bool forceFull)
    {
        if (_isCalculating)
        {
            return;
        }

        IsCalculating = true;
        bool changed = false;


        void OnChanged(object? sender, PropertyChangedEventArgs args) => changed = true;

        Model.PropertyChanged += OnChanged;
        try
        {
            using var activity = _telemetryService.StartActivity($"{nameof(ClosedPositionViewModel)}.{nameof(RecalculateAsync)}");
            if (forceFull)
            {
                Model.ResetCalculationCache();
            }

            var sinceTimestamp = ResolveSinceTimestamp(forceFull);
            IReadOnlyList<TradingHistoryEntry> entries = sinceTimestamp.HasValue
                ? await _storageService.LoadBySymbolSummarySinceAsync(Model.Symbol, sinceTimestamp.Value)
                : await _storageService.LoadBySymbolSummaryAsync(Model.Symbol);

            ApplyEntries(entries, forceFull);
        }
        finally
        {
            Model.PropertyChanged -= OnChanged;
            IsCalculating = false;
            if (changed) await RaiseUpdateCompleted();
        }
    }



    public async Task SetSinceDateAsync(DateTime? sinceDate)
    {

        if (Model.SinceDate == sinceDate)
        {
            return;
        }

        Model.SinceDate = sinceDate;
        Model.ResetCalculationCache();

        _ = RecalculateAsync(forceFull: true);
    }

    public Task SetSinceTimeAsync(TimeSpan? timePart)
    {

        var datePart = Model.SinceDate?.Date;
        if (!datePart.HasValue && timePart.HasValue)
        {
            datePart = DateTime.Today;
        }

        var combined = datePart.HasValue ? datePart.Value.Date + (timePart ?? TimeSpan.Zero) : (DateTime?)null;
        return SetSinceDateAsync(combined);
    }

    private async Task RaiseUpdateCompleted()
    {
        if (UpdateCompleted != null)
        {
            await UpdateCompleted.Invoke();
        }
        
    }

    private void ApplyEntries(IReadOnlyList<TradingHistoryEntry> entries, bool forceFull)
    {
        if (entries.Count == 0)
        {
            return;
        }

        var positionSize = Model.PositionSize;
        var avgPrice = Model.AvgPrice;
        var entryQty = Model.EntryQty;
        var entryValue = Model.EntryValue;
        var closeQty = Model.CloseQty;
        var closeValue = Model.CloseValue;
        var realized = Model.Realized;
        var feeTotal = Model.FeeTotal;

        var firstTimestamp = Model.FirstTradeTimestamp ?? long.MaxValue;
        var lastProcessedTimestamp = forceFull ? null : Model.LastProcessedTimestamp;
        var lastProcessedIds = forceFull ? new List<string>() : Model.LastProcessedIdsAtTimestamp;
        var processedAny = false;

        long? maxTimestamp = lastProcessedTimestamp;
        var maxTimestampIds = new List<string>();

        foreach (var entry in entries
                     .OrderBy(item => item.Timestamp ?? long.MinValue)
                     .ThenBy(item => item.Id, StringComparer.Ordinal))
        {
            if (lastProcessedTimestamp.HasValue && entry.Timestamp.HasValue)
            {
                if (entry.Timestamp.Value < lastProcessedTimestamp.Value)
                {
                    continue;
                }

                if (entry.Timestamp.Value == lastProcessedTimestamp.Value
                    && lastProcessedIds.Count > 0
                    && lastProcessedIds.Contains(entry.Id))
                {
                    continue;
                }
            }

            if (entry.Timestamp.HasValue)
            {
                var entryTimestamp = entry.Timestamp.Value;
                if (entryTimestamp < firstTimestamp)
                {
                    firstTimestamp = entryTimestamp;
                }

                if (!maxTimestamp.HasValue || entryTimestamp > maxTimestamp.Value)
                {
                    maxTimestamp = entryTimestamp;
                    maxTimestampIds.Clear();
                    maxTimestampIds.Add(entry.Id);
                }
                else if (entryTimestamp == maxTimestamp.Value)
                {
                    maxTimestampIds.Add(entry.Id);
                }
            }

            var qty = entry.Size;
            var price = entry.Price;
            var fee = entry.Fee;
            var side = (entry.Side ?? string.Empty).Trim();
            var type = (entry.TransactionType ?? string.Empty).Trim();

            if (string.Equals(type, "SETTLEMENT", StringComparison.OrdinalIgnoreCase))
            {
                qty = 0m;
                price = 0m;
            }
            else if (string.Equals(type, "DELIVERY", StringComparison.OrdinalIgnoreCase))
            {
                if (_exchangeService.TryParseSymbol(entry.Symbol, out _, out _, out var strike, out var legType))
                {
                    var intrinsicCall = Math.Max(entry.Price - strike, 0m);
                    var intrinsicPut = Math.Max(strike - entry.Price, 0m);

                    if (legType == LegType.Call)
                    {
                        price = intrinsicCall;
                    }
                    else if (legType == LegType.Put)
                    {
                        price = intrinsicPut;
                    }
                }
            }
            else if (!string.Equals(type, "TRADE", StringComparison.OrdinalIgnoreCase))
            {
                qty = 0m;
            }

            var qtySigned = Round10(string.Equals(side, "SELL", StringComparison.OrdinalIgnoreCase) ? -qty : qty);
            var closeTradeQty = Math.Sign(qtySigned) == -Math.Sign(positionSize)
                ? Round10(Math.Min(Math.Abs(qtySigned), Math.Abs(positionSize)))
                : 0m;
            var openTradeQty = Round10(qtySigned - Math.Sign(qtySigned) * closeTradeQty);

            var cashBefore = -avgPrice * positionSize;
            var cashAfter = cashBefore + (-avgPrice * closeTradeQty * Math.Sign(qtySigned)) + (-price * openTradeQty);
            var positionAfter = Round10(positionSize + qtySigned);
            var avgAfter = Math.Abs(positionAfter) < 0.000000001m ? 0m : -cashAfter / positionAfter;

            if (closeTradeQty != 0m)
            {
                realized += positionSize > 0m
                    ? (price - avgPrice) * closeTradeQty
                    : (avgPrice - price) * closeTradeQty;
            }

            if (openTradeQty != 0m)
            {
                var openQtyAbs = Math.Abs(openTradeQty);
                entryQty += openQtyAbs;
                entryValue += price * openQtyAbs;
            }

            if (closeTradeQty != 0m)
            {
                closeQty += closeTradeQty;
                closeValue += price * closeTradeQty;
            }

            feeTotal += fee;
            positionSize = positionAfter;
            avgPrice = avgAfter;
            processedAny = true;
        }

        if (!processedAny)
        {
            return;
        }

        Model.PositionSize = positionSize;
        Model.AvgPrice = avgPrice;
        Model.EntryQty = entryQty;
        Model.EntryValue = entryValue;
        Model.CloseQty = closeQty;
        Model.CloseValue = closeValue;
        Model.Realized = realized;
        Model.FeeTotal = feeTotal;

        if (firstTimestamp != long.MaxValue)
        {
            Model.FirstTradeTimestamp = firstTimestamp;
        }

        if (maxTimestamp.HasValue)
        {
            Model.LastProcessedTimestamp = maxTimestamp;
            Model.LastProcessedIdsAtTimestamp = maxTimestampIds;
        }
    }

    private long? ResolveSinceTimestamp(bool forceFull)
    {
        DateTime? sinceDate = Model.SinceDate;

        if (!sinceDate.HasValue && _defaultLookbackDays > 0)
        {
            sinceDate = DateTime.Now.Date.AddDays(-_defaultLookbackDays);
        }

        if (!forceFull && Model.LastProcessedTimestamp.HasValue)
        {
            var processedDate = DateTimeOffset.FromUnixTimeMilliseconds(Model.LastProcessedTimestamp.Value).LocalDateTime;
            if (!sinceDate.HasValue || processedDate > sinceDate.Value)
            {
                return Model.LastProcessedTimestamp.Value;
            }
        }

        if (sinceDate.HasValue)
        {
            return new DateTimeOffset(DateTime.SpecifyKind(sinceDate.Value, DateTimeKind.Local))
                .ToUnixTimeMilliseconds();
        }

        return null;
    }

    private DateTime? ResolveFirstTradeDate()
    {
        if (!Model.FirstTradeTimestamp.HasValue)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(Model.FirstTradeTimestamp.Value).ToLocalTime().DateTime;
        }
        catch
        {
            return null;
        }
    }

    private static decimal Round10(decimal value)
    {
        return Math.Round(value, 10, MidpointRounding.AwayFromZero);
    }

    public async Task RemoveClosedPositionAsync()
    {

        if (Removed != null)
        {
            await Removed.Invoke(this);
        }
    }

}