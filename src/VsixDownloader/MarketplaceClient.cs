using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VsixDownloader;

internal sealed record PackageBuild(string ExtensionId, string Version, string? TargetPlatform, bool IsPreRelease, Uri DownloadUri)
{
    public string PlatformLabel => TargetPlatform ?? "universal";
    public string FileName => $"{ExtensionId}-{Version}-{PlatformLabel}.vsix";
}

internal sealed record ExtensionCatalog(string DisplayName, IReadOnlyList<PackageBuild> Builds);

internal sealed partial class MarketplaceClient(HttpClient http, Settings settings)
{
    internal const string VsixAssetType = "Microsoft.VisualStudio.Services.VSIXPackage";
    private const string ApiVersion = "7.2-preview.1";
    private const string ApiUri = "https://marketplace.visualstudio.com/_apis/public/gallery/extensionquery";
    private const string PreReleaseProperty = "Microsoft.VisualStudio.Code.PreRelease";
    private const int IncludeVersions = 1, IncludeFiles = 2, IncludeVersionProperties = 16,
        ExcludeNonValidated = 32, IncludeAssetUri = 128;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9-]{0,79}\.[A-Za-z0-9][A-Za-z0-9-]{0,79}\z")]
    private static partial Regex ExtensionPattern();

    [GeneratedRegex(@"\A[0-9]+\.[0-9]+\.[0-9]+\z")]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"\A[a-z0-9](?:[a-z0-9-]{0,39})\z")]
    private static partial Regex PlatformPattern();

    public static bool IsValidId(string value) => ExtensionPattern().IsMatch(value);

    public async Task<ExtensionCatalog?> FindAsync(string extensionId, CancellationToken cancellationToken)
    {
        if (!IsValidId(extensionId))
            throw new ArgumentException("Ожидается ID в формате publisher.extension.", nameof(extensionId));

        var body = new
        {
            filters = new[]
            {
                new
                {
                    criteria = new[]
                    {
                        new { filterType = 7, value = extensionId },
                        new { filterType = 8, value = "Microsoft.VisualStudio.Code" }
                    },
                    pageNumber = 1, pageSize = 10, sortBy = 0, sortOrder = 0
                }
            },
            assetTypes = new[] { VsixAssetType },
            flags = IncludeVersions | IncludeFiles | IncludeVersionProperties | ExcludeNonValidated | IncludeAssetUri
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiUri}?api-version={ApiVersion}")
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Accept.ParseAdd($"application/json;api-version={ApiVersion}");
        request.Headers.UserAgent.ParseAdd("vsix-downloader/1.0");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.ResponseTimeoutSeconds));
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            NetworkErrors.EnsureSuccess(response, "Marketplace");
            var payload = await response.Content.ReadFromJsonAsync<GalleryResponse>(JsonOptions, timeout.Token);
            if (payload?.Results is null)
                throw new InvalidDataException("В ответе Marketplace нет списка results.");
            return ToCatalog(extensionId, payload);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Marketplace не ответил вовремя.", ex);
        }
    }

    private static ExtensionCatalog? ToCatalog(string requestedId, GalleryResponse payload)
    {
        var entry = payload.Results!.SelectMany(x => x.Extensions ?? [])
            .FirstOrDefault(x => string.Equals($"{x.Publisher?.PublisherName}.{x.ExtensionName}", requestedId,
                StringComparison.OrdinalIgnoreCase));
        if (entry is null)
            return null;

        var id = $"{entry.Publisher!.PublisherName}.{entry.ExtensionName}".ToLowerInvariant();
        var builds = new List<PackageBuild>();
        foreach (var release in entry.Versions ?? [])
        {
            if (release.Version is not { Length: <= 64 } version || !VersionPattern().IsMatch(version) ||
                !Version.TryParse(version, out _))
                continue;
            var platform = string.IsNullOrWhiteSpace(release.TargetPlatform) ? null : release.TargetPlatform;
            if (platform is not null && !PlatformPattern().IsMatch(platform))
                continue;
            if (!Uri.TryCreate(release.FallbackAssetUri, UriKind.Absolute, out var assetBase) ||
                !IsGalleryAssetUri(assetBase))
                continue;

            var uri = new UriBuilder(assetBase)
            {
                Path = $"{assetBase.AbsolutePath.TrimEnd('/')}/{VsixAssetType}",
                Query = platform is null ? "redirect=true" : $"redirect=true&targetPlatform={Uri.EscapeDataString(platform)}"
            }.Uri;
            var isPreRelease = release.Properties?.Any(p => p.Key == PreReleaseProperty &&
                string.Equals(p.Value, "true", StringComparison.OrdinalIgnoreCase)) ?? false;
            builds.Add(new PackageBuild(id, version, platform, isPreRelease, uri));
        }

        return new ExtensionCatalog(entry.DisplayName ?? id, builds
            .DistinctBy(x => (x.Version, x.TargetPlatform, x.IsPreRelease))
            .OrderByDescending(x => Version.Parse(x.Version))
            .ThenBy(x => x.IsPreRelease)
            .ThenBy(x => x.PlatformLabel, StringComparer.Ordinal)
            .ToArray());
    }

    internal static bool IsGalleryAssetUri(Uri uri) => IsHttpsUri(uri) &&
        (uri.IdnHost == "marketplace.visualstudio.com" ||
         uri.IdnHost.EndsWith(".gallery.vsassets.io", StringComparison.Ordinal) ||
         uri.IdnHost.EndsWith(".gallerycdn.vsassets.io", StringComparison.Ordinal));

    internal static bool IsHttpsUri(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps &&
        uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0;

    private sealed record GalleryResponse(GalleryResult[]? Results);
    private sealed record GalleryResult(GalleryExtension[]? Extensions);
    private sealed record GalleryExtension(GalleryPublisher? Publisher, string? ExtensionName, string? DisplayName, GalleryVersion[]? Versions);
    private sealed record GalleryPublisher(string? PublisherName);
    private sealed record GalleryVersion(string? Version, string? TargetPlatform, string? FallbackAssetUri, GalleryProperty[]? Properties);
    private sealed record GalleryProperty(string? Key, string? Value);
}
