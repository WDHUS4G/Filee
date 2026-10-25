// The bottom-right progress cards: one card per job.

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.Views;
using Filee.Core.Conversion;
using Filee.Core.Localization;
using Filee.Core.Platform;
using Filee.Core.Presets;

namespace Filee.App.Tests;

public class ToastWindowTests
{
    [AvaloniaFact]
    public void A_job_handed_to_the_hidden_toast_gets_one_card()
    {
        // Issue #60: a large file's ring handed its job to the toast, which showed two identical cards.
        TestServices.EnsureInitialized("en");
        var service = AppHost.Get<ConversionService>();
        service.ToastJobs.Clear();
        var toast = new ToastWindow { DataContext = service };
        try
        {
            for (var round = 0; round < 3; round++)
            {
                var job = new JobViewModel(new ConversionJob(["C:/clip.mp4"], new Preset { TargetFormat = "mp4" }, "MP4"),
                    AppHost.Get<ILocalizer>(), AppHost.Get<IPlatformServices>(), _ => { });
                service.Jobs.Insert(0, job);
                service.ShowInToast(job);
                Pump();
                Assert.True(toast.IsVisible);
                Assert.Equal(1, Cards(toast));

                service.ToastJobs.Remove(job);
                service.Jobs.Remove(job);
                Pump();
                Assert.False(toast.IsVisible);
                Assert.Equal(0, Cards(toast));
            }
        }
        finally
        {
            service.ToastJobs.Clear();
            toast.Close();
        }
    }

    [AvaloniaFact]
    public void A_note_on_a_converted_file_is_shown_under_the_card()
    {
        TestServices.EnsureInitialized("en");
        var conversion = new ConversionJob(["C:/scan.pdf", "C:/photos.pdf"], new Preset { TargetFormat = "pdf" }, "Compress PDF");
        conversion.Files[0].State = FileState.Done;
        conversion.Files[0].NoticeKey = "notice.pdf_not_smaller";
        conversion.Files[1].State = FileState.Done;
        var job = new JobViewModel(conversion, AppHost.Get<ILocalizer>(), AppHost.Get<IPlatformServices>(), _ => { });

        Assert.True(job.HasNotices);
        Assert.Equal("scan.pdf — Couldn't make it smaller, so it was copied as it is", job.Notices);
        Assert.False(job.HasErrors);
    }

    [AvaloniaFact]
    public void Missing_document_fonts_are_combined_and_offered_without_blocking_the_job()
    {
        TestServices.EnsureInitialized("en");
        var conversion = new ConversionJob(["C:/one.docx", "C:/two.docx"], new Preset { TargetFormat = "pdf" }, "PDF");
        conversion.Files[0].State = FileState.Running;
        conversion.Files[0].MissingFonts = ["Tahoma", "Noto Serif SC"];
        conversion.Files[1].State = FileState.Done;
        conversion.Files[1].MissingFonts = ["tahoma"];
        var job = new JobViewModel(conversion, AppHost.Get<ILocalizer>(), AppHost.Get<IPlatformServices>(), _ => { });

        Assert.True(job.CanGetFonts);
        Assert.True(job.HasMissingFonts);
        Assert.Equal(["Noto Serif SC", "Tahoma"], job.MissingFonts);
        Assert.Equal("Missing document fonts: Noto Serif SC, Tahoma", job.MissingFontsText);
        Assert.True(job.IsRunning);
        Assert.Equal("https://learn.microsoft.com/en-us/typography/font-list/tahoma", FontSources.Url("Tahoma")!.ToString());
        Assert.Equal("https://fonts.google.com/?query=Noto Serif SC", FontSources.Url("Noto Serif SC")!.ToString());
        Assert.Null(FontSources.Url("맑은 고딕"));
        Assert.Null(FontSources.Url("함초롬바탕"));
        Assert.Null(FontSources.Url("MS Mincho"));
    }

    [AvaloniaFact]
    public void A_missing_family_without_a_public_font_page_still_shows_the_warning()
    {
        TestServices.EnsureInitialized("en");
        var conversion = new ConversionJob(["C:/one.docx"], new Preset { TargetFormat = "pdf" }, "PDF");
        conversion.Files[0].MissingFonts = ["MS Mincho"];
        var job = new JobViewModel(conversion, AppHost.Get<ILocalizer>(), AppHost.Get<IPlatformServices>(), _ => { });

        Assert.True(job.HasMissingFonts);
        Assert.False(job.CanGetFonts);
        Assert.Equal("Missing document fonts: MS Mincho", job.MissingFontsText);
    }

    private static int Cards(ToastWindow toast) =>
        toast.GetVisualDescendants().OfType<ItemsControl>().Single(c => c.Name == "Jobs").ItemsPanelRoot?.Children.Count ?? 0;

    private static void Pump()
    {
        for (var i = 0; i < 10; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }
}
