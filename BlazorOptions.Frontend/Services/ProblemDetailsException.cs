using System;
using BlazorOptions.API.TradingHistory;

namespace BlazorOptions.Services;

public sealed class ProblemDetailsException : Exception
{
    public ProblemDetailsException(ProblemDetails details)
        : base(details.Title ?? details.Detail ?? "Request failed.")
    {
        Details = details ?? throw new ArgumentNullException(nameof(details));
    }

    public ProblemDetails Details { get; }
}
