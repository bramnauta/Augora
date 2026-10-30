using System.Buffers.Binary;
using System.Net;
using System.Text;
using Augora.Server.Discovery;

namespace Augora.Tests.Server;

public sealed class MdnsPacketTests
{
    private static readonly MdnsService Service = new("Augora on nas", "augora-nas.local", 4727, ["version=0.1.0", "path=/"]);

    /// <summary>A query for the given names (type PTR, class IN) without name compression.</summary>
    private static byte[] Query(params string[] names)
    {
        var packet = new List<byte> { 0, 0, 0, 0, 0, (byte)names.Length, 0, 0, 0, 0, 0, 0 };
        foreach (var name in names)
        {
            foreach (var label in name.Split('.'))
            {
                packet.Add((byte)label.Length);
                packet.AddRange(Encoding.UTF8.GetBytes(label));
            }

            packet.AddRange([0, 0, 12, 0, 1]);
        }

        return [.. packet];
    }

    [Fact]
    public void Questions_are_read()
    {
        var names = MdnsPacket.ReadQuestionNames(Query("_augora._tcp.local", "_http._tcp.local"));

        Assert.Equal(["_augora._tcp.local", "_http._tcp.local"], names);
    }

    [Fact]
    public void Compressed_names_are_followed()
    {
        // Second question is "x" + a pointer to the first name at offset 12.
        var packet = new List<byte> { 0, 0, 0, 0, 0, 2, 0, 0, 0, 0, 0, 0 };
        packet.AddRange([7, .. "_augora"u8, 4, .. "_tcp"u8, 5, .. "local"u8, 0, 0, 12, 0, 1]);
        packet.AddRange([1, (byte)'x', 0xC0, 12, 0, 12, 0, 1]);

        Assert.Equal(["_augora._tcp.local", "x._augora._tcp.local"], MdnsPacket.ReadQuestionNames([.. packet]));
    }

    [Fact]
    public void Responses_and_garbage_are_not_questions()
    {
        var response = MdnsPacket.BuildResponse(Service, IPAddress.Parse("192.168.1.10"), goodbye: false);

        Assert.Empty(MdnsPacket.ReadQuestionNames(response));
        Assert.Empty(MdnsPacket.ReadQuestionNames([1, 2, 3]));
        Assert.Empty(MdnsPacket.ReadQuestionNames([0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0xC0, 12]));
    }

    [Fact]
    public void Service_matches_its_own_names_only()
    {
        Assert.True(Service.Matches("_augora._tcp.local"));
        Assert.True(Service.Matches("AUGORA ON NAS._augora._tcp.local"));
        Assert.True(Service.Matches("augora-nas.local"));
        Assert.True(Service.Matches("_services._dns-sd._udp.local"));
        Assert.False(Service.Matches("_http._tcp.local"));
    }

    [Fact]
    public void Response_holds_the_service_records()
    {
        var response = MdnsPacket.BuildResponse(Service, IPAddress.Parse("192.168.1.10"), goodbye: false);

        Assert.Equal(0x8400, BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(2)));
        Assert.Equal(5, BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(6)));
        Assert.Contains("Augora on nas"u8.ToArray(), Windows(response, "Augora on nas".Length));
        Assert.Contains("version=0.1.0"u8.ToArray(), Windows(response, "version=0.1.0".Length));
        Assert.Contains(new byte[] { 192, 168, 1, 10 }, Windows(response, 4));
        Assert.Contains(new byte[] { 0x12, 0x77 }, Windows(response, 2)); // port 4727 in the SRV record
    }

    [Fact]
    public void Goodbye_expires_the_records()
    {
        var response = MdnsPacket.BuildResponse(Service, IPAddress.Parse("192.168.1.10"), goodbye: true);
        var alive = MdnsPacket.BuildResponse(Service, IPAddress.Parse("192.168.1.10"), goodbye: false);

        Assert.Equal(alive.Length, response.Length);
        Assert.DoesNotContain(new byte[] { 0, 0, 0x11, 0x94 }, Windows(response, 4)); // 4500 seconds
        Assert.Contains(new byte[] { 0, 0, 0x11, 0x94 }, Windows(alive, 4));
    }

    private static IEnumerable<byte[]> Windows(byte[] data, int length) =>
        Enumerable.Range(0, data.Length - length + 1).Select(i => data[i..(i + length)]);
}
