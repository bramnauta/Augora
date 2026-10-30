using System.Collections.Concurrent;
using Augora.Contracts;
using Augora.Core.Players;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Augora.Players.GoogleCast;

/// <summary>
/// Finds Google Cast devices and groups through mDNS and the manually added device list.
/// </summary>
public sealed class CastPlayerProvider(
    IServiceScopeFactory scopeFactory,
    IOptions<GoogleCastOptions> options,
    ILoggerFactory loggerFactory) : BackgroundService, IPlayerProvider
{
    public const string ProviderId = "googlecast";

    private readonly ConcurrentDictionary<string, CastPlayer> _players = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger _logger = loggerFactory.CreateLogger<CastPlayerProvider>();
    private readonly GoogleCastOptions _options = options.Value;

    public string Id => ProviderId;

    public string Name => "Google Cast";

    public IReadOnlyCollection<IPlayer> Players => _players.Values.ToList();

    public event EventHandler<IPlayer>? PlayerAdded;

    public event EventHandler<IPlayer>? PlayerRemoved;

    public async Task<ManualCastDeviceDto> AddManualDeviceAsync(AddManualCastDeviceRequest request, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var device = await scope.ServiceProvider.GetRequiredService<ICastDeviceStore>().AddAsync(request, cancellationToken);
        AddOrUpdate(CastDevice.FromManual(device));
        return device;
    }

    public async Task<bool> RemoveManualDeviceAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        if (!await scope.ServiceProvider.GetRequiredService<ICastDeviceStore>().RemoveAsync(id, cancellationToken))
        {
            return false;
        }

        if (_players.TryRemove(CastDevice.FromManual(new ManualCastDeviceDto(id, "", "", 0)).Id, out var player))
        {
            PlayerRemoved?.Invoke(this, player);
            await player.DisposeAsync();
        }

        return true;
    }

    public async Task<IReadOnlyList<ManualCastDeviceDto>> GetManualDevicesAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ICastDeviceStore>().GetAllAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        foreach (var player in _players.Values)
        {
            await player.DisposeAsync();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            foreach (var device in await GetManualDevicesAsync(stoppingToken))
            {
                AddOrUpdate(CastDevice.FromManual(device));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Loading manual Cast devices failed");
        }

        foreach (var device in _options.Devices.Where(d => !string.IsNullOrWhiteSpace(d.Id) && !string.IsNullOrWhiteSpace(d.Host)))
        {
            AddOrUpdate(CastDevice.FromOptions(device));
        }

        if (!_options.Discovery)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var device in await CastDiscovery.FindAsync(loggerFactory))
                {
                    AddOrUpdate(CastDevice.FromOptions(device));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Cast discovery failed");
            }

            await Task.Delay(_options.DiscoveryInterval, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private void AddOrUpdate(CastDevice device)
    {
        // A manually added device that is also discovered keeps its manual entry.
        if (!device.Id.StartsWith("cast:manual-", StringComparison.Ordinal)
            && _players.Values.Any(p => p.Id.StartsWith("cast:manual-", StringComparison.Ordinal) && p.Device.Host == device.Host && p.Device.Port == device.Port))
        {
            return;
        }

        if (_players.TryGetValue(device.Id, out var existing))
        {
            existing.UpdateDevice(device);
            return;
        }

        var player = new CastPlayer(device, _options.ReconnectInterval, _options.ReceiverAppId, loggerFactory);
        if (_players.TryAdd(device.Id, player))
        {
            _logger.LogInformation("Found Cast {Kind} {Name} at {Host}:{Port}", device.Kind, device.Name, device.Host, device.Port);
            player.Start();
            PlayerAdded?.Invoke(this, player);
        }
    }
}
