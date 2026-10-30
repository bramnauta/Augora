using System.Collections.Concurrent;

namespace Augora.Core;

/// <summary>
/// Settings an administrator changes in the UI. A value set here wins over the same setting in the configuration
/// (environment variables, appsettings), which stays the default.
/// </summary>
public interface IRuntimeSettings
{
    /// <summary>The stored value, or <c>null</c> when the setting was never changed in the UI.</summary>
    string? Get(string key);

    /// <summary>Stores a value; <c>null</c> removes it, so the configuration applies again.</summary>
    Task SetAsync(string key, string? value, CancellationToken cancellationToken);

    /// <summary>Raised after a value changed; the argument is its key.</summary>
    event EventHandler<string>? Changed;
}

/// <summary>Persistence of <see cref="IRuntimeSettings"/>.</summary>
public interface IRuntimeSettingsStore
{
    Task<IReadOnlyDictionary<string, string>> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(string key, string? value, CancellationToken cancellationToken);
}

/// <summary>Keeps all values in memory (they are few and read often); the store is only touched on start and on change.</summary>
public sealed class RuntimeSettings(IRuntimeSettingsStore? store = null) : IRuntimeSettings
{
    private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler<string>? Changed;

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (store is null)
        {
            return;
        }

        _values.Clear();
        foreach (var (key, value) in await store.LoadAsync(cancellationToken))
        {
            _values[key] = value;
        }
    }

    public string? Get(string key) => _values.GetValueOrDefault(key);

    public async Task SetAsync(string key, string? value, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            value = null;
        }

        if (store is not null)
        {
            await store.SaveAsync(key, value, cancellationToken);
        }

        if (value is null)
        {
            _values.TryRemove(key, out _);
        }
        else
        {
            _values[key] = value;
        }

        Changed?.Invoke(this, key);
    }
}
