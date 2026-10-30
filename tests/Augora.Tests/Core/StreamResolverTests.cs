using System.Net;
using System.Text;
using Augora.Core.Streaming;
using Microsoft.Extensions.Logging.Abstractions;

namespace Augora.Tests.Core;

public sealed class StreamResolverTests
{
    private sealed class FakeHandler(Dictionary<string, (string ContentType, string Body)> responses) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!responses.TryGetValue(request.RequestUri!.ToString(), out var response))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(response.Body, Encoding.UTF8, response.ContentType),
            });
        }
    }

    private static StreamResolver Resolver(Dictionary<string, (string, string)> responses) =>
        new(new HttpClient(new FakeHandler(responses)), NullLogger<StreamResolver>.Instance);

    [Fact]
    public async Task Follows_nested_playlists_to_the_audio_stream()
    {
        var resolver = Resolver(new()
        {
            ["http://radio.example/listen.pls"] = ("audio/x-scpls", "[playlist]\nFile1=http://radio.example/list.m3u\n"),
            ["http://radio.example/list.m3u"] = ("audio/x-mpegurl", "#EXTM3U\nhttp://edge.example/live\n"),
            ["http://edge.example/live"] = ("audio/aacp", "binary audio"),
        });

        var stream = await resolver.ResolveAsync(new HttpStreamSource(new Uri("http://radio.example/listen.pls"), IsLive: true), TestContext.Current.CancellationToken);

        Assert.Equal(new Uri("http://edge.example/live"), stream.Url);
        Assert.Equal("audio/aac", stream.ContentType);
        Assert.False(stream.Direct);
    }

    [Fact]
    public async Task Hands_hls_to_the_player_directly()
    {
        var resolver = Resolver(new()
        {
            ["https://hls.example/master.m3u8"] = ("application/vnd.apple.mpegurl", "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=128000\nchunks.m3u8\n"),
        });

        var stream = await resolver.ResolveAsync(new HttpStreamSource(new Uri("https://hls.example/master.m3u8"), IsLive: true), TestContext.Current.CancellationToken);

        Assert.True(stream.Direct);
    }
}
