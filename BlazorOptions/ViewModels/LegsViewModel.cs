using System.Collections.ObjectModel;
using BlazorOptions.Shared;
using Microsoft.AspNetCore.Components.Web;
using MudBlazor;

namespace BlazorOptions.ViewModels;

public class LegsViewModel
{
    private readonly PositionBuilderViewModel _positionBuilder;
    private readonly IDialogService _dialogService;

    public LegsViewModel(PositionBuilderViewModel positionBuilder, IDialogService dialogService)
    {
        _positionBuilder = positionBuilder;
        _dialogService = dialogService;
    }

    public ObservableCollection<OptionLegModel> Legs => _positionBuilder.Legs;

    public IEnumerable<OptionLegType> LegTypes => Enum.GetValues<OptionLegType>();

    public string QuickLegInput
    {
        get => _positionBuilder.QuickLegInput;
        set => _positionBuilder.QuickLegInput = value;
    }

    public bool HasActivePosition => _positionBuilder.SelectedPosition is not null;

    public async Task OnQuickLegKeyDown(KeyboardEventArgs args)
    {
        if (args.Key == "Enter")
        {
            await AddQuickLegAsync();
        }
    }

    public async Task AddQuickLegAsync()
    {
        await _positionBuilder.AddLegFromTextAsync(QuickLegInput);
    }

    public async Task AddLegAsync()
    {
        if (_positionBuilder.SelectedPosition is null)
        {
            return;
        }

        var parameters = new DialogParameters
        {
            [nameof(OptionChainDialog.Position)] = _positionBuilder.SelectedPosition,
            [nameof(OptionChainDialog.UnderlyingPrice)] = _positionBuilder.SelectedPrice ?? _positionBuilder.LivePrice
        };

        var options = new DialogOptions
        {
            CloseOnEscapeKey = true,
            MaxWidth = MaxWidth.Large,
            FullWidth = true
        };

        var dialog = await _dialogService.ShowAsync<OptionChainDialog>("Add leg", parameters, options);
        var result = await dialog.Result;

        if (result is null || result.Canceled || result.Data is not IEnumerable<OptionLegModel> legs)
        {
            return;
        }

        await _positionBuilder.UpdateLegsAsync(legs);
        _positionBuilder.NotifyStateChanged();
    }

    public async Task RemoveLegAsync(OptionLegModel leg)
    {
        if (await _positionBuilder.RemoveLegAsync(leg))
        {
            _positionBuilder.UpdateChart();
            _positionBuilder.NotifyStateChanged();
        }
    }

    public async Task UpdateLegIncludedAsync(OptionLegModel leg, bool include)
    {
        leg.IsIncluded = include;
        await PersistAndRefreshAsync();
    }

    public async Task UpdateLegTypeAsync(OptionLegModel leg, OptionLegType type)
    {
        leg.Type = type;
        await PersistAndRefreshAsync();
    }

    public async Task UpdateLegStrikeAsync(OptionLegModel leg, double strike)
    {
        leg.Strike = strike;
        await PersistAndRefreshAsync();
    }

    public async Task UpdateLegExpirationAsync(OptionLegModel leg, DateTime? date)
    {
        if (date.HasValue)
        {
            leg.ExpirationDate = date.Value;
        }

        await PersistAndRefreshAsync();
    }

    public async Task UpdateLegSizeAsync(OptionLegModel leg, double size)
    {
        leg.Size = size;
        await PersistAndRefreshAsync();
    }

    public async Task UpdateLegPriceAsync(OptionLegModel leg, double price)
    {
        leg.Price = price;
        await PersistAndRefreshAsync();
    }

    public async Task UpdateLegIvAsync(OptionLegModel leg, double iv)
    {
        leg.ImpliedVolatility = iv;
        await PersistAndRefreshAsync();
    }

    private async Task PersistAndRefreshAsync()
    {
        await _positionBuilder.PersistPositionsAsync();
        _positionBuilder.UpdateTemporaryPnls();
        _positionBuilder.UpdateChart();
        _positionBuilder.NotifyStateChanged();
    }
}
