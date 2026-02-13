namespace BlazorChart.Models;

public sealed class ChartRange
{
    public ChartRange(double xMin, double xMax, double yMin, double yMax)
    {
        XMin = xMin;
        XMax = xMax;
        YMin = yMin;
        YMax = yMax;
    }

    public double XMin { get; set; }

    public double XMax { get; set; }

    public double YMin { get; set; }

    public double YMax { get; set; }
}
