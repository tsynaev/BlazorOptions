using System;
using System.Collections.Generic;

namespace BlazorOptions.ViewModels;

public sealed class LegGroup
{
    public LegGroup(DateTime? expirationDate, IReadOnlyList<LegViewModel> legs)
    {
        ExpirationDate = expirationDate;
        Legs = legs;
    }

    public DateTime? ExpirationDate { get; }

    public IReadOnlyList<LegViewModel> Legs { get; }
}
