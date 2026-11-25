using System.Net;
using System.Text.Json;

namespace VsixDownloader;

internal sealed record Settings
{
    public string? ProxyUrl { get; init; }
    public string? OutputDirectory { get; init; }
    public int ConnectTimeoutSeconds { get; init; } = 30;
    public int ResponseTimeoutSeconds { get; init; } = 120;
    public int IdleTimeoutSeconds { get; init; } = 180;
    public int MaxAttempts { get; init; } = 5;

    public void Validate()
    {
        _ = GetProxy();
        if (ConnectTimeoutSeconds is < 1 or > 3600 || ResponseTimeoutSeconds is < 1 or > 3600 ||
            IdleTimeoutSeconds is < 1 or > 3600 || MaxAttempts is < 1 or > 10)
            throw new ArgumentException("Таймауты должны быть от 1 до 3600 с, число попыток — от 1 до 10.");
    }

    public Uri? GetProxy()
    {
        if (string.IsNullOrWhiteSpace(ProxyUrl))
            return null;

        if (!Uri.TryCreate(ProxyUrl.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https" or "socks4" or "socks4a" or "socks5") ||
            uri.Host.Length == 0 || uri.Port is < 1 or > 65535 || uri.OriginalString.Contains('@') ||
            uri.UserInfo.Length != 0 || (uri.AbsolutePath is not ("" or "/")) ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("Укажите URL прокси с портом при необходимости, без логина, пароля и пути.");

        return uri;
    }

    public HttpClient CreateClient()
    {
        Validate();
        var proxy = GetProxy();
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = proxy is not null,
            Proxy = proxy is null ? null : new WebProxy(proxy),
            ConnectTimeout = TimeSpan.FromSeconds(ConnectTimeoutSeconds)
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }
}

internal static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string PathOnDisk { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify),
        "vsix-downloader", "settings.json");

    public static Settings Load()
    {
        if (!File.Exists(PathOnDisk))
            return new Settings();

        var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(PathOnDisk), JsonOptions)
            ?? throw new InvalidDataException("Файл настроек пуст.");
        settings.Validate();
        return settings;
    }

    public static void Save(Settings settings)
    {
        settings.Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(PathOnDisk)!);
        var tempPath = PathOnDisk + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tempPath, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(tempPath, PathOnDisk, overwrite: true);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }
}
