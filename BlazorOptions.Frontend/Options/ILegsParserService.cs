namespace BlazorOptions.ViewModels;

public interface ILegsParserService
{
    IReadOnlyList<LegModel> ParseLegs(string input, decimal defaultSize, DateTime? defaultExpiration, string? baseAsset);

    Task ApplyTickerDefaultsAsync(IReadOnlyList<LegModel> legs, string? baseAsset, decimal? underlyingPrice);

    string BuildPreviewDescription(IEnumerable<LegModel> legs, decimal? underlyingPrice, string? baseAsset);

}
