using BlazorOptions.API.Common;
using BlazorOptions.Services;

namespace BlazorOptions.ViewModels;

public sealed class PositionEquityPanelViewModel : Bindable
{
    public IExchangeService ExchangeService { get; set; } = null!;

    public bool ShowUpdatedTimestamp => true;
}
