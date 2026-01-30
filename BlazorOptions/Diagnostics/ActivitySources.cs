using System.Diagnostics;

namespace BlazorOptions.Diagnostics
{
    internal static class ActivitySources
    {
        internal const string Name = "BlazorOptions.Telemetry";

        internal static readonly ActivitySource Telemetry = new ActivitySource(Name);
    }

}
