using System.Collections.ObjectModel;
using BlazorOptions.Shared;
using Microsoft.AspNetCore.Components.Web;
using MudBlazor;

namespace BlazorOptions.ViewModels;

public sealed class LegsCollectionViewModel
{
    private readonly PositionBuilderViewModel _positionBuilder;
    private readonly IDialogService _dialogService;

    public LegsCollectionViewModel(PositionBuilderViewModel positionBuilder, IDialogService dialogService, LegsCollectionModel collection)
    {
        _positionBuilder = positionBuilder;
        _dialogService = dialogService;
        Collection = collection;
    }

    public LegsCollectionModel Collection { get; }

    public ObservableCollection<OptionLegModel> Legs => Collection.Legs;

    public IEnumerable<OptionLegType> LegTypes => Enum.GetValues<OptionLegType>();

    public string QuickLegInput { get; set; } = string.Empty;

    public string Name
    {
        get => Collection.Name;
        set => _ = SetNameAsync(value);
    }

    public string Color
    {
        get => Collection.Color;
        set => _ = SetColorAsync(value);
    }

    public bool HasActivePosition => _positionBuilder.SelectedPosition is not null;

    public bool IsSelected => _positionBuilder.SelectedCollection?.Id == Collection.Id;

    public bool IsVisible
    {
        get => Collection.IsVisible;
        set => _ = SetVisibilityAsync(value);
    }

    public async Task OnQuickLegKeyDown(KeyboardEventArgs args)
    {
        if (args.Key == "Enter")
        {
            await AddQuickLegAsync();
        }
    }

    public async Task AddQuickLegAsync()
    {
        if (!EnsureActiveCollection())
        {
            return;
        }

        await _positionBuilder.AddLegFromTextAsync(QuickLegInput);
        QuickLegInput = string.Empty;
    }

    public async Task AddLegAsync()
    {
        if (!EnsureActiveCollection())
        {
            return;
        }

        if (_positionBuilder.SelectedPosition is null)
        {
            return;
        }

        var parameters = new DialogParameters
        {
            [nameof(OptionChainDialog.Position)] = _positionBuilder.SelectedPosition,
            [nameof(OptionChainDialog.Collection)] = Collection,
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

    public async Task DuplicateCollectionAsync()
    {
        if (!EnsureActiveCollection())
        {
            return;
        }

        await _positionBuilder.DuplicateCollectionAsync();
    }

    public async Task SetVisibilityAsync(bool isVisible)
    {
        if (!EnsureActiveCollection())
        {
            return;
        }

        await _positionBuilder.UpdateCollectionVisibilityAsync(Collection.Id, isVisible);
    }

    public async Task SetColorAsync(string color)
    {
        if (!EnsureActiveCollection())
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(color))
        {
            return;
        }

        Collection.Color = color;
        await _positionBuilder.PersistPositionsAsync();
        _positionBuilder.UpdateChart();
        _positionBuilder.NotifyStateChanged();
    }

    public async Task SetNameAsync(string name)
    {
        if (!EnsureActiveCollection())
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        Collection.Name = name.Trim();
        await _positionBuilder.PersistPositionsAsync();
        _positionBuilder.UpdateChart();
        _positionBuilder.NotifyStateChanged();
    }

    public async Task RemoveCollectionAsync()
    {
        if (_positionBuilder.SelectedPosition is null)
        {
            return;
        }

        var confirmed = await _dialogService.ShowMessageBox(
            "Remove portfolio?",
            $"Delete {Collection.Name} and its legs?",
            yesText: "Delete",
            cancelText: "Cancel");

        if (confirmed != true)
        {
            return;
        }

        await _positionBuilder.RemoveCollectionAsync(Collection.Id);
    }

    public async Task OpenSettingsAsync()
    {
        var parameters = new DialogParameters
        {
            [nameof(PortfolioSettingsDialog.CollectionId)] = Collection.Id
        };

        var options = new DialogOptions
        {
            CloseOnEscapeKey = true,
            MaxWidth = MaxWidth.Small,
            FullWidth = true
        };

        await _dialogService.ShowAsync<PortfolioSettingsDialog>("Portfolio settings", parameters, options);
    }

    public async Task RemoveLegAsync(OptionLegModel leg)
    {
        if (!EnsureActiveCollection())
        {
            return;
        }

        if (await _positionBuilder.RemoveLegAsync(leg))
        {
            _positionBuilder.UpdateChart();
            _positionBuilder.NotifyStateChanged();
        }
    }

    public async Task UpdateLegIncludedAsync(OptionLegModel leg, bool include)
    {
        if (!EnsureActiveCollection())
        {
            return;
        }

        leg.IsIncluded = include;
        await PersistAndRefreshAsync();
    }

    public async Task UpdateLegTypeAsync(OptionLegModel leg, OptionLegType type)
    {
        if (!EnsureActiveCollection())
        {
            return;
        }

        leg.Type = type;
        await PersistAndRefreshAsync();
    }

    public async Task UpdateLegStrikeAsync(OptionLegModel leg, double strike)
    {
        if (!EnsureActiveCollection())
        {
            return;
        }

        leg.Strike = strike;
        await PersistAndRefreshAsync();
    }

    public async Task UpdateLegExpirationAsync(OptionLegModel leg, DateTime? date)
    {
        if (!EnsureActiveCollection())
        {
            return;
        }

        if (date.HasValue)
        {
            leg.ExpirationDate = date.Value;
        }

        await PersistAndRefreshAsync();
    }

    public async Task UpdateLegSizeAsync(OptionLegModel leg, double size)
    {
        if (!EnsureActiveCollection())
        {
            return;
        }

        leg.Size = size;
        await PersistAndRefreshAsync();
    }

    public async Task UpdateLegPriceAsync(OptionLegModel leg, double price)
    {
        if (!EnsureActiveCollection())
        {
            return;
        }

        leg.Price = price;
        await PersistAndRefreshAsync();
    }

    public async Task UpdateLegIvAsync(OptionLegModel leg, double iv)
    {
        if (!EnsureActiveCollection())
        {
            return;
        }

        leg.ImpliedVolatility = iv;
        await PersistAndRefreshAsync();
    }

    private bool EnsureActiveCollection()
    {
        return _positionBuilder.TrySetActiveCollection(Collection.Id);
    }

    private async Task PersistAndRefreshAsync()
    {
        await _positionBuilder.PersistPositionsAsync();
        _positionBuilder.UpdateTemporaryPnls();
        _positionBuilder.UpdateChart();
        _positionBuilder.NotifyStateChanged();
    }
}
