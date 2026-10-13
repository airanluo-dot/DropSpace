namespace DropSpace.App.Services.Media;

/// <summary>Independent, persistent ownership budgets for mandatory and optional subscriptions.</summary>
internal sealed class MediaSubscriptionAdmission(int managerCapacity = 4, int primaryCapacity = 12, int recoveryCapacity = 16,
    IMediaOperationExecutor? executor = null)
{
    // Total native ownership remains capped at 32. Optional recovery churn can exhaust
    // only its own partition; it cannot prevent manager reconnect or primary admission.
    // Keep these pools for the service lifetime, including across soft restart.
    internal BoundedMediaOperation Manager { get; } = new(managerCapacity, 3, executor);
    internal BoundedMediaOperation Primary { get; } = new(primaryCapacity, 3, executor);
    internal BoundedMediaOperation Recovery { get; } = new(recoveryCapacity, 3, executor);
}
