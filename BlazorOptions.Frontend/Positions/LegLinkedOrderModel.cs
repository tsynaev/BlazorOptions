namespace BlazorOptions.ViewModels;

public sealed record LegLinkedOrderModel(
    string OrderId,
    string Side,
    decimal Quantity,
    decimal? Price,
    decimal? ExpectedPnl);

