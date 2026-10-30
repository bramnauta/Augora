namespace Augora.Contracts;

/// <summary>
/// A Google Cast device that is added by hand, for networks where mDNS does not reach the server
/// (e.g. the speakers live in a different VLAN).
/// </summary>
public sealed record ManualCastDeviceDto(Guid Id, string Name, string Host, int Port);

public sealed record AddManualCastDeviceRequest(string Name, string Host, int Port = 8009);
