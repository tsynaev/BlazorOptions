namespace BlazorChart.Models;

public sealed record CandleVolumePoint(
    long Time,
    double Open,
    double High,
    double Low,
    double Close,
    double Volume);
