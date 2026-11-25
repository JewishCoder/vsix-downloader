using System.Net;

namespace VsixDownloader;

internal static class NetworkErrors
{
    public static void EnsureSuccess(HttpResponseMessage response, string service)
    {
        if (response.IsSuccessStatusCode)
            return;

        var message = response.StatusCode switch
        {
            HttpStatusCode.ProxyAuthenticationRequired => "Прокси требует авторизацию (HTTP 407), а утилита использует прокси без авторизации.",
            HttpStatusCode.Forbidden => $"{service}: доступ запрещён (HTTP 403). Проверьте настройки прокси.",
            HttpStatusCode.NotFound => $"{service}: ресурс не найден (HTTP 404).",
            _ => $"{service}: HTTP {(int)response.StatusCode}."
        };

        if (response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or
            HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or
            HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
        {
            var retryAfter = response.Headers.RetryAfter;
            throw new TransferInterruptedException(message, retryAfter?.Delta ?? (retryAfter?.Date - DateTimeOffset.UtcNow));
        }

        throw new HttpRequestException(message, null, response.StatusCode);
    }

    public static string Describe(Exception error) => error switch
    {
        HttpRequestException { HttpRequestError: HttpRequestError.ProxyTunnelError } =>
            "Прокси не открыл HTTPS-туннель. Проверьте URL, доступ к доменам Marketplace и отсутствие требований к авторизации.",
        HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } =>
            "Не удалось установить TLS-соединение. Проверьте доверенные сертификаты и настройки прокси.",
        HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError } =>
            "Не удалось разрешить имя прокси или сервера Microsoft Marketplace.",
        _ => error.Message
    };

    public static bool IsTransient(Exception error) => error is TransferInterruptedException or
        HttpRequestException { StatusCode: null, HttpRequestError: HttpRequestError.ConnectionError or
            HttpRequestError.NameResolutionError or HttpRequestError.ResponseEnded or
            HttpRequestError.HttpProtocolError or HttpRequestError.Unknown };
}

internal sealed class TransferInterruptedException(string message, TimeSpan? retryAfter = null, Exception? inner = null)
    : IOException(message, inner)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
