namespace BlazorOptions.ViewModels;

public sealed class PortfolioSettingsDialogViewModel
{
    private readonly PositionBuilderViewModel _positionBuilder;

    public PortfolioSettingsDialogViewModel(PositionBuilderViewModel positionBuilder)
    {
        _positionBuilder = positionBuilder;
    }

    public Guid CollectionId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string Color { get; private set; } = "#1976D2";

    public bool CanRemove => _positionBuilder.SelectedPosition?.Collections?.Count > 1;

    public void Load(Guid collectionId)
    {
        if (CollectionId == collectionId && !string.IsNullOrEmpty(Name))
        {
            return;
        }

        CollectionId = collectionId;
        var collection = _positionBuilder.SelectedPosition?.Collections
            .FirstOrDefault(item => item.Collection.Id == collectionId);
        if (collection is null)
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
        var collection = _positionBuilder.SelectedPosition?.Collections
            .FirstOrDefault(item => item.Collection.Id == CollectionId);
        if (collection is null)
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

        await _positionBuilder.SelectedPosition.PersistPositionAsync();
        _positionBuilder.UpdateChart();
        _positionBuilder.NotifyStateChanged();
    }

    public Task<bool> RemoveAsync()
    {
        var collection = _positionBuilder.SelectedPosition?.Collections
            .FirstOrDefault(item => item.Collection.Id == CollectionId);
        if (collection is null)
        {
            return Task.FromResult(false);
        }

        return _positionBuilder.SelectedPosition?.RemoveCollectionAsync(collection.Collection)
               ?? Task.FromResult(false);
    }
}
