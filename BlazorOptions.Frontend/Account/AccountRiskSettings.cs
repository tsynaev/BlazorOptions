namespace BlazorOptions.ViewModels;

public sealed class AccountRiskSettings
{
    public decimal MaxLossOptionPercent { get; set; } = 30m;

    public decimal MaxLossFuturesPercent { get; set; } = 30m;
}
