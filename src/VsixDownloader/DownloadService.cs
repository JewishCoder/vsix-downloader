using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VsixDownloader;

internal sealed record DownloadProgress(long Bytes, long? TotalBytes, string Status);
internal sealed record DownloadResult(string Path, bool AlreadyExists);

internal sealed class DownloadService(HttpClient http, Settings settings)
{
    private const int BufferSize = 128 * 1024;
    private const int MaxRedirects = 8;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<DownloadResult> DownloadAsync(PackageBuild build, string outputDirectory,
        Action<DownloadProgress>? report, CancellationToken cancellationToken)
    {
        if (!MarketplaceClient.IsGalleryAssetUri(build.DownloadUri))
            throw new InvalidDataException("Адрес пакета не принадлежит Visual Studio Marketplace.");

        Directory.CreateDirectory(outputDirectory);
        var stateDirectory = Path.Combine(outputDirectory, ".vsix-downloader");
        Directory.CreateDirectory(stateDirectory);
        var finalPath = Path.Combine(outputDirectory, build.FileName);
        var partialPath = Path.Combine(stateDirectory, build.FileName + ".part");
        var metadataPath = partialPath + ".json";
        var lockPath = Path.Combine(stateDirectory, build.FileName + ".lock");
        using var destinationLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        if (File.Exists(finalPath))
        {
            try
            {
                VsixValidator.Validate(finalPath, build);
            }
            catch (InvalidDataException ex)
            {
                throw new InvalidDataException($"Файл уже существует, но не соответствует выбранному пакету: {finalPath}. " +
                    "Переместите его или выберите другую папку.", ex);
            }
            ResetPartial(partialPath, metadataPath);
            return new DownloadResult(finalPath, AlreadyExists: true);
        }

        var sourceKey = ResourceKey(build.DownloadUri);
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await TransferOnceAsync(build.DownloadUri, sourceKey, partialPath, metadataPath, report, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    VsixValidator.Validate(partialPath, build);
                }
                catch (InvalidDataException)
                {
                    ResetPartial(partialPath, metadataPath);
                    throw;
                }

                File.Move(partialPath, finalPath, overwrite: false);
                File.Delete(metadataPath);
                return new DownloadResult(finalPath, AlreadyExists: false);
            }
            catch (Exception ex) when (NetworkErrors.IsTransient(ex) && !cancellationToken.IsCancellationRequested)
            {
                if (attempt >= settings.MaxAttempts)
                    throw new IOException($"Загрузка не завершилась за {attempt} попыток. {NetworkErrors.Describe(ex)}", ex);

                var serverDelay = (ex as TransferInterruptedException)?.RetryAfter;
                if (serverDelay > TimeSpan.FromMinutes(10))
                    throw new IOException($"Сервер просит повторить запрос не раньше чем через {serverDelay.Value.TotalMinutes:F0} мин. " +
                        "Повторите загрузку позже.", ex);

                var delay = serverDelay.HasValue
                    ? TimeSpan.FromSeconds(Math.Max(serverDelay.Value.TotalSeconds, 0))
                    : TimeSpan.FromSeconds(Math.Min(Math.Pow(2, attempt - 1), 15));
                report?.Invoke(new DownloadProgress(LengthOf(partialPath), ReadState(metadataPath, sourceKey)?.TotalBytes,
                    $"Повтор {attempt + 1}/{settings.MaxAttempts} через {delay.TotalSeconds:F0} с · {NetworkErrors.Describe(ex)}"));
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    private async Task TransferOnceAsync(Uri uri, string sourceKey, string partialPath, string metadataPath,
        Action<DownloadProgress>? report, CancellationToken cancellationToken)
    {
        var partialLength = LengthOf(partialPath);
        var state = partialLength > 0 ? ReadState(metadataPath, sourceKey) : null;
        if (state?.TotalBytes is long knownTotal && partialLength > knownTotal)
            state = null;
        var offset = state is null ? 0 : partialLength;
        report?.Invoke(new DownloadProgress(offset, state?.TotalBytes, offset > 0 ? "Запрос докачки" : "Ожидание ответа"));

        using var response = await GetAsync(uri, offset, state?.GetCondition(), cancellationToken);
        if (offset > 0 && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            ResetPartial(partialPath, metadataPath);
            throw new TransferInterruptedException("Сервер отклонил диапазон. Загрузка начнётся заново.", TimeSpan.Zero);
        }
        NetworkErrors.EnsureSuccess(response, "Загрузка VSIX");

        if (response.Content.Headers.ContentEncoding.Any(x => !string.Equals(x, "identity", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Сервер вернул сжатое HTTP-представление, непригодное для докачки VSIX.");

        var effectiveKey = ResourceKey(response.RequestMessage?.RequestUri ?? uri);
        long? totalBytes, responseBytes;
        var append = response.StatusCode == HttpStatusCode.PartialContent;
        if (response.StatusCode == HttpStatusCode.OK)
        {
            totalBytes = responseBytes = response.Content.Headers.ContentLength;
            offset = 0; // A full response replaces the partial file even when requested with Range.
            state = CreateState(response, sourceKey, effectiveKey, totalBytes);
        }
        else if (append && offset > 0 && state is not null)
        {
            var range = response.Content.Headers.ContentRange;
            var responseTag = response.Headers.ETag;
            var responseDate = response.Content.Headers.LastModified;
            var declaredLength = response.Content.Headers.ContentLength;
            if (range?.Unit != "bytes" || range.From != offset || range.To is not long last ||
                range.Length is not long length || last < offset || last >= length ||
                (state.TotalBytes is long oldLength && oldLength != length) ||
                (declaredLength is long size && size != last - offset + 1) || state.EffectiveKey != effectiveKey ||
                (state.ETag is not null && responseTag is not null && responseTag.ToString() != state.ETag) ||
                (state.LastModified is { } oldDate && responseDate.HasValue && responseDate != oldDate))
            {
                ResetPartial(partialPath, metadataPath);
                throw new TransferInterruptedException("Ответ докачки не совпал с сохранённой частью. Загрузка начнётся заново.", TimeSpan.Zero);
            }
            totalBytes = length;
            responseBytes = last - offset + 1;
            state = state with { TotalBytes = totalBytes };
        }
        else
        {
            throw new InvalidDataException($"Неожиданный ответ сервера при скачивании VSIX (HTTP {(int)response.StatusCode}).");
        }

        var written = await CopyAsync(response, partialPath, metadataPath, state, offset, totalBytes, responseBytes, append,
            report, cancellationToken);
        if (totalBytes is long total && written < total)
        {
            var servedRequestedSegment = responseBytes.HasValue && written - offset == responseBytes;
            throw new TransferInterruptedException(servedRequestedSegment
                ? "Сервер отдал часть диапазона. Продолжим со следующего байта."
                : "Сервер завершил передачу до получения всех байтов.", servedRequestedSegment ? TimeSpan.Zero : null);
        }
    }

    private async Task<HttpResponseMessage> GetAsync(Uri uri, long offset, RangeConditionHeaderValue? condition,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.ResponseTimeoutSeconds));

        try
        {
            for (var redirect = 0; ; redirect++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.UserAgent.ParseAdd("vsix-downloader/1.0");
                request.Headers.AcceptEncoding.ParseAdd("identity");
                if (offset > 0 && condition is not null)
                {
                    request.Headers.Range = new RangeHeaderValue(offset, null);
                    request.Headers.IfRange = condition;
                }

                var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Found or
                    HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect))
                    return response;

                using (response)
                {
                    var location = response.Headers.Location;
                    if (redirect == MaxRedirects || location is null || !Uri.TryCreate(uri, location, out var next) ||
                        !MarketplaceClient.IsHttpsUri(next))
                        throw new InvalidDataException("Некорректное перенаправление при загрузке из Marketplace.");
                    uri = next;
                }
            }
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TransferInterruptedException("Сервер не прислал заголовки ответа вовремя.", inner: ex);
        }
    }

