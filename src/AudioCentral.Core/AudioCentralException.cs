namespace AudioCentral.Core;

/// <summary>
/// An expected failure that is reported to API clients as Problem Details with a machine readable code.
/// </summary>
public class AudioCentralException(string code, string message, int statusCode = 400, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;

    public int StatusCode { get; } = statusCode;
}
