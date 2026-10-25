// "Request this format" (issue #117): files of a type Filee can't read lead to the GitHub "Format request" form with
// the extension filled in, from the toast of a failed conversion and when the donut closes on such files.

using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Filee.App.Services;
using Filee.Core.Settings;

namespace Filee.App.Tests;

[Collection("MainWindow")]
public sealed class FormatRequestTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "filee-format-request-" + Guid.NewGuid().ToString("N"));

    public FormatRequestTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, true); } catch (IOException) { }
    }

    /// <summary>An extension no test has offered yet: the donut offers each one once while Filee runs.</summary>
    private static string NewExtension() => "q" + Guid.NewGuid().ToString("N")[..6];

    private string File(string name)
    {
        var path = Path.Combine(_folder, name);
        System.IO.File.WriteAllText(path, "data");
        return path;
    }

    private static void Pump()
    {
        for (var i = 0; i < 10; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    [Fact]
    public void Only_unknown_extensions_are_requested_once_each_in_lower_case()
    {
        var folder = Path.Combine(_folder, "album.ncm");
        Directory.CreateDirectory(folder);
        var paths = new[]
        {
            File("song.NCM"), File("other.ncm"), File("photo.jpg"), File("README"), File("track.kgm"), folder,
            File("a.qmc0"), File("b.mflac"),
        };

        // Known types, a folder and a file without an extension have nothing to request; at most three per request.
        Assert.Equal([".ncm", ".kgm", ".qmc0"], FormatRequest.UnknownExtensions(paths));
    }

    [Fact]
    public void The_link_opens_the_format_request_form_with_only_the_extension()
    {
        var url = FormatRequest.Url([".ncm", ".kgm"]).AbsoluteUri;

        Assert.StartsWith(UpdateService.RepositoryUrl.TrimEnd('/') + "/issues/new?template=format_request.yml&", url);
        Assert.Contains("&title=%5BFormat%5D%3A%20.ncm%2C%20.kgm", url);
        Assert.Contains("&extension=.ncm%2C%20.kgm", url);
    }

    [AvaloniaFact]
    public async Task A_conversion_that_fails_on_an_unknown_type_offers_the_request_in_the_toast()
    {
        TestServices.EnsureInitialized("en");
        var store = AppHost.Get<UserDataStore>();
        var extension = NewExtension();
        var job = AppHost.Get<ConversionService>().Start([File("song." + extension)], store.FindPreset("to-pdf")!, showInToast: false)!;
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (job.IsRunning && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
                Pump();
            }

            Assert.False(job.IsRunning);
            Assert.True(job.CanRequestFormat);
            Assert.Equal(["." + extension], job.UnknownExtensions);
        }
        finally
        {
            job.DismissCommand.Execute(null);
        }
    }

    [AvaloniaFact]
    public void The_donut_closing_on_unknown_files_offers_the_request_once()
    {
        TestServices.EnsureInitialized("en");
        var radial = AppHost.Get<RadialController>();
        var windows = AppHost.Get<WindowService>();
        var extension = NewExtension();
        var song = File("song." + extension);
        try
        {
            radial.ShowForFiles(400, 400, [song]);
            radial.Close(animate: false);
            Pump();

            var notice = windows.FormatRequestNotice;
            Assert.NotNull(notice);
            Assert.True(notice.IsVisible);
            Assert.Equal(["." + extension], notice.Extensions);
            notice.Close();
            Pump();

            // Once per extension while Filee runs.
            radial.ShowForFiles(400, 400, [song]);
            radial.Close(animate: false);
            Pump();
            Assert.Null(windows.FormatRequestNotice);

            // Files Filee reads: nothing to request, even mixed with an unknown type (the toast covers that one).
            radial.ShowForFiles(400, 400, [File("notes.txt"), File("song." + NewExtension())]);
            radial.Close(animate: false);
            Pump();
            Assert.Null(windows.FormatRequestNotice);
        }
        finally
        {
            windows.FormatRequestNotice?.Close();
        }
    }
}
