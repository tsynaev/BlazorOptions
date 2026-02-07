namespace BlazorChart.Models;

public sealed record LiveCandleUpdate(long Time, double Open, double High, double Low, double Close, bool Confirm);
