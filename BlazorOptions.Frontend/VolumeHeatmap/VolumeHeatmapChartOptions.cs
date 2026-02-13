namespace BlazorOptions.ViewModels;

public sealed record VolumeHeatmapChartOptions(
    string Symbol,
    DateTime FromUtc,
    DateTime ToUtc,
    VolumeHeatmapMetric Metric,
    string[] Hours,
    string[] Weekdays,
    IReadOnlyList<VolumeHeatmapCell> Cells,
    VolumeHeatmapCell? MaxCell,
    double MinVolume,
    double MaxVolume);

public sealed record VolumeHeatmapCell(
    int HourIndex,
    int WeekdayIndex,
    double Volume);

public enum VolumeHeatmapMetric
{
    AvgVolumePerHour = 0,
    AvgOpenCloseDiffPerHour = 1
}

public sealed record VolumeHeatmapMetricOption(
    string Label,
    VolumeHeatmapMetric Value);
