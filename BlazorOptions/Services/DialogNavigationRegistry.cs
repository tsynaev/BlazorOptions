namespace BlazorOptions.Services;

public sealed class DialogNavigationRegistry : IDialogNavigationRegistry
{
    private readonly Dictionary<Type, Func<object, IServiceProvider, Task>> _mapping;

    public DialogNavigationRegistry(IEnumerable<DialogActionRegistration> registrations)
    {
        _mapping = new Dictionary<Type, Func<object, IServiceProvider, Task>>();

        foreach (var registration in registrations)
        {
            if (_mapping.ContainsKey(registration.ViewModelType))
            {
                throw new InvalidOperationException(
                    $"Dialog for view model '{registration.ViewModelType.Name}' is already registered.");
            }

            _mapping[registration.ViewModelType] = registration.Action;
        }
    }

    public Func<object, IServiceProvider, Task> GetAction(Type viewModelType)
    {
        if (_mapping.TryGetValue(viewModelType, out var action))
        {
            return action;
        }

        throw new InvalidOperationException(
            $"Dialog for view model '{viewModelType.Name}' is not registered.");
    }
}
