using BlazorOptions.Diagnostics;
using System.Diagnostics;
using System.Globalization;

namespace BlazorOptions.Services;

public interface ITelemetryService
{
    Activity? StartActivity(string name, ActivityKind kind = ActivityKind.Internal);
}

public sealed class TelemetryService : ITelemetryService
{
    private readonly ActivityListener _listener;

    public TelemetryService()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => string.Equals(source.Name, ActivitySources.Name, StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = OnActivityStopped
        };

        ActivitySource.AddActivityListener(_listener);
    }

    public Activity? StartActivity(string name, ActivityKind kind = ActivityKind.Internal)
    {
        return ActivitySources.Telemetry.StartActivity(name, kind);
    }

    private static void OnActivityStopped(Activity activity)
    {
        var depth = GetDepth(activity);
        var indent = depth == 0 ? string.Empty : new string(' ', depth * 2);
        var elapsed = activity.Duration.TotalMilliseconds.ToString("0.##", CultureInfo.InvariantCulture);
        Console.WriteLine($"{indent}{activity.DisplayName} => {elapsed}ms");
    }

    private static int GetDepth(Activity activity)
    {
        var depth = 0;
        var parent = activity.Parent;
        while (parent is not null)
        {
            depth++;
            parent = parent.Parent;
        }

        return depth;
    }
}
