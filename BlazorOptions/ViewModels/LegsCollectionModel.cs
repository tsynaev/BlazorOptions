using System.Collections.ObjectModel;

namespace BlazorOptions.ViewModels;

public class LegsCollectionModel
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public string Color { get; set; } = "#1976D2";

    public bool IsVisible { get; set; } = true;

    public ObservableCollection<OptionLegModel> Legs { get; set; } = new();
}
