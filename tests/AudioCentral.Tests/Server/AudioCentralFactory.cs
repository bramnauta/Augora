using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AudioCentral.Tests.Server;

/// <summary>
/// The real server with its own empty database, and without network-facing providers.
/// </summary>
public sealed class AudioCentralFactory : WebApplicationFactory<Program>
{
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "audiocentral-tests", Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // UseSetting rather than ConfigureAppConfiguration: Program reads these before the app is built.
        builder.UseSetting("AudioCentral:DataDirectory", _dataDirectory);
        builder.UseSetting("Players:GoogleCast:Enabled", "false");
        builder.UseSetting("Providers:TuneIn:Enabled", "false");
        builder.UseEnvironment("Testing");
    }

    public HttpClient CreateCookieClient() => CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