    private async Task<long> CopyAsync(HttpResponseMessage response, string partialPath, string metadataPath,
        ResumeState? state, long offset, long? totalBytes, long? responseBytes, bool append,
        Action<DownloadProgress>? report, CancellationToken cancellationToken)
    {
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(partialPath, append ? FileMode.Open : FileMode.Create,
            FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (append)
        {
            if (output.Length != offset)
                throw new IOException("Размер частичного файла изменился во время скачивания.");
            output.Position = offset;
        }
        WriteState(metadataPath, state);

        var written = offset;
        var status = append ? "Докачка" : "Загрузка";
        report?.Invoke(new DownloadProgress(written, totalBytes, status));
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            while (true)
            {
                int count;
                try
                {
                    idle.CancelAfter(TimeSpan.FromSeconds(settings.IdleTimeoutSeconds));
                    count = await input.ReadAsync(buffer.AsMemory(0, BufferSize), idle.Token);
                    idle.CancelAfter(Timeout.InfiniteTimeSpan);
                }
                catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TransferInterruptedException($"Сервер не передавал данные более {settings.IdleTimeoutSeconds} с.", inner: ex);
                }
                catch (IOException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TransferInterruptedException("Соединение оборвалось во время скачивания.", inner: ex);
                }

                if (count == 0)
                    return written;
                if (responseBytes is long expected && count > expected - (written - offset))
                    throw new InvalidDataException("Сервер прислал больше байтов, чем заявил в заголовках.");

                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                written += count;
                report?.Invoke(new DownloadProgress(written, totalBytes, status));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static ResumeState? CreateState(HttpResponseMessage response, string sourceKey, string effectiveKey, long? totalBytes)
    {
        var tag = response.Headers.ETag;
        if (tag is { IsWeak: false } && tag.Tag != "*")
            return new ResumeState(sourceKey, effectiveKey, tag.ToString(), null, totalBytes);

        // RFC 9110 §8.8.2.2 requires evidence that the date is a strong validator.
        var modified = response.Content.Headers.LastModified;
        if (tag is null && modified.HasValue && response.Headers.Date - modified >= TimeSpan.FromSeconds(60))
            return new ResumeState(sourceKey, effectiveKey, null, modified, totalBytes);
        return null;
    }

    private static ResumeState? ReadState(string path, string sourceKey)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            var state = JsonSerializer.Deserialize<ResumeState>(File.ReadAllText(path), JsonOptions);
            return state?.SourceKey == sourceKey && state.GetCondition() is not null ? state : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void WriteState(string path, ResumeState? state)
    {
        if (state is null)
        {
            File.Delete(path);
            return;
        }
        var temp = path + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(state, JsonOptions));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    private static void ResetPartial(string partialPath, string metadataPath)
    {
        File.Delete(partialPath);
        File.Delete(metadataPath);
    }

    private static long LengthOf(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

    private static string ResourceKey(Uri uri) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri)));

    private sealed record ResumeState(string SourceKey, string EffectiveKey, string? ETag, DateTimeOffset? LastModified, long? TotalBytes)
    {
        public RangeConditionHeaderValue? GetCondition()
        {
            if (EntityTagHeaderValue.TryParse(ETag, out var tag) && !tag.IsWeak && tag.Tag != "*")
                return new RangeConditionHeaderValue(tag);
            return LastModified is { } date ? new RangeConditionHeaderValue(date) : null;
        }
    }
}
