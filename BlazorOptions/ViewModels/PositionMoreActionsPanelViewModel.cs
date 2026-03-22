using BlazorOptions.API.Common;

namespace BlazorOptions.ViewModels;

public sealed class PositionMoreActionsPanelViewModel : Bindable
{
    private readonly Func<Task> _openPositionSettings;

    public PositionMoreActionsPanelViewModel(Func<Task> openPositionSettings)
    {
        _openPositionSettings = openPositionSettings;
    }

    public Task OpenPositionSettingsAsync() => _openPositionSettings();
}
