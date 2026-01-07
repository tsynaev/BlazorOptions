export function registerThemePreferenceListener(dotNetReference) {
    const mediaQuery = window.matchMedia("(prefers-color-scheme: dark)");

    dotNetReference.invokeMethodAsync("UpdateSystemPreference", mediaQuery.matches);

    const handler = (event) => {
        dotNetReference.invokeMethodAsync("UpdateSystemPreference", event.matches);
    };

    if (mediaQuery.addEventListener) {
        mediaQuery.addEventListener("change", handler);
    } else if (mediaQuery.addListener) {
        mediaQuery.addListener(handler);
    }
}
