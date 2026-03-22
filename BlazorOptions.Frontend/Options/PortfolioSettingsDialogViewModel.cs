namespace BlazorOptions.ViewModels;

public sealed class PortfolioSettingsDialogViewModel
{
    private readonly PositionViewModel _positionViewModel;

    public PortfolioSettingsDialogViewModel(PositionViewModel positionViewModel)
    {
        _positionViewModel = positionViewModel;
    }

    public Guid CollectionId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string Color { get; private set; } = "#1976D2";

    public bool CanRemove => false;

    public void Load(Guid collectionId)
    {
        if (CollectionId == collectionId && !string.IsNullOrEmpty(Name))
        {
            return;
        }

        CollectionId = collectionId;
        var collection = _positionViewModel.LegsCollection;
        if (collection?.Collection.Id != collectionId)
        {
            Name = string.Empty;
            return;
        }

        Name = collection.Collection.Name;
        Color = collection.Collection.Color;
    }

    public void SetName(string name)
    {
        Name = name;
    }

    public void SetColor(string color)
    {
        Color = color;
    }

    public async Task SaveAsync()
    {
        var collection = _positionViewModel.LegsCollection;
        if (collection?.Collection.Id != CollectionId)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(Name))
        {
            collection.Collection.Name = Name.Trim();
        }

        if (!string.IsNullOrWhiteSpace(Color))
        {
            collection.Collection.Color = Color;
        }

        await _positionViewModel.PersistPositionAsync();
        _positionViewModel.UpdateChart();
        _positionViewModel.NotifyStateChanged();
    }

    public Task<bool> RemoveAsync() => Task.FromResult(false);
}
