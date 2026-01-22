using System;

namespace BlazorOptions.ViewModels;

public class ClosedPositionModel
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Symbol { get; set; } = string.Empty;

    public DateTime? SinceDate { get; set; }
}
