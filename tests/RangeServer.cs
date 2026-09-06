using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Snail.Toolkit.Mvc.RangeRequests.Tests;

/// <summary>
/// A real Kestrel bound to a loopback port, serving one entity.
/// </summary>
/// <remarks>
/// Kestrel rather than <c>TestServer</c>, which is what <c>WebApplicationFactory</c> hosts. The checks these
/// tests exist for — a response that writes fewer bytes than its Content-Length promised, and an abort in
/// the middle of a body — live in Kestrel's own protocol handling and never run under TestServer.
/// </remarks>
internal sealed class RangeServer : IAsyncDisposable
{
    private readonly WebApplication _application;

    private RangeServer(WebApplication application, HttpClient client)
    {
        _application = application;
        Client = client;
    }

    /// <summary>
    /// Talks to the running server.
    /// </summary>
    internal HttpClient Client { get; }

    internal static Task<RangeServer> StartAsync(RangeContent content) => StartAsync(_ => content, logs: null);

    internal static async Task<RangeServer> StartAsync(Func<HttpContext, RangeContent> serving, RecordingLogs? logs)
    {
        var builder = WebApplication.CreateSlimBuilder();

        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        if (logs is not null)
        {
            builder.Logging.AddProvider(logs);
            builder.Logging.SetMinimumLevel(LogLevel.Trace);
        }

        var application = builder.Build();

        application.MapMethods("/entity", ["GET", "HEAD"], (HttpContext http) => new RangeFileHttpResult(serving(http)));

        await application.StartAsync();

        var client = new HttpClient { BaseAddress = new Uri(AddressOf(application)) };

        return new RangeServer(application, client);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();

        await _application.StopAsync();
        await _application.DisposeAsync();
    }

    private static string AddressOf(WebApplication application)
        => application.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()!
            .Addresses
            .First();
}
