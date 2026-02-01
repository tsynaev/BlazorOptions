namespace BlazorOptions.API.Positions;

public sealed class LegsCollectionDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Color { get; set; } = string.Empty;

    public bool IsVisible { get; set; }

    public List<LegDto> Legs { get; set; } = new();
}
