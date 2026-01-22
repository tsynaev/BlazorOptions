using System;
using System.Collections.ObjectModel;

namespace BlazorOptions.ViewModels;

public class PositionModel
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string BaseAsset { get; set; } = "ETH";

    public string QuoteAsset { get; set; } = "USDT";

    public string Name { get; set; } = "Position";

    public ObservableCollection<LegsCollectionModel> Collections { get; set; } = new();

    public ObservableCollection<ClosedPositionModel> ClosedPositions { get; set; } = new();

    public bool IncludeClosedPositions { get; set; }

    public double ClosedPositionsNetTotal { get; set; }
}

