using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace Augora.Server.Discovery;

/// <summary>What this server announces: <c>{Instance}._augora._tcp.local</c> on <c>{Host}</c> and <see cref="Port"/>.</summary>
internal sealed record MdnsService(string Instance, string Host, int Port, IReadOnlyList<string> Txt)
{
    public const string ServiceType = "_augora._tcp.local";

    /// <summary>The name that lists all service types of a host (DNS-SD).</summary>
    public const string ServiceEnumeration = "_services._dns-sd._udp.local";

    public string InstanceName => $"{Instance}.{ServiceType}";

    /// <summary>Whether a question is about this service, its instance, its host or the service enumeration.</summary>
    public bool Matches(string name) =>
        name.Equals(ServiceType, StringComparison.OrdinalIgnoreCase)
        || name.Equals(InstanceName, StringComparison.OrdinalIgnoreCase)
        || name.Equals(Host, StringComparison.OrdinalIgnoreCase)
        || name.Equals(ServiceEnumeration, StringComparison.OrdinalIgnoreCase);
}

/// <summary>The few multicast DNS messages needed to announce one service (RFC 6762 and 6763).</summary>
internal static class MdnsPacket
{
    private const ushort TypeA = 1;
    private const ushort TypePtr = 12;
    private const ushort TypeTxt = 16;
    private const ushort TypeSrv = 33;
    private const ushort ClassIn = 1;
    private const ushort ClassInCacheFlush = 0x8001;

    private const uint LongTtl = 4500;
    private const uint HostTtl = 120;

    /// <summary>The names asked about in a query; empty for responses and for anything malformed.</summary>
    public static IReadOnlyList<string> ReadQuestionNames(ReadOnlySpan<byte> packet)
    {
        const int headerLength = 12;
        if (packet.Length < headerLength || (packet[2] & 0x80) != 0)
        {
            return [];
        }

        var count = BinaryPrimitives.ReadUInt16BigEndian(packet[4..]);
        var names = new List<string>();
        var offset = headerLength;
        for (var i = 0; i < count; i++)
        {
            if (!TryReadName(packet, ref offset, out var name) || offset + 4 > packet.Length)
            {
                break;
            }

            offset += 4; // type and class
            names.Add(name);
        }

        return names;
    }

    /// <summary>
    /// A response with every record of the service. <paramref name="goodbye"/> sets the lifetimes to zero, which
    /// removes the service from the caches of listeners.
    /// </summary>
    public static byte[] BuildResponse(MdnsService service, IPAddress address, bool goodbye)
    {
        var longTtl = goodbye ? 0 : LongTtl;
        var hostTtl = goodbye ? 0 : HostTtl;

        var srv = new List<byte>();
        WriteUInt16(srv, 0); // priority
        WriteUInt16(srv, 0); // weight
        WriteUInt16(srv, (ushort)service.Port);
        WriteName(srv, service.Host);

        var txt = new List<byte>();
        foreach (var entry in service.Txt)
        {
            var bytes = Encoding.UTF8.GetBytes(entry);
            txt.Add((byte)Math.Min(bytes.Length, 255));
            txt.AddRange(bytes.Take(255));
        }

        if (txt.Count == 0)
        {
            txt.Add(0);
        }

        var records = new List<byte>();
        AddRecord(records, MdnsService.ServiceType, TypePtr, ClassIn, longTtl, NameBytes(service.InstanceName));
        AddRecord(records, MdnsService.ServiceEnumeration, TypePtr, ClassIn, longTtl, NameBytes(MdnsService.ServiceType));
        AddRecord(records, service.InstanceName, TypeSrv, ClassInCacheFlush, hostTtl, srv);
        AddRecord(records, service.InstanceName, TypeTxt, ClassInCacheFlush, longTtl, txt);
        AddRecord(records, service.Host, TypeA, ClassInCacheFlush, hostTtl, address.GetAddressBytes());

        var packet = new List<byte>();
        WriteUInt16(packet, 0); // id
        WriteUInt16(packet, 0x8400); // response, authoritative
        WriteUInt16(packet, 0); // questions
        WriteUInt16(packet, 5); // answers
        WriteUInt16(packet, 0); // authority
        WriteUInt16(packet, 0); // additional
        packet.AddRange(records);
        return [.. packet];
    }

    private static void AddRecord(List<byte> packet, string name, ushort type, ushort recordClass, uint ttl, IEnumerable<byte> data)
    {
        var bytes = data.ToArray();
        WriteName(packet, name);
        WriteUInt16(packet, type);
        WriteUInt16(packet, recordClass);
        Span<byte> ttlBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(ttlBytes, ttl);
        packet.AddRange(ttlBytes.ToArray());
        WriteUInt16(packet, (ushort)bytes.Length);
        packet.AddRange(bytes);
    }

    private static List<byte> NameBytes(string name)
    {
        var bytes = new List<byte>();
        WriteName(bytes, name);
        return bytes;
    }

    private static void WriteName(List<byte> packet, string name)
    {
        foreach (var label in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var bytes = Encoding.UTF8.GetBytes(label);
            if (bytes.Length > 63)
            {
                throw new ArgumentException($"DNS label '{label}' is longer than 63 bytes.", nameof(name));
            }

            packet.Add((byte)bytes.Length);
            packet.AddRange(bytes);
        }

        packet.Add(0);
    }

    private static void WriteUInt16(List<byte> packet, ushort value)
    {
        packet.Add((byte)(value >> 8));
        packet.Add((byte)value);
    }

    /// <summary>Reads a name, following compression pointers; <paramref name="offset"/> ends after the name in place.</summary>
    private static bool TryReadName(ReadOnlySpan<byte> packet, ref int offset, out string name)
    {
        name = "";
        var labels = new List<string>();
        var position = offset;
        var jumped = false;
        var pointers = 0;
        while (true)
        {
            if (position >= packet.Length)
            {
                return false;
            }

            var length = packet[position];
            if (length == 0)
            {
                if (!jumped)
                {
                    offset = position + 1;
                }

                name = string.Join('.', labels);
                return true;
            }

            if ((length & 0xC0) == 0xC0)
            {
                if (position + 1 >= packet.Length || ++pointers > 16)
                {
                    return false;
                }

                if (!jumped)
                {
                    offset = position + 2;
                }

                jumped = true;
                position = ((length & 0x3F) << 8) | packet[position + 1];
                continue;
            }

            if ((length & 0xC0) != 0 || position + 1 + length > packet.Length)
            {
                return false;
            }

            labels.Add(Encoding.UTF8.GetString(packet.Slice(position + 1, length)));
            position += 1 + length;
        }
    }
}
