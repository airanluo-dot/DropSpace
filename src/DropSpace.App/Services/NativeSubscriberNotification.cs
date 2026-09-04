using Microsoft.Extensions.Logging;

namespace DropSpace.App.Services;

internal static class NativeSubscriberNotification
{
    internal static void Invoke<T>(EventHandler<T>? handlers, object sender, T args, ILogger logger)
    {
        if (handlers is null) return;
        foreach (EventHandler<T> handler in handlers.GetInvocationList())
        {
            try { handler(sender, args); }
            catch (Exception exception) { Report(logger, exception); }
        }
    }

    internal static void Invoke(EventHandler? handlers, object sender, ILogger logger)
    {
        if (handlers is null) return;
        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try { handler(sender, EventArgs.Empty); }
            catch (Exception exception) { Report(logger, exception); }
        }
    }

    private static void Report(ILogger logger, Exception exception)
    {
        try { logger.LogWarning("Native notification subscriber failed: {Category}.", exception.GetType().Name); }
        catch { /* Logging cannot throw across a native boundary. */ }
    }
}
