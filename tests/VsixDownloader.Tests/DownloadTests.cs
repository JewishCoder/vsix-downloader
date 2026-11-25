using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;

namespace VsixDownloader.Tests;

public sealed class DownloadTests : IDisposable
{
    private const int Prefix = 120;
    private const string ValidTag = "\"asset-1\"";
    private static readonly PackageBuild Build = new("ms.example", "1.2.3", "linux-x64", false,
        new Uri("https://ms.gallery.vsassets.io/asset?redirect=true&targetPlatform=linux-x64"));
    private readonly string directory = Path.Combine(Path.GetTempPath(), "vsix-downloader-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ResumesAcrossInvocationsAfterReadTimeoutAndFollowsHttpsRedirect()
    {
        var bytes = CreateVsix();
        var cdn = new Uri("https://ms.gallerycdn.vsassets.io/package.vsix");
        var calls = 0;
        using var http = WithReplies(request =>
        {
            var number = calls++;
            if (number is 0 or 2)
                return new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = cdn } };
            Assert.Equal(cdn, request.RequestUri);
            if (number == 1)
            {
                Assert.Null(request.Headers.Range);
                return Response(HttpStatusCode.OK, new PrefixThenStream(bytes[..Prefix], waitForCancel: true), bytes.Length);
            }

            Assert.Equal(3, number);
            AssertRange(request);
            return Partial(bytes);
        });

        var settings = new Settings { MaxAttempts = 1, IdleTimeoutSeconds = 1 };
        var error = await Assert.ThrowsAsync<IOException>(() => Download(http, settings));
        Assert.Contains("не передавал данные", error.Message);
        Assert.False(File.Exists(Path.Combine(directory, Build.FileName)));
        Assert.Equal(Prefix, new FileInfo(PartialPath()).Length);

