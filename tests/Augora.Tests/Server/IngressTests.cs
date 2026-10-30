using Augora.Server;
using Microsoft.AspNetCore.Http;

namespace Augora.Tests.Server;

public sealed class IngressTests
{
    private static HttpRequest RequestWith(string? header)
    {
        var request = new DefaultHttpContext().Request;
        if (header is not null)
        {
            request.Headers[Ingress.Header] = header;
        }

        return request;
    }

    [Fact]
    public void Ingress_prefix_is_read_from_the_header()
    {
        Assert.Equal("/api/hassio_ingress/abc-DEF_123", Ingress.PathBase(RequestWith("/api/hassio_ingress/abc-DEF_123")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("/somewhere/else")]
    [InlineData("/api/hassio_ingress/")]
    [InlineData("/api/hassio_ingress/token/extra")]
    [InlineData("/api/hassio_ingress/to\"><script>")]
    public void Other_values_are_not_ingress(string? header)
    {
        Assert.Null(Ingress.PathBase(RequestWith(header)));
    }

    [Fact]
    public void Base_href_points_at_the_prefix()
    {
        const string html = """<head><base href="/" /><link href="css/app.css" /></head>""";

        Assert.Equal(
            """<head><base href="/api/hassio_ingress/tok/" /><link href="css/app.css" /></head>""",
            Ingress.RewriteBase(html, "/api/hassio_ingress/tok"));
    }
}
