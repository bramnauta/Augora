using System.Text.RegularExpressions;
using Microsoft.Extensions.FileProviders;

namespace Augora.Server;

/// <summary>
/// Home Assistant ingress serves the app under <c>/api/hassio_ingress/{token}</c>, strips that prefix and passes it in
/// <c>X-Ingress-Path</c>. The prefix becomes the request's path base, and the web app gets it as its base href.
/// </summary>
public static partial class Ingress
{
    public const string Header = "X-Ingress-Path";

    [GeneratedRegex("^/api/hassio_ingress/[A-Za-z0-9_-]+$")]
    private static partial Regex PrefixPattern();

    [GeneratedRegex("""<base href="[^"]*"\s*/?>""")]
    private static partial Regex BaseTag();

    /// <summary>The ingress prefix of the request, or <c>null</c> when it did not come through ingress.</summary>
    public static string? PathBase(HttpRequest request) =>
        request.Headers[Header].ToString() is { Length: > 0 } value && PrefixPattern().IsMatch(value) ? value : null;

    /// <summary>Points the base href of the web app at <paramref name="pathBase"/>.</summary>
    public static string RewriteBase(string html, string pathBase) =>
        BaseTag().Replace(html, $"""<base href="{pathBase}/" />""", 1);

    /// <summary>
    /// Sets the path base of requests that came through ingress; run it before authentication. The root document is
    /// answered here, because static assets would serve index.html with the wrong base href.
    /// </summary>
    public static IApplicationBuilder UseIngressPathBase(this IApplicationBuilder app) =>
        app.Use((context, next) =>
        {
            if (PathBase(context.Request) is not { } pathBase)
            {
                return next(context);
            }

            context.Request.PathBase = pathBase;
            return context.Request.Path.Value is "/" or "/index.html"
                ? ServeIndexAsync(context, context.RequestServices.GetRequiredService<IWebHostEnvironment>().WebRootFileProvider)
                : next(context);
        });

    /// <summary>
    /// Serves the web app's index.html (with the right base href) for the root and for client-side routes.
    /// It must never be cached: it decides which version of the app the browser loads after an update.
    /// </summary>
    public static async Task ServeIndexAsync(HttpContext context, IFileProvider webRoot)
    {
        var file = webRoot.GetFileInfo("index.html");
        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method) || !file.Exists)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        string html;
        await using (var stream = file.CreateReadStream())
        using (var reader = new StreamReader(stream))
        {
            html = await reader.ReadToEndAsync(context.RequestAborted);
        }

        if (PathBase(context.Request) is { } pathBase)
        {
            html = RewriteBase(html, pathBase);
        }

        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers.CacheControl = "no-cache, no-store";
        if (!HttpMethods.IsHead(context.Request.Method))
        {
            await context.Response.WriteAsync(html, context.RequestAborted);
        }
    }
}
