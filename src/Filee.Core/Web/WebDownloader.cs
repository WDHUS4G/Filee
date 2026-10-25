// Saves files from the web: the bytes a browser handed over with a drag, or, when a drag brought only an address, the
// file downloaded from it. Downloads are plain GET requests without the browser's cookies or sign-in (Filee has
// neither): only http and https, a few redirects, a size cap and timeouts. Whatever arrives is named by what it
// really is, never replaces an existing file, and programs or web pages are refused instead of saved.

using System.Net;
using System.Net.Http.Headers;
using Filee.Core.Conversion;

namespace Filee.Core.Web;

public sealed class WebDownloader : IDisposable
{
    /// <summary>Largest file Filee saves from the web.</summary>
    public const long DefaultMaxBytes = 2L * 1024 * 1024 * 1024;

    private readonly HttpClient _http;

    /// <param name="handler">For tests; by default a handler without cookies, credentials or automatic redirects.</param>
    /// <param name="userAgent">"Filee/1.13.0"; some servers refuse requests without one.</param>
    public WebDownloader(HttpMessageHandler? handler = null, string? userAgent = null)
    {
        handler ??= new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = true,
            Credentials = null,
            PreAuthenticate = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(20),
        };
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        if (userAgent is not null)
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
    }

    public long MaxBytes { get; init; } = DefaultMaxBytes;

    public int MaxRedirects { get; init; } = 5;

    /// <summary>Least time between two progress reports while bytes arrive.</summary>
    public TimeSpan ReportInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>How long the server may take to start answering.</summary>
    public TimeSpan ResponseTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a download may go without receiving anything.</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Downloads <paramref name="url"/> into <paramref name="folder"/> and returns the file's path. The name comes from
    /// the server (Content-Disposition), else <paramref name="suggestedName"/>, else the address, and its extension
    /// from the first bytes when they say what the file is.
    /// </summary>
    /// <exception cref="ConversionRefusedException">With the reason to show (<c>error.web_*</c>).</exception>
    public async Task<string> DownloadAsync(Uri url, string folder, string? suggestedName, IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!IsWebAddress(url))
            throw new ConversionRefusedException("error.web_download_failed", url.Scheme);

        using var response = await GetFollowingRedirectsAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new ConversionRefusedException("error.web_download_failed", $"HTTP {(int)response.StatusCode}");

        var headers = response.Content.Headers;
        var mime = headers.ContentType?.MediaType;
        if (WebFormats.IsPage(mime))
            throw new ConversionRefusedException("error.web_page");
        if (WebFormats.IsProgram(mime))
            throw new ConversionRefusedException("error.web_blocked");
        if (headers.ContentLength is { } length && length > MaxBytes)
            throw new ConversionRefusedException("error.web_too_large", Megabytes(length));

        var finalUrl = response.RequestMessage?.RequestUri ?? url;
        var name = WebFileNames.FromContentDisposition(headers.ContentDisposition?.ToString())
                   ?? suggestedName
                   ?? WebFileNames.FromUrl(finalUrl)
                   ?? "download";
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await SaveAsync(body, folder, name, mime, headers.ContentLength, progress, cancellationToken, refusePages: true);
    }

    /// <summary>
    /// Writes <paramref name="source"/> into <paramref name="folder"/> as <paramref name="name"/> (sanitized, renamed to
    /// what the first bytes are, numbered when taken) and returns the path. A file cut short by an error is deleted.
    /// </summary>
    /// <param name="refusePages">
    /// A download: an HTML page where a file was expected is a sign-in or error page, not the file.
    /// </param>
    /// <exception cref="ConversionRefusedException">A program, a web page or a file over <see cref="MaxBytes"/>.</exception>
    public async Task<string> SaveAsync(Stream source, string folder, string name, string? mime, long? length,
        IProgress<TransferProgress>? progress, CancellationToken cancellationToken, bool refusePages = false)
    {
        // Every chunk would flood the UI: a report at most every ReportInterval, and the last one always.
        TimeSpan? lastReport = null;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        void Report(long bytes, bool final = false)
        {
            if (progress is null || (!final && lastReport is { } last && clock.Elapsed - last < ReportInterval))
                return;
            lastReport = clock.Elapsed;
            progress.Report(new TransferProgress(bytes, length is > 0 ? length : null));
        }

        var head = new byte[WebFormats.SniffLength];
        var headLength = await ReadAtLeastAsync(source, head, cancellationToken);
        if (WebFormats.LooksLikeProgram(head.AsSpan(0, headLength)))
            throw new ConversionRefusedException("error.web_blocked");
        if (refusePages && WebFormats.LooksLikePage(head.AsSpan(0, headLength)))
            throw new ConversionRefusedException("error.web_page");

        name = WebFormats.NameFor(WebFileNames.Sanitize(name), head.AsSpan(0, headLength), mime);
        if (WebFileNames.IsBlocked(name))
            throw new ConversionRefusedException("error.web_blocked");

        var file = WebFileNames.CreateUnique(folder, name, out var path);
        try
        {
            await using (file)
            {
                await file.WriteAsync(head.AsMemory(0, headLength), cancellationToken);
                long total = headLength;
                Report(total);
                var buffer = new byte[1 << 16];
                while (true)
                {
                    var read = await ReadWithStallTimeoutAsync(source, buffer, cancellationToken);
                    if (read == 0)
                        break;
                    total += read;
                    if (total > MaxBytes)
                        throw new ConversionRefusedException("error.web_too_large", Megabytes(total));
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    Report(total);
                }
                Report(total, final: true);
            }
            return path;
        }
        catch
        {
            TryDelete(path);
            throw;
        }
    }

    /// <summary>True for an http or https address, the only ones Filee downloads.</summary>
    public static bool IsWebAddress(Uri url) =>
        url.IsAbsoluteUri && (url.Scheme == Uri.UriSchemeHttps || url.Scheme == Uri.UriSchemeHttp);

    private async Task<HttpResponseMessage> GetFollowingRedirectsAsync(Uri url, CancellationToken cancellationToken)
    {
        for (var hop = 0; ; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
            HttpResponseMessage response;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(ResponseTimeout);
                try
                {
                    response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new ConversionRefusedException("error.web_timeout");
                }
                catch (HttpRequestException ex)
                {
                    throw new ConversionRefusedException("error.web_download_failed", ex.HttpRequestError switch
                    {
                        HttpRequestError.NameResolutionError => url.Host,
                        _ => ex.Message,
                    });
                }
            }

            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                response.Dispose();
                var next = location.IsAbsoluteUri ? location : new Uri(url, location);
                // Never from https down to http, and never to anything but the web (file:, ftp:).
                if (!IsWebAddress(next) || (url.Scheme == Uri.UriSchemeHttps && next.Scheme != Uri.UriSchemeHttps))
                    throw new ConversionRefusedException("error.web_download_failed", next.Scheme + ":");
                if (hop >= MaxRedirects)
                    throw new ConversionRefusedException("error.web_download_failed", "too many redirects");
                url = next;
                continue;
            }
            return response;
        }
    }

    private async Task<int> ReadAtLeastAsync(Stream source, byte[] buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await ReadWithStallTimeoutAsync(source, buffer.AsMemory(total), cancellationToken);
            if (read == 0)
                break;
            total += read;
        }
        return total;
    }

    private async Task<int> ReadWithStallTimeoutAsync(Stream source, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stall.CancelAfter(StallTimeout);
        try
        {
            return await source.ReadAsync(buffer, stall.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ConversionRefusedException("error.web_timeout");
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            throw new ConversionRefusedException("error.web_download_failed", ex.Message);
        }
    }

    private static string Megabytes(long bytes) => $"{bytes / (1024.0 * 1024):0} MB";

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose() => _http.Dispose();
}
