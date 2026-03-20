using BlazorOptions.API.Common;

namespace BlazorOptions.ViewModels;

public sealed class PositionNotesPanelViewModel : Bindable
{
    private readonly PositionViewModel _positionViewModel;

    public PositionNotesPanelViewModel(PositionViewModel positionViewModel)
    {
        _positionViewModel = positionViewModel;
    }

    public string Notes
    {
        get => _positionViewModel.Position?.Notes ?? string.Empty;
        set
        {
            if (_positionViewModel.Position is null)
            {
                return;
            }

            if (string.Equals(_positionViewModel.Position.Notes, value, StringComparison.Ordinal))
            {
                return;
            }

            _positionViewModel.Position.Notes = value;
            OnPropertyChanged();
        }
    }
}
