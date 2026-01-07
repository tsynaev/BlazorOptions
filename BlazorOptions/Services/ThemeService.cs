namespace BlazorOptions.Services;

public enum ThemeMode
{
    System,
    Light,
    Dark
}

public class ThemeService
{
    public event Action? OnChange;

    public ThemeMode Mode { get; private set; } = ThemeMode.System;

    public bool IsSystemDarkMode { get; private set; }

    public bool IsDarkMode => Mode switch
    {
        ThemeMode.Dark => true,
        ThemeMode.Light => false,
        ThemeMode.System => IsSystemDarkMode,
        _ => IsSystemDarkMode
    };

    public Task SetIsDarkMode(bool isDarkMode)
    {
        SetMode(isDarkMode ? ThemeMode.Dark : ThemeMode.Light);

        return Task.CompletedTask;
    }

    public Task UpdateSystemPreference(bool isDarkMode)
    {
        if (IsSystemDarkMode == isDarkMode)
        {
            return Task.CompletedTask;
        }

        IsSystemDarkMode = isDarkMode;
        NotifyStateChanged();
        return Task.CompletedTask;
    }

    public void SetMode(ThemeMode mode)
    {
        if (Mode == mode)
        {
            return;
        }

        Mode = mode;
        NotifyStateChanged();
    }

    private void NotifyStateChanged()
    {
        OnChange?.Invoke();
    }
}
