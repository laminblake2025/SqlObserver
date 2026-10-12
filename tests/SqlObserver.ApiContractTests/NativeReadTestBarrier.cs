namespace SqlObserver.ApiContractTests;

internal static class NativeReadTestBarrier
{
    internal static async Task WaitForStartAsync(Task started, Task request)
    {
        // Hosted TestServer startup may contend with other contract hosts. This
        // readiness bound is separate from the unchanged cancellation deadline.
        Task completed = await Task.WhenAny(started, request).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Same(started, completed);
        await started;
    }
}