        var result = await Download(http, settings);
        Assert.Equal(bytes, File.ReadAllBytes(result.Path));
        Assert.Equal(4, calls);
        Assert.False(File.Exists(PartialPath()));
        Assert.False(File.Exists(PartialPath() + ".json"));
    }

    [Fact]
    public async Task AutomaticallyResumesAfterTransportBreakWithinOneDownload()
    {
        var bytes = CreateVsix();
        var calls = 0;
        var statuses = new List<string>();
        using var http = WithReplies(request =>
        {
            if (calls++ == 0)
                return Response(HttpStatusCode.OK, new PrefixThenStream(bytes[..Prefix]), bytes.Length);
            AssertRange(request);
            return Partial(bytes);
        });
        var result = await Download(http, new Settings { MaxAttempts = 2 }, x => statuses.Add(x.Status));
        Assert.Equal(bytes, File.ReadAllBytes(result.Path));
        Assert.Contains("Докачка", statuses);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ReplacesPartialFileWhenRangeIsIgnored()
    {
        var bytes = CreateVsix('b');
        var calls = 0;
        using var http = WithReplies(request =>
        {
            if (calls++ == 0)
                return Response(HttpStatusCode.OK, new PrefixThenStream(CreateVsix('a')[..Prefix]), bytes.Length);
            AssertRange(request);
            return Complete(bytes, "\"asset-2\"");
        });
        var settings = new Settings { MaxAttempts = 1 };
        await Assert.ThrowsAsync<IOException>(() => Download(http, settings));
        var result = await Download(http, settings);
        Assert.Equal(bytes, File.ReadAllBytes(result.Path));
    }

    [Theory]
    [InlineData(416)]
    [InlineData(206)]
    public async Task RequestsCleanDownloadAfterInvalidRangeResponse(int status)
    {
        var bytes = CreateVsix();
        var calls = 0;
        using var http = WithReplies(request =>
        {
            switch (calls++)
            {
                case 0:
                    return Response(HttpStatusCode.OK, new PrefixThenStream(bytes[..Prefix]), bytes.Length);
                case 1:
                    AssertRange(request);
                    return status == 416 ? new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable)
                        : Partial(bytes, Prefix + 1);
                default:
                    Assert.Null(request.Headers.Range);
                    Assert.Null(request.Headers.IfRange);
                    return Complete(bytes);
            }
        });
        await Assert.ThrowsAsync<IOException>(() => Download(http, new Settings { MaxAttempts = 1 }));
        var result = await Download(http, new Settings { MaxAttempts = 2 });
        Assert.Equal(bytes, File.ReadAllBytes(result.Path));
        Assert.Equal(3, calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("W/\"asset-1\"")]
    public async Task DoesNotAppendWithoutAStrongValidator(string? tag)
    {
        var bytes = CreateVsix();
        var calls = 0;
        using var http = WithReplies(request =>
        {
            Assert.Null(request.Headers.Range);
            return calls++ == 0
                ? Response(HttpStatusCode.OK, new PrefixThenStream(bytes[..Prefix]), bytes.Length, tag)
                : Complete(bytes);
        });
        var settings = new Settings { MaxAttempts = 1 };
        await Assert.ThrowsAsync<IOException>(() => Download(http, settings));
        Assert.Equal(bytes, File.ReadAllBytes((await Download(http, settings)).Path));
    }

    [Fact]
    public async Task CanResumeUsingStrongLastModifiedDateWhenNoETagWasSent()
    {
        var bytes = CreateVsix();
        var lastModified = new DateTimeOffset(2024, 10, 30, 10, 0, 0, TimeSpan.Zero);
        var calls = 0;
        using var http = WithReplies(request =>
        {
            if (calls++ == 0)
            {
                var response = Response(HttpStatusCode.OK, new PrefixThenStream(bytes[..Prefix]), bytes.Length, tag: null);
                response.Headers.Date = lastModified.AddMinutes(2);
                response.Content.Headers.LastModified = lastModified;
                return response;
            }

            Assert.Equal($"bytes={Prefix}-", request.Headers.Range?.ToString());
            Assert.Equal(lastModified, request.Headers.IfRange?.Date);
            return Partial(bytes);
        });
        var settings = new Settings { MaxAttempts = 1 };
        await Assert.ThrowsAsync<IOException>(() => Download(http, settings));
        Assert.Equal(bytes, File.ReadAllBytes((await Download(http, settings)).Path));
    }

    [Fact]
    public async Task RejectsHttpDowngradeOnRedirect()
    {
        using var http = WithReplies(_ => new HttpResponseMessage(HttpStatusCode.Found)
        {
            Headers = { Location = new Uri("http://ms.gallerycdn.vsassets.io/package.vsix") }
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => Download(http, new Settings()));
        Assert.False(File.Exists(Path.Combine(directory, Build.FileName)));
    }

    [Fact]
    public async Task DownloadsSlowBodyEvenAfterHeaderTimeoutWindowHasElapsed()
    {
        var bytes = CreateVsix();
        using var http = WithReplies(_ => Response(HttpStatusCode.OK, new SlowStream(bytes), bytes.Length));
        var result = await Download(http, new Settings { MaxAttempts = 1, ResponseTimeoutSeconds = 1, IdleTimeoutSeconds = 3 });
        Assert.Equal(bytes, File.ReadAllBytes(result.Path));
    }

    [Fact]
    public async Task FailsWithoutPublishingAValidZipForADifferentPlatform()
    {
        using var http = WithReplies(_ => Complete(CreateVsix(platform: "win32-x64")));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Download(http, new Settings()));
        Assert.Contains("платформа", error.Message);
        Assert.False(File.Exists(Path.Combine(directory, Build.FileName)));
        Assert.False(File.Exists(PartialPath()));
    }

    [Fact]
    public async Task KeepsExistingValidFileWithoutMakingRequest()
    {
        var bytes = CreateVsix();
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, Build.FileName), bytes);
        using var http = WithReplies(_ => throw new InvalidOperationException("No request expected"));
        var result = await Download(http, new Settings());
        Assert.True(result.AlreadyExists);
        Assert.Equal(bytes, File.ReadAllBytes(result.Path));
    }

    [Fact]
    public async Task RetriesRateLimitUsingRetryAfter()
    {
        var bytes = CreateVsix();
        var calls = 0;
        using var http = WithReplies(_ => calls++ == 0
            ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero) } }
            : Complete(bytes));
        Assert.Equal(bytes, File.ReadAllBytes((await Download(http, new Settings { MaxAttempts = 2 })).Path));
        Assert.Equal(2, calls);
    }

    private Task<DownloadResult> Download(HttpClient http, Settings settings, Action<DownloadProgress>? progress = null) =>
        new DownloadService(http, settings).DownloadAsync(Build, directory, progress, CancellationToken.None);

    private string PartialPath() => Path.Combine(directory, ".vsix-downloader", Build.FileName + ".part");

    private static HttpClient WithReplies(Func<HttpRequestMessage, HttpResponseMessage> reply) =>
        new(new TestHandler((request, _) => Task.FromResult(reply(request))));

    private static void AssertRange(HttpRequestMessage request)
    {
        Assert.Equal($"bytes={Prefix}-", request.Headers.Range?.ToString());
        Assert.Equal(ValidTag, request.Headers.IfRange?.ToString());
        Assert.Contains(request.Headers.AcceptEncoding, x => x.Value == "identity");
    }

    private static HttpResponseMessage Partial(byte[] bytes, int start = Prefix)
    {
        var response = Response(HttpStatusCode.PartialContent, new MemoryStream(bytes[start..]), bytes.Length - start);
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, bytes.Length - 1, bytes.Length);
        return response;
    }

    private static HttpResponseMessage Complete(byte[] bytes, string tag = ValidTag) =>
        Response(HttpStatusCode.OK, new MemoryStream(bytes), bytes.Length, tag);

    private static HttpResponseMessage Response(HttpStatusCode status, Stream stream, long length, string? tag = ValidTag)
    {
        var response = new HttpResponseMessage(status) { Content = new StreamContent(stream) };
        response.Content.Headers.ContentLength = length;
        if (tag is not null)
            response.Headers.ETag = EntityTagHeaderValue.Parse(tag);
        return response;
    }

    private static byte[] CreateVsix(char payload = 'a', string platform = "linux-x64")
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("extension/payload.txt", CompressionLevel.NoCompression).Open()))
                writer.Write(new string(payload, 256));
            using (var writer = new StreamWriter(zip.CreateEntry("extension/package.json").Open()))
                writer.Write("{}");
            using (var writer = new StreamWriter(zip.CreateEntry("extension.vsixmanifest").Open()))
                writer.Write($"""
                    <PackageManifest xmlns="http://schemas.microsoft.com/developer/vsx-schema/2011">
                        <Metadata><Identity Publisher="ms" Id="example" Version="1.2.3" TargetPlatform="{platform}"/></Metadata>
                    </PackageManifest>
                    """);
        }
        return stream.ToArray();
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    private sealed class PrefixThenStream(byte[] prefix, bool waitForCancel = false) : MemoryStream(prefix)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = await base.ReadAsync(buffer, cancellationToken);
            if (count > 0)
                return count;
            if (waitForCancel)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new IOException("Simulated connection interruption");
        }
    }

    private sealed class SlowStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position < Length)
                await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken);
            return await base.ReadAsync(buffer[..Math.Min(buffer.Length, 100)], cancellationToken);
        }
    }
}
