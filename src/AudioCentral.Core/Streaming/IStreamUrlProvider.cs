namespace AudioCentral.Core.Streaming;

/// <summary>
/// Builds the url under which players on the network can reach a stream session.
/// This is not necessarily the public url of the UI: players connect over the LAN.
/// </summary>
public interface IStreamUrlProvider
{
    Uri GetStreamUrl(StreamSession session);

    /// <summary>Makes a server-relative url (artwork) absolute for players on the LAN.</summary>
    Uri GetAbsoluteUrl(string relativePath);
}
