using Augora.Contracts;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.JSInterop;

namespace Augora.Web.Services;

/// <summary>
/// Makes this browser tab a player: registers over the hub, executes commands with an
/// &lt;audio&gt; element (wwwroot/js/browserPlayer.js) and reports its state back.
/// </summary>
public sealed class BrowserPlayerService(IJSRuntime js, PlayerStore store) : IAsyncDisposable
{
    private const string EnabledKey = "augora.browserPlayer";
    private const string DeviceIdKey = "augora.deviceId";
    private const string VolumeKey = "augora.browserVolume";

    private IJSObjectReference? _module;
    private DotNetObjectReference<BrowserPlayerService>? _self;
    private bool _handlersAttached;

    public event Action? Changed;

    public bool IsEnabled { get; private set; }

    public string? PlayerId { get; private set; }

    /// <summary>The browser refused to start audio without a click (autoplay policy).</summary>
    public bool NeedsInteraction { get; private set; }

    /// <summary>Another tab of this browser took over the player.</summary>
    public bool WasReleased { get; private set; }

    /// <summary>Re-enables the player after a reload when the user had it on.</summary>
    public async Task RestoreAsync()
    {
        if (await js.InvokeAsync<string?>("localStorage.getItem", EnabledKey) == "1")
        {
            await EnableCoreAsync(unlock: false);
        }
    }

    /// <summary>Call from a click handler: the click also unlocks audio playback.</summary>
    public async Task EnableAsync()
    {
        await EnableCoreAsync(unlock: true);
        await js.InvokeVoidAsync("localStorage.setItem", EnabledKey, "1");
    }

    public async Task DisableAsync()
    {
        IsEnabled = false;
        PlayerId = null;
        NeedsInteraction = false;
        await js.InvokeVoidAsync("localStorage.removeItem", EnabledKey);
        if (_module is not null)
        {
            await _module.InvokeVoidAsync("stop");
        }

        if (store.Hub is { State: HubConnectionState.Connected } hub)
        {
            await hub.InvokeAsync(RemotePlayerHub.Unregister);
        }

        Changed?.Invoke();
    }

    /// <summary>Resumes after the autoplay policy blocked playback; must be called from a click.</summary>
    public async Task AllowPlaybackAsync()
    {
        if (_module is not null && await _module.InvokeAsync<string>("start") == "ok")
        {
            NeedsInteraction = false;
            Changed?.Invoke();
        }
    }

    [JSInvokable]
    public async Task OnStateChanged(RemotePlayerStateDto state)
    {
        if (IsEnabled && store.Hub is { State: HubConnectionState.Connected } hub)
        {
            await hub.SendAsync(RemotePlayerHub.ReportState, state);
        }

        if (state.State == PlaybackState.Playing && NeedsInteraction)
        {
            NeedsInteraction = false;
            Changed?.Invoke();
        }

        await js.InvokeVoidAsync("localStorage.setItem", VolumeKey, state.Volume.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            await _module.DisposeAsync();
        }

        _self?.Dispose();
    }

    private async Task EnableCoreAsync(bool unlock)
    {
        _module ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/browserPlayer.js");
        if (_self is null)
        {
            _self = DotNetObjectReference.Create(this);
            var stored = await js.InvokeAsync<string?>("localStorage.getItem", VolumeKey);
            var volume = double.TryParse(stored, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 1.0;
            await _module.InvokeVoidAsync("init", _self, volume);
        }

        if (unlock)
        {
            await _module.InvokeAsync<bool>("unlock");
        }

        AttachHandlers();
        IsEnabled = true;
        WasReleased = false;
        await RegisterAsync();
    }

    private void AttachHandlers()
    {
        if (_handlersAttached || store.Hub is not { } hub)
        {
            return;
        }

        _handlersAttached = true;
        hub.On<RemotePlayerCommandDto>(RemotePlayerHub.Command, ExecuteAsync);
        hub.On(RemotePlayerHub.Released, async () =>
        {
            IsEnabled = false;
            PlayerId = null;
            WasReleased = true;
            if (_module is not null)
            {
                await _module.InvokeVoidAsync("stop");
            }

            Changed?.Invoke();
        });
        hub.Reconnected += async _ =>
        {
            if (IsEnabled)
            {
                await RegisterAsync();
            }
        };
    }

    private async Task RegisterAsync()
    {
        if (store.Hub is not { State: HubConnectionState.Connected } hub)
        {
            return;
        }

        var deviceId = await js.InvokeAsync<string?>("localStorage.getItem", DeviceIdKey);
        if (string.IsNullOrEmpty(deviceId))
        {
            deviceId = Guid.NewGuid().ToString("N");
            await js.InvokeVoidAsync("localStorage.setItem", DeviceIdKey, deviceId);
        }

        var device = await _module!.InvokeAsync<DeviceDescription>("describe");
        var name = string.IsNullOrEmpty(device.Os) ? device.Browser : $"{device.Browser} ({device.Os})";
        PlayerId = await hub.InvokeAsync<string>(RemotePlayerHub.Register, new RegisterPlayerRequest(deviceId, name, device.Mobile ? PlayerKind.Phone : PlayerKind.Browser, Ephemeral: true));
        await _module!.InvokeVoidAsync("reportState");
        Changed?.Invoke();
    }

    private async Task ExecuteAsync(RemotePlayerCommandDto command)
    {
        if (_module is null || !IsEnabled)
        {
            return;
        }

        switch (command.Type)
        {
            case RemotePlayerCommandType.Play when command.Media is { } media:
                var result = await _module.InvokeAsync<string>("play", media);
                NeedsInteraction = result == "blocked";
                Changed?.Invoke();
                break;
            case RemotePlayerCommandType.Pause:
                await _module.InvokeVoidAsync("pause");
                break;
            case RemotePlayerCommandType.Resume:
                await AllowPlaybackAsync();
                break;
            case RemotePlayerCommandType.Stop:
                await _module.InvokeVoidAsync("stop");
                break;
            case RemotePlayerCommandType.SetVolume when command.Volume is { } volume:
                await _module.InvokeVoidAsync("setVolume", volume);
                break;
            case RemotePlayerCommandType.SetMuted when command.Muted is { } muted:
                await _module.InvokeVoidAsync("setMuted", muted);
                break;
            case RemotePlayerCommandType.Seek when command.PositionSeconds is { } position:
                await _module.InvokeVoidAsync("seek", position);
                break;
        }
    }

    private sealed record DeviceDescription(string Browser, string Os, bool Mobile);
}
