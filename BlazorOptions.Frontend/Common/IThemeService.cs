namespace BlazorOptions.Services;

public interface IThemeService
{
    event Action? OnChange;
    ThemeMode Mode { get; }
    bool IsSystemDarkMode { get; }
    bool IsDarkMode { get; }
    Task SetIsDarkMode(bool isDarkMode);
    void SetMode(ThemeMode mode);
}
