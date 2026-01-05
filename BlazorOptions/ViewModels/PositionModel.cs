using System.Collections.ObjectModel;

namespace BlazorOptions.ViewModels;

public class PositionModel
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Pair { get; set; } = "ETH/USDT";

    public ObservableCollection<OptionLegModel> Legs { get; set; } = new();
}
