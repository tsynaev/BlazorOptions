namespace BlazorOptions.ViewModels;

public enum OptionLegType
{
    Call,
    Put,
    Future
}

public class OptionLegModel
{
    public Guid Id { get; } = Guid.NewGuid();

    public bool IsIncluded { get; set; } = true;

    public OptionLegType Type { get; set; } = OptionLegType.Call;

    public double Strike { get; set; } = 1000;

    public DateTime ExpirationDate { get; set; } = DateTime.UtcNow.Date.AddMonths(1);

    public double Size { get; set; } = 1;

    public double Price { get; set; } = 50;

    public double ImpliedVolatility { get; set; } = 65;
}
