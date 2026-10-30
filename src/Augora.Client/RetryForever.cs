using Microsoft.AspNetCore.SignalR.Client;

namespace Augora.Client;

/// <summary>A phone goes in and out of range all day: keep reconnecting, a little slower each time up to 30 seconds.</summary>
internal sealed class RetryForever : IRetryPolicy
{
    public TimeSpan? NextRetryDelay(RetryContext retryContext) =>
        TimeSpan.FromSeconds(Math.Min(30, 2 + retryContext.PreviousRetryCount * 2));
}
