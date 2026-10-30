using Augora.Contracts;

namespace Augora.Players.GoogleCast;

/// <summary>
/// Storage for devices that are added by hand because mDNS does not reach them.
/// </summary>
public interface ICastDeviceStore
{
    Task<IReadOnlyList<ManualCastDeviceDto>> GetAllAsync(CancellationToken cancellationToken);

    Task<ManualCastDeviceDto> AddAsync(AddManualCastDeviceRequest request, CancellationToken cancellationToken);

    Task<bool> RemoveAsync(Guid id, CancellationToken cancellationToken);
}
