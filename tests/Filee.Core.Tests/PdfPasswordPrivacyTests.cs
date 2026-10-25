// A preset's PDF password stays in the library: it is left out of history and of an export, and a retry from history
// never writes the PDF without it.

using Filee.Core.Conversion;
using Filee.Core.History;
using Filee.Core.Presets;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class PdfPasswordPrivacyTests
{
    private static Preset Locked() => new() { Id = "locked", TargetFormat = "pdf", Pdf = { Password = "secret-1234" } };

    [Fact]
    public async Task History_keeps_no_password_but_remembers_one_was_there()
    {
        using var dir = new TempDir();
        await using var queue = new JobQueue(new ConverterCatalog([new FakeConverter("magick", new ConversionEdge("jpg", "pdf")) { Throw = true }]));
        var preset = Locked();
        var job = new ConversionJob([dir.File("a.jpg")], preset, "Test");
        await queue.RunAsync(job);

        var entry = HistoryEntry.From(job);

        Assert.Equal("", entry.Preset!.Pdf.Password);
        Assert.True(entry.Preset.Pdf.PasswordLeftOut);
        Assert.Equal("secret-1234", preset.Pdf.Password);
    }

    [Fact]
    public void An_export_leaves_passwords_out()
    {
        using var dir = new TempDir();
        var store = new UserDataStore(dir.Path);
        store.Load();
        store.Presets.Add(Locked());
        var bundle = Path.Combine(dir.Path, "bundle.json");

        store.Export(bundle);

        Assert.DoesNotContain("secret-1234", File.ReadAllText(bundle));
        Assert.Equal("secret-1234", store.FindPreset("locked")!.Pdf.Password);
        using var other = new TempDir();
        var target = new UserDataStore(other.Path);
        target.Load();
        target.Import(bundle);
        Assert.False(target.FindPreset("locked")!.Pdf.PasswordLeftOut);
    }

    [Fact]
    public async Task A_retry_without_its_password_fails_and_writes_no_pdf()
    {
        using var dir = new TempDir();
        await using var queue = new JobQueue(new ConverterCatalog([new FakeConverter("magick", new ConversionEdge("jpg", "pdf"))]));
        var fromHistory = Locked().WithoutPassword(mark: true);
        var job = new ConversionJob([dir.File("a.jpg")], fromHistory, "Test");

        await queue.RunAsync(job);

        Assert.Equal(JobState.Failed, job.State);
        Assert.Equal("error.pdf_password_gone", job.Files[0].ErrorKey);
        Assert.False(File.Exists(Path.Combine(dir.Path, "a.pdf")));
    }
}
