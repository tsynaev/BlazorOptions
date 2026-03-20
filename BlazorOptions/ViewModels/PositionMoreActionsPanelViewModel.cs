using BlazorOptions.API.Common;

namespace BlazorOptions.ViewModels;

public sealed class PositionMoreActionsPanelViewModel : Bindable
{
    private readonly Func<Task> _openPositionSettings;
    private readonly Func<Task> _addPortfolio;

    public PositionMoreActionsPanelViewModel(
        Func<Task> openPositionSettings,
        Func<Task> addPortfolio)
    {
        _openPositionSettings = openPositionSettings;
        _addPortfolio = addPortfolio;
    }

    public Task OpenPositionSettingsAsync() => _openPositionSettings();

    public Task AddPortfolioAsync() => _addPortfolio();
}
