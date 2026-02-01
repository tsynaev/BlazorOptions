# Dialog Navigation

This app can open dialogs via a navigation service that maps view models to dialog components.

## Registration
- Register one dialog action per view model using `AddDialogAction<TViewModel>()` or use the default dialog helper with `AddDialog<TDialog, TViewModel>()`.
- A view model can only be registered once. Duplicate registrations throw at startup.

```csharp
builder.Services.AddDialog<TradingSymbolDialog, TradingSymbolDialogViewModel>();

builder.Services.AddDialogAction<TradingSymbolDialogViewModel>(async (vm, sp) =>
{
    var dialogService = sp.GetRequiredService<IDialogService>();
    var parameters = new DialogParameters
    {
        ["ViewModel"] = vm
    };

    _ = dialogService.ShowAsync<TradingSymbolDialog>("Trades", parameters);
    await Task.CompletedTask;
});
```

## Usage
Call the navigation service with a view model type. The registered action is invoked automatically.

```csharp
var viewModel = await navigationService.NavigateToAsync<TradingSymbolDialogViewModel>(vm =>
{
    return vm.LoadAsync(symbol, category, sinceDate);
});
```

## Dialog parameter
Dialogs should accept the view model via a `ViewModel` parameter:

```razor
@code {
    [Parameter] public TradingSymbolDialogViewModel? ViewModel { get; set; }
}
```
