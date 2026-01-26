using System;
using System.Diagnostics;
using System.Globalization;

namespace BlazorOptions.Services;

public interface ITelemetryService
{
    Activity? StartActivity(string name, ActivityKind kind = ActivityKind.Internal);
}

public sealed class TelemetryService : ITelemetryService
{
    private const string ActivitySourceName = "BlazorOptions.Telemetry";
    private readonly ActivitySource _activitySource;
    private readonly ActivityListener _listener;

    public TelemetryService()
    {
        _activitySource = new(ActivitySourceName);
        _listener = new ActivityListener
        {
            ShouldListenTo = source => string.Equals(source.Name, ActivitySourceName, StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = OnActivityStopped
        };

        ActivitySource.AddActivityListener(_listener);
    }

    public Activity? StartActivity(string name, ActivityKind kind = ActivityKind.Internal)
    {
        return _activitySource.StartActivity(name, kind);
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
