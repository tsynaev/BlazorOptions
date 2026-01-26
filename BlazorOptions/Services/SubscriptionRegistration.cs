namespace BlazorOptions.Services
{
    public sealed class SubscriptionRegistration : IDisposable
    {
        private Action? _dispose;

        public SubscriptionRegistration(Action dispose)
        {
            _dispose = dispose;
        }

        public void Dispose()
        {
            var dispose = Interlocked.Exchange(ref _dispose, null);
            dispose?.Invoke();
        }
    }
}