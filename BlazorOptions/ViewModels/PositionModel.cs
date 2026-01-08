using System.Collections.ObjectModel;

namespace BlazorOptions.ViewModels;

public class PositionModel
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string BaseAsset { get; set; } = "ETH";

    public string QuoteAsset { get; set; } = "USDT";

    public string Pair { get; set; } = "ETH/USDT";

    public ObservableCollection<OptionLegModel> Legs { get; set; } = new();
}
