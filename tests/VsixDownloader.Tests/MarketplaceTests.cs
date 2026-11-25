using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace VsixDownloader.Tests;

public sealed class MarketplaceTests
{
    [Fact]
    public async Task RequestsExactIdAndReturnsVersionsWithTheirOwnPlatforms()
    {
        using var http = new HttpClient(new TestHandler(async (request, token) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("marketplace.visualstudio.com", request.RequestUri!.Host);
            Assert.Equal("/_apis/public/gallery/extensionquery", request.RequestUri.AbsolutePath);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var criteria = body.RootElement.GetProperty("filters")[0].GetProperty("criteria");
            Assert.Equal(7, criteria[0].GetProperty("filterType").GetInt32());
            Assert.Equal("MS.Example", criteria[0].GetProperty("value").GetString());
            Assert.Equal("Microsoft.VisualStudio.Code", criteria[1].GetProperty("value").GetString());
            Assert.Equal(179, body.RootElement.GetProperty("flags").GetInt32());
            return JsonResponse("""
                { "results": [ { "extensions": [
                  { "publisher": { "publisherName": "other" }, "extensionName": "example" },
                  { "publisher": { "publisherName": "MS" }, "extensionName": "Example", "displayName": "Example extension",
                    "versions": [
                      { "version": "1.2.0", "fallbackAssetUri": "https://ms.gallery.vsassets.io/asset" },
                      { "version": "1.10.0", "targetPlatform": "linux-x64", "fallbackAssetUri": "https://ms.gallery.vsassets.io/asset",
                        "properties": [{ "key": "Microsoft.VisualStudio.Code.PreRelease", "value": "true" }] },
                      { "version": "1.10.0", "targetPlatform": "win32-x64", "fallbackAssetUri": "https://ms.gallery.vsassets.io/asset",
                        "properties": [{ "key": "Microsoft.VisualStudio.Code.PreRelease", "value": "true" }] },
                      { "version": "1.2.0", "fallbackAssetUri": "https://ms.gallery.vsassets.io/asset" },
                      { "version": "1.0.0", "fallbackAssetUri": "https://evil.gallery.vsassets.io.example/asset" }
                    ] }
                ] } ] }
                """);
        }));
        var catalog = await new MarketplaceClient(http, new Settings()).FindAsync("MS.Example", CancellationToken.None);
        Assert.NotNull(catalog);
        Assert.Equal("Example extension", catalog.DisplayName);
        Assert.Collection(catalog.Builds,
            x =>
            {
                Assert.Equal("ms.example-1.10.0-linux-x64.vsix", x.FileName);
                Assert.Equal("?redirect=true&targetPlatform=linux-x64", x.DownloadUri.Query);
                Assert.EndsWith(MarketplaceClient.VsixAssetType, x.DownloadUri.AbsolutePath);
                Assert.True(x.IsPreRelease);
            },
            x => Assert.Equal("win32-x64", x.TargetPlatform),
            x =>
            {
                Assert.Equal("universal", x.PlatformLabel);
                Assert.Equal("?redirect=true", x.DownloadUri.Query);
                Assert.False(x.IsPreRelease);
            });
    }

    [Fact]
    public async Task DoesNotUseOtherExtensionWhenRequestedIdIsAbsent()
    {
        using var http = new HttpClient(new TestHandler((_, _) => Task.FromResult(JsonResponse("""
            {"results":[{"extensions":[{"publisher":{"publisherName":"ms"},"extensionName":"other"}]}]}
            """))));
        Assert.Null(await new MarketplaceClient(http, new Settings()).FindAsync("ms.example", CancellationToken.None));
    }

    [Theory]
    [InlineData("http://proxy.example:8080", "http", 8080)]
    [InlineData("https://proxy.example", "https", 443)]
    [InlineData("socks5://127.0.0.1:1080", "socks5", 1080)]
    public void AcceptsExplicitUnauthenticatedProxy(string value, string scheme, int port)
    {
        var proxy = (new Settings { ProxyUrl = value }).GetProxy();
        Assert.NotNull(proxy);
        Assert.Equal(scheme, proxy.Scheme);
        Assert.Equal(port, proxy.Port);
    }

    [Theory]
    [InlineData("proxy.example:8080")]
    [InlineData("http://user:password@proxy.example:8080")]
    [InlineData("https://proxy.example/a")]
    [InlineData("https://proxy.example?auth=true")]
    [InlineData("ftp://proxy.example:8080")]
    public void RejectsMalformedOrAuthenticatedProxy(string value) =>
        Assert.Throws<ArgumentException>(() => (new Settings { ProxyUrl = value }).Validate());

    [Fact]
    public void ProxyIsOptional() => Assert.Null(new Settings().GetProxy());

    [Fact]
    public async Task HttpClientUsesConfiguredProxyForHttpsWithoutSendingCredentials()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var http = (new Settings { ProxyUrl = $"http://127.0.0.1:{port}" }).CreateClient();
        var pending = http.GetAsync("https://marketplace.visualstudio.com/", timeout.Token);

        using var connection = await listener.AcceptTcpClientAsync(timeout.Token);
        await using var stream = connection.GetStream();
        using var reader = new StreamReader(stream, leaveOpen: true);
        Assert.Equal("CONNECT marketplace.visualstudio.com:443 HTTP/1.1", await reader.ReadLineAsync(timeout.Token));
        while (await reader.ReadLineAsync(timeout.Token) is { Length: > 0 } line)
            Assert.False(line.StartsWith("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase));

        await stream.WriteAsync("HTTP/1.1 407 Proxy Authentication Required\r\nConnection: close\r\nContent-Length: 0\r\n\r\n"u8.ToArray(), timeout.Token);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => pending);
        Assert.Equal(HttpRequestError.ProxyTunnelError, error.HttpRequestError);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };
}

internal sealed class TestHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> reply) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await reply(request, cancellationToken);
        response.RequestMessage = request;
        return response;
    }
}
