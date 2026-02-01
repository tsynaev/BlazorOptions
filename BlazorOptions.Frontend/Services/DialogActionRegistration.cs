namespace BlazorOptions.Services;

public sealed record DialogActionRegistration(Type ViewModelType, Func<object, IServiceProvider, Task> Action);
