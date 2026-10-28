using System.Diagnostics.CodeAnalysis;
using AudioCentral.Contracts;

namespace AudioCentral.Core.Media;

/// <summary>
/// Identifies an item within a provider, serialised as <c>{provider}:{type}:{id}</c>.
/// The id part may contain any character, including colons.
/// </summary>
public readonly record struct MediaItemRef(string Provider, MediaType Type, string Id)
{
    public static MediaItemRef Parse(string value) =>
        TryParse(value, out var result)
            ? result
            : throw new AudioCentralException(ErrorCodes.InvalidUri, $"'{value}' is not a valid media uri.", 400);

    public static bool TryParse([NotNullWhen(true)] string? value, out MediaItemRef result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Split(':', 3);
        if (parts.Length != 3 || parts[0].Length == 0 || !Enum.TryParse<MediaType>(parts[1], ignoreCase: true, out var type))
        {
            return false;
        }

        result = new MediaItemRef(parts[0], type, parts[2]);
        return true;
    }

    public override string ToString() => $"{Provider}:{Type.ToString().ToLowerInvariant()}:{Id}";
}
