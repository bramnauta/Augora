namespace Augora.Core;

/// <summary>
/// An expected failure that is reported to API clients as Problem Details with a machine readable code.
/// </summary>
public class AugoraException(string code, string message, int statusCode = 400, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;

    public int StatusCode { get; } = statusCode;

    /// <summary>Extra Problem Details members, e.g. the players holding a Spotify account.</summary>
    public IDictionary<string, object?> Extensions { get; } = new Dictionary<string, object?>();
}
