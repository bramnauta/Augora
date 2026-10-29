namespace AudioCentral.Core.Providers;

/// <summary>The AudioCentral user of the current request, if any. Implemented by the server with the HTTP context.</summary>
public interface ICurrentUser
{
    string? UserId { get; }
}

internal sealed class NoCurrentUser : ICurrentUser
{
    public string? UserId => null;
}
