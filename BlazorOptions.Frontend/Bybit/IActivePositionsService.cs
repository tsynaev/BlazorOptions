namespace BlazorOptions.Services;

public interface IActivePositionsService : IAsyncDisposable
{
    string? LastError { get; }

    event Func<IReadOnlyList<BybitPosition>, Task>? PositionsUpdated;

    event Action<BybitPosition>? PositionUpdated;

    Task InitializeAsync();

    Task ReloadFromExchangeAsync();

    Task<IEnumerable<BybitPosition>> GetPositionsAsync();
}
