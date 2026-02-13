using System.Collections.ObjectModel;

namespace BlazorOptions.API.Positions;

public class LegsCollectionModel
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public string Color { get; set; } = "#1976D2";

    public bool IsVisible { get; set; } = true;

    public ObservableCollection<LegModel> Legs { get; set; } = new();
}
