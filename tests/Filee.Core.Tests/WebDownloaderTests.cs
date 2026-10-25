// WebDownloader against a local HTTP server: names, redirects, the size cap, timeouts, cancelling, and what is refused.

using System.Net;
using System.Net.Sockets;
using Filee.Core.Conversion;
using Filee.Core.Web;

namespace Filee.Core.Tests;

public sealed class WebDownloaderTests : IDisposable
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D, 0x49, 0x48, 0x44, 0x52];

    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<string, Func<HttpListenerContext, Task>> _routes = new(StringComparer.Ordinal);
    private readonly TempDir _dir = new();
    private readonly string _root;

    public WebDownloaderTests()
    {
        var port = FreePort();
        _root = $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Add(_root);
        _listener.Start();
        _ = Task.Run(ServeAsync);
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }
            _ = Task.Run(async () =>
            {
                try
                {
                    if (_routes.TryGetValue(context.Request.Url!.AbsolutePath, out var route))
                        await route(context);
                    else
                        context.Response.StatusCode = 404;
                    context.Response.Close();
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or IOException or InvalidOperationException)
                {
                    // The client went away (a test that cancels or times out).
                }
            });
        }
    }

    private void Serve(string path, byte[] body, string? contentType = null, string? disposition = null, bool chunked = false) =>
        _routes[path] = async context =>
        {
            if (contentType is not null)
                context.Response.ContentType = contentType;
            if (disposition is not null)
                context.Response.AddHeader("Content-Disposition", disposition);
            if (chunked)
                context.Response.SendChunked = true;
            else
                context.Response.ContentLength64 = body.Length;
            await context.Response.OutputStream.WriteAsync(body);
        };

    private void Redirect(string path, string location, int status = 302) =>
        _routes[path] = context =>
        {
            context.Response.StatusCode = status;
            context.Response.RedirectLocation = location;
            return Task.CompletedTask;
        };

    private Uri Url(string path) => new(_root + path.TrimStart('/'));

    private static WebDownloader Downloader() => new(userAgent: "Filee-tests/1.0");

    [Fact]
    public async Task Names_the_file_as_the_server_says_and_by_what_it_is()
    {
        Serve("/get", Png, "application/octet-stream", "attachment; filename*=UTF-8''%EC%82%AC%EC%A7%84.jpg");
        using var downloader = Downloader();

        var path = await downloader.DownloadAsync(Url("/get"), _dir.Path, null, null, TestContext.Current.CancellationToken);

        Assert.Equal(Path.Combine(_dir.Path, "사진.png"), path); // named "사진.jpg", but the bytes are a PNG
        Assert.Equal(Png, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Names_the_file_after_the_address_and_never_replaces_one()
    {
        Serve("/files/report.pdf", "%PDF-1.7 test"u8.ToArray(), "application/pdf");
        File.WriteAllText(Path.Combine(_dir.Path, "report.pdf"), "mine");
        using var downloader = Downloader();

        var path = await downloader.DownloadAsync(Url("/files/report.pdf"), _dir.Path, null, null, TestContext.Current.CancellationToken);

        Assert.Equal(Path.Combine(_dir.Path, "report (2).pdf"), path);
        Assert.Equal("mine", File.ReadAllText(Path.Combine(_dir.Path, "report.pdf")));
    }

    [Fact]
    public async Task Follows_a_few_redirects_and_no_more()
    {
        Serve("/real/photo.png", Png, "image/png");
        Redirect("/short", "/hop");
        Redirect("/hop", _root + "real/photo.png", 301);
        for (var i = 0; i < 8; i++)
            Redirect($"/loop{i}", $"/loop{i + 1}");
        using var downloader = Downloader();

        var path = await downloader.DownloadAsync(Url("/short"), _dir.Path, null, null, TestContext.Current.CancellationToken);
        Assert.Equal("photo.png", Path.GetFileName(path));

        var refused = await Assert.ThrowsAsync<ConversionRefusedException>(() =>
            downloader.DownloadAsync(Url("/loop0"), _dir.Path, null, null, TestContext.Current.CancellationToken));
        Assert.Equal("error.web_download_failed", refused.ErrorKey);
    }

    [Fact]
    public async Task Never_follows_a_redirect_off_the_web()
    {
        Redirect("/sneaky", "file:///C:/Windows/win.ini");
        using var downloader = Downloader();

        var refused = await Assert.ThrowsAsync<ConversionRefusedException>(() =>
            downloader.DownloadAsync(Url("/sneaky"), _dir.Path, null, null, TestContext.Current.CancellationToken));
        Assert.Equal("error.web_download_failed", refused.ErrorKey);
        Assert.Empty(Directory.GetFiles(_dir.Path));
    }

    [Fact]
    public async Task Refuses_a_file_over_the_size_cap_said_or_not_and_leaves_nothing_behind()
    {
        var big = new byte[64 * 1024];
        Png.CopyTo(big, 0);
        Serve("/said.png", big, "image/png");
        Serve("/unsaid.png", big, "image/png", chunked: true);
        using var downloader = new WebDownloader { MaxBytes = 10_000 };

        var said = await Assert.ThrowsAsync<ConversionRefusedException>(() =>
            downloader.DownloadAsync(Url("/said.png"), _dir.Path, null, null, TestContext.Current.CancellationToken));
        var unsaid = await Assert.ThrowsAsync<ConversionRefusedException>(() =>
            downloader.DownloadAsync(Url("/unsaid.png"), _dir.Path, null, null, TestContext.Current.CancellationToken));

        Assert.Equal("error.web_too_large", said.ErrorKey);
        Assert.Equal("error.web_too_large", unsaid.ErrorKey);
        Assert.Empty(Directory.GetFiles(_dir.Path));
    }

    [Fact]
    public async Task Refuses_web_pages_and_programs()
    {
        Serve("/doc.pdf", "<!doctype html><title>Sign in</title>"u8.ToArray(), "text/html; charset=utf-8");
        Serve("/sneaky.pdf", "<html><body>Sign in</body></html>"u8.ToArray(), "application/octet-stream");
        Serve("/tool.png", "MZ\x90\0\x03\0\0\0"u8.ToArray(), "image/png");
        Serve("/x.bin", "%PDF-1.4"u8.ToArray(), "application/x-msdownload");
        using var downloader = Downloader();
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal("error.web_page", (await Assert.ThrowsAsync<ConversionRefusedException>(() => downloader.DownloadAsync(Url("/doc.pdf"), _dir.Path, null, null, ct))).ErrorKey);
        Assert.Equal("error.web_page", (await Assert.ThrowsAsync<ConversionRefusedException>(() => downloader.DownloadAsync(Url("/sneaky.pdf"), _dir.Path, null, null, ct))).ErrorKey);
        Assert.Equal("error.web_blocked", (await Assert.ThrowsAsync<ConversionRefusedException>(() => downloader.DownloadAsync(Url("/tool.png"), _dir.Path, null, null, ct))).ErrorKey);
        Assert.Equal("error.web_blocked", (await Assert.ThrowsAsync<ConversionRefusedException>(() => downloader.DownloadAsync(Url("/x.bin"), _dir.Path, null, null, ct))).ErrorKey);
        Assert.Empty(Directory.GetFiles(_dir.Path));
    }

    [Fact]
    public async Task Reports_a_missing_file_with_its_status()
    {
        using var downloader = Downloader();
        var refused = await Assert.ThrowsAsync<ConversionRefusedException>(() =>
            downloader.DownloadAsync(Url("/nothing.pdf"), _dir.Path, null, null, TestContext.Current.CancellationToken));
        Assert.Equal("error.web_download_failed", refused.ErrorKey);
        Assert.Equal("HTTP 404", refused.Detail);
    }

    [Fact]
    public async Task Gives_up_on_a_server_that_doesnt_answer_or_stalls()
    {
        _routes["/silent.png"] = async _ => await Task.Delay(TimeSpan.FromSeconds(5), _stop.Token);
        _routes["/stalls.png"] = async context =>
        {
            context.Response.ContentType = "image/png";
            context.Response.ContentLength64 = 10_000;
            await context.Response.OutputStream.WriteAsync(Png);
            await context.Response.OutputStream.FlushAsync();
            await Task.Delay(TimeSpan.FromSeconds(5), _stop.Token);
        };
        using var downloader = new WebDownloader
        {
            ResponseTimeout = TimeSpan.FromMilliseconds(300),
            StallTimeout = TimeSpan.FromMilliseconds(300),
        };
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal("error.web_timeout", (await Assert.ThrowsAsync<ConversionRefusedException>(() => downloader.DownloadAsync(Url("/silent.png"), _dir.Path, null, null, ct))).ErrorKey);
        Assert.Equal("error.web_timeout", (await Assert.ThrowsAsync<ConversionRefusedException>(() => downloader.DownloadAsync(Url("/stalls.png"), _dir.Path, null, null, ct))).ErrorKey);
        Assert.Empty(Directory.GetFiles(_dir.Path));
    }

    [Fact]
    public async Task Cancelling_stops_the_download_and_removes_the_partial_file()
    {
        // A first chunk past the sniffed head, so the file gets created, then nothing until the test is over.
        var chunk = new byte[WebFormats.SniffLength * 4];
        Png.CopyTo(chunk, 0);
        _routes["/slow.png"] = async context =>
        {
            context.Response.ContentType = "image/png";
            context.Response.ContentLength64 = 10_000;
            await context.Response.OutputStream.WriteAsync(chunk);
            await context.Response.OutputStream.FlushAsync();
            await Task.Delay(Timeout.Infinite, _stop.Token);
        };
        using var downloader = Downloader();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        // The first report comes right after the file is created and the head written into it.
        var written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var download = downloader.DownloadAsync(Url("/slow.png"), _dir.Path, null, new ProgressCallback(_ => written.TrySetResult()), cancel.Token);
        await await Task.WhenAny(written.Task, download); // a download that fails before writing fails the test with its reason
        Assert.Single(Directory.GetFiles(_dir.Path));
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
        Assert.Empty(Directory.GetFiles(_dir.Path)); // deleted before the cancellation reaches the caller
    }

    [Fact]
    public async Task Reports_the_bytes_so_far_and_the_size_when_the_server_says_it()
    {
        var body = new byte[300_000];
        Png.CopyTo(body, 0);
        Serve("/sized.png", body, "image/png");
        Serve("/unsized.png", body, "image/png", chunked: true);
        using var downloader = new WebDownloader { ReportInterval = TimeSpan.Zero };
        var sized = new List<TransferProgress>();
        var unsized = new List<TransferProgress>();

        await downloader.DownloadAsync(Url("/sized.png"), _dir.Path, null, new SyncProgress(sized), TestContext.Current.CancellationToken);
        await downloader.DownloadAsync(Url("/unsized.png"), _dir.Path, null, new SyncProgress(unsized), TestContext.Current.CancellationToken);

        Assert.True(sized.Count >= 2);
        Assert.All(sized, p => Assert.Equal(body.Length, p.Total));
        Assert.Equal(sized.Select(p => p.Bytes).Order(), sized.Select(p => p.Bytes)); // only forward
        Assert.Equal(new TransferProgress(body.Length, body.Length), sized[^1]);
        Assert.Equal(1, sized[^1].Fraction);
        Assert.All(unsized, p => Assert.Null(p.Total));
        Assert.Equal(body.Length, unsized[^1].Bytes);
        Assert.Null(unsized[^1].Fraction);
    }

    [Fact]
    public async Task Reports_are_spaced_out_but_the_last_one_always_comes()
    {
        var body = new byte[400_000];
        Png.CopyTo(body, 0);
        Serve("/many.png", body, "image/png");
        using var downloader = new WebDownloader { ReportInterval = TimeSpan.FromHours(1) };
        var reports = new List<TransferProgress>();

        await downloader.DownloadAsync(Url("/many.png"), _dir.Path, null, new SyncProgress(reports), TestContext.Current.CancellationToken);

        Assert.Equal([new TransferProgress(WebFormats.SniffLength, body.Length), new TransferProgress(body.Length, body.Length)], reports);
    }

    [Fact]
    public async Task Bytes_a_browser_handed_over_are_saved_like_a_download()
    {
        using var downloader = Downloader();
        var item = WebDropItem.FromBytes("image.jpg", Png);

        var path = await item.SaveAsync(_dir.Path, downloader, null, TestContext.Current.CancellationToken);

        Assert.Equal(Path.Combine(_dir.Path, "image.png"), path);
        var program = WebDropItem.FromBytes("innocent.png", "MZ\x90\0"u8.ToArray());
        Assert.Equal("error.web_blocked", (await Assert.ThrowsAsync<ConversionRefusedException>(() =>
            program.SaveAsync(_dir.Path, downloader, null, TestContext.Current.CancellationToken))).ErrorKey);
    }

    private sealed class ProgressCallback(Action<TransferProgress> report) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value) => report(value);
    }

    private sealed class SyncProgress(List<TransferProgress> values) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value)
        {
            lock (values)
                values.Add(value);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Close();
        _dir.Dispose();
    }
}
