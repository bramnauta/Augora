using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Augora.Providers.Podcasts;

/// <summary>Regularly checks the subscribed feeds, so new episodes are announced to their subscribers.</summary>
public sealed class PodcastRefreshService(PodcastProvider provider, ILogger<PodcastRefreshService> logger) : BackgroundService
{
    private static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(FirstCheck, stoppingToken);
            using var timer = new PeriodicTimer(Interval);
            do
            {
                try
                {
                    await provider.CheckForNewEpisodesAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Checking the podcasts for new episodes failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }
}
