using System.Net.Http.Headers;

namespace Augora.Client;

/// <summary>Signs the API calls of an app with its access token, refreshing it when needed.</summary>
public sealed class BearerTokenHandler(ServerClient server) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (await server.GetAccessTokenAsync() is { } token)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
