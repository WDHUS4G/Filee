// Lua scripts and CSS style sheets (issues #388, #368) become a PDF of their text, never run.

using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Pdf;

namespace Filee.Engines.Tests;

public class ScriptTextTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    [Theory]
    [InlineData("site.css", "body { color: #1d9e75; }")]
    [InlineData("init.lua", "local greeting = \"hello\" print(greeting)")]
    public async Task The_code_becomes_a_pdf_of_its_text(string name, string code)
    {
        var available = fx.Converters.Where(c => fx.Catalog.StatusOf(c).IsAvailable).Select(c => c.Id).ToHashSet();
        Assert.SkipUnless(available.Contains("rhwp") || available.Contains("libreoffice"), "neither rhwp nor LibreOffice is available");
        var path = Path.Combine(fx.NewFolder(), name);
        await File.WriteAllTextAsync(path, code, TestContext.Current.CancellationToken);

        var job = await fx.ConvertAsync([path], new Preset { TargetFormat = "pdf" });

        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        var text = PdfDocumentReader.PlainText(PdfDocumentReader.ReadText(Assert.Single(job.Outputs)));
        Assert.Contains(code.Split(' ')[1], text);
    }
}
