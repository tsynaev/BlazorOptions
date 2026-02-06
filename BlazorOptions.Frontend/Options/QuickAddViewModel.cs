using BlazorOptions.Diagnostics;
using BlazorOptions.Services;

namespace BlazorOptions.ViewModels;

public sealed class QuickAddViewModel
{
    private readonly INotifyUserService _context;
    private readonly ILegsParserService _legsParserService;

    public string QuickLegInput { get; set; } = string.Empty;

    public string AddActionDescription => BuildActionDescription(QuickLegInput);

    public IReadOnlyList<LegPreviewItem> PreviewItems =>
        BuildPreviewItems(QuickLegInput);

    public decimal? Price { get; set; }

    public string? BaseAsset { get; set; }
    public LegsCollectionModel? Collection { get; set; }

    public event Func<LegModel,Task>? LegCreated;

    public QuickAddViewModel(
        INotifyUserService context,
        ILegsParserService legsParserService)
    {
        _context = context;
        _legsParserService = legsParserService;
    }

   

 

    public async Task OnQuickLegKeyDown(string key)
    {
        if (string.Equals(key, "Enter", StringComparison.Ordinal))
        {
            await AddQuickLegAsync();
        }
    }

    public async Task AddQuickLegAsync()
    {
        using var activity = ActivitySources.Telemetry.StartActivity("QuickAdd.AddQuickLeg");
        var leg = await AddLegFromTextWithResultAsync(QuickLegInput);
        if (leg is not null)
        {
            QuickLegInput = string.Empty;
        }
    }


    public async Task<LegModel?> AddLegFromTextWithResultAsync(string? input)
    {
        using var activity = ActivitySources.Telemetry.StartActivity("QuickAdd.ParseLeg");
        var collection = Collection;
        if (collection is null)
        {
            _context.NotifyUser("Select a position before adding a leg.");
            return null;
        }

        if (string.IsNullOrWhiteSpace(input))
        {
            _context.NotifyUser("Enter a leg expression like '+1 C 3400' or '+1 P'.");
            return null;
        }

        IReadOnlyList<LegModel> legs;
        try
        {
            legs = _legsParserService.ParseLegs(input, 1m, null, BaseAsset);
        }
        catch (LegsParserService.LegsParseException ex)
        {
            _context.NotifyUser(ex.Message);
            return null;
        }

        if (legs.Count == 0)
        {
            _context.NotifyUser("Enter a leg expression like '+1 C 3400' or '+1 P'.");
            return null;
        }

        if (legs.Count > 1)
        {
            _context.NotifyUser("Enter a single leg to add.");
            return null;
        }

        var leg = legs[0];
        await _legsParserService.ApplyTickerDefaultsAsync(legs, BaseAsset, Price);
        collection.Legs.Add(leg);
        await (LegCreated?.Invoke(leg) ?? Task.CompletedTask);
        return leg;
    }


    private string BuildActionDescription(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var collection = Collection;
        if (collection is null)
        {
            return "Select a position before adding a leg.";
        }

        try
        {
            var legs = _legsParserService.ParseLegs(input, 1m, null, BaseAsset);
            return _legsParserService.BuildPreviewDescription(legs, Price, BaseAsset);
        }
        catch (LegsParserService.LegsParseException ex)
        {
            return ex.Message;
        }
    }

    private IReadOnlyList<LegPreviewItem> BuildPreviewItems(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return Array.Empty<LegPreviewItem>();
        }

        try
        {
            var legs = _legsParserService.ParseLegs(input, 1m, null, BaseAsset);
            if (legs.Count == 0)
            {
                return Array.Empty<LegPreviewItem>();
            }

            return legs
                .Select(leg => new LegPreviewItem(
                    _legsParserService.BuildPreviewDescription(new[] { leg }, Price, BaseAsset),
                    leg.Size >= 0))
                .Where(item => !string.IsNullOrWhiteSpace(item.Description))
                .ToList();
        }
        catch (LegsParserService.LegsParseException)
        {
            return Array.Empty<LegPreviewItem>();
        }
    }


}
