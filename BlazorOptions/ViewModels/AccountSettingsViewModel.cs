using BlazorOptions.Services;

namespace BlazorOptions.ViewModels;

public class AccountSettingsViewModel : IDisposable
{
    private readonly ThemeService _themeService;

    public AccountSettingsViewModel(ThemeService themeService)
    {
        _themeService = themeService;
        _themeService.OnChange += HandleThemeChanged;
    }

    public event Action? OnChange;

    public ThemeMode SelectedTheme
    {
        get => _themeService.Mode;
        set => _themeService.SetMode(value);
    }

    public string SelectedThemeDescription => SelectedTheme switch
    {
        ThemeMode.System => "Follows your device theme automatically.",
        ThemeMode.Dark => "For low-light environments.",
        ThemeMode.Light => "For brighter interfaces.",
        _ => string.Empty
    };

    public string SystemPreferenceLabel => _themeService.IsSystemDarkMode
        ? "System preference: Dark"
        : "System preference: Light";

    public IReadOnlyList<ThemeOption> ThemeOptions { get; } = new[]
    {
        new ThemeOption(ThemeMode.System, "System"),
        new ThemeOption(ThemeMode.Light, "Light"),
        new ThemeOption(ThemeMode.Dark, "Dark")
    };

    public void Dispose()
    {
        _themeService.OnChange -= HandleThemeChanged;
    }

    private void HandleThemeChanged()
    {
        OnChange?.Invoke();
    }
}

public record ThemeOption(ThemeMode Mode, string Label);
