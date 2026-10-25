using System.IO.Compression;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.Views;
using Filee.Core.Conversion;
using Filee.Core.Localization;
using Filee.Core.Settings;

namespace Filee.App.Tests;

public class SpreadsheetPrintStudioTests
{
    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("pt-PT")]
    [InlineData("pt-BR")]
    public async Task Translated_print_settings_fit_at_the_minimum_window_width(string language)
    {
        TestServices.EnsureInitialized(language);
        var folder = Directory.CreateTempSubdirectory("filee-studio-layout-").FullName;
        SpreadsheetPrintStudioWindow? window = null;
        SpreadsheetPrintStudioViewModel? vm = null;
        try
        {
            var source = Path.Combine(folder, "sample.xlsx");
            WriteWorkbook(source);
            vm = new SpreadsheetPrintStudioViewModel(source, AppHost.Get<SpreadsheetPrintService>(),
                AppHost.Get<ILocalizer>(), AppHost.Get<UserDataStore>());
            await vm.Initialization;
            Assert.Single(vm.Sheets);
            vm.SelectedSheet!.Order = vm.PageOrders.Last().Value;
            window = new SpreadsheetPrintStudioWindow { DataContext = vm, Width = 1040, Height = 860 };
            window.Show();
            RenderTests.Pump();
            if (Environment.GetEnvironmentVariable("FILEE_LOCALIZATION_SCREENSHOTS") == "1")
                RenderTests.Save(window, $"spreadsheet-studio-{language}-1040.png");
            var labels = window.GetVisualDescendants().OfType<TextBlock>()
                .Where(text => text.IsEffectivelyVisible && !string.IsNullOrWhiteSpace(text.Text));
            Assert.NotEmpty(labels);
            var clipped = labels.Where(text => text.TextLayout.Width > text.Bounds.Width + 1)
                .Select(text => $"'{text.Text}' ({text.TextLayout.Width} > {text.Bounds.Width})").ToArray();
            Assert.True(clipped.Length == 0, $"{language}: clipped text: {string.Join("; ", clipped)}");
        }
        finally
        {
            // The window owns its view model and disposes it when closed.
            if (window is not null) window.Close();
            else vm?.Dispose();
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }

    [AvaloniaFact]
    public void Multiple_workbooks_keep_independent_studio_windows()
    {
        TestServices.EnsureInitialized();
        var folder = Directory.CreateTempSubdirectory("filee-studios-test-").FullName;
        var windows = AppHost.Get<WindowService>();
        var before = windows.SpreadsheetStudios.ToArray();
        try
        {
            var first = Path.Combine(folder, "first.xlsx");
            var second = Path.Combine(folder, "second.xlsx");
            WriteWorkbook(first);
            WriteWorkbook(second);

            windows.ShowSpreadsheetPrintStudio(first);
            windows.ShowSpreadsheetPrintStudio(second);

            var opened = windows.SpreadsheetStudios.Except(before).ToArray();
            Assert.Equal(2, opened.Length);
            Assert.All(opened, window => Assert.True(window.IsVisible));
            Assert.Equal(["first.xlsx", "second.xlsx"], opened.Select(window =>
                ((SpreadsheetPrintStudioViewModel)window.DataContext!).FileName).Order());
            opened[0].Close();
            Assert.True(opened[1].IsVisible);
            Assert.DoesNotContain(opened[0], windows.SpreadsheetStudios);
        }
        finally
        {
            foreach (var window in windows.SpreadsheetStudios.Except(before).ToArray())
                window.Close();
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }

    [AvaloniaFact]
    public async Task Closing_during_export_cancels_LibreOffice_and_removes_temporary_files()
    {
        TestServices.EnsureInitialized();
        var service = AppHost.Get<SpreadsheetPrintService>();
        Assert.SkipUnless(service.Status.IsAvailable, "LibreOffice not found");
        var folder = Directory.CreateTempSubdirectory("filee-sheet-studio-test-").FullName;
        try
        {
            var source = Path.Combine(folder, "large.xlsx");
            WriteWorkbook(source);
            var output = Path.Combine(folder, "cancelled.pdf");
            var before = WorkFolders();
            var vm = new SpreadsheetPrintStudioViewModel(source, service, AppHost.Get<ILocalizer>(),
                AppHost.Get<UserDataStore>());
            await vm.Initialization;

            var export = vm.ExportAsync(output);
            await WaitUntilAsync(() => WorkFolders().Except(before).Any(), TimeSpan.FromSeconds(10));
            vm.Dispose();
            await export;
            await WaitUntilAsync(() => !WorkFolders().Except(before).Any(), TimeSpan.FromSeconds(10));

            Assert.False(File.Exists(output + ".filee-tmp"));
            Assert.Empty(WorkFolders().Except(before));
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }

    private static HashSet<string> WorkFolders() => Directory.Exists(JobQueue.DefaultWorkRoot)
        ? Directory.GetDirectories(JobQueue.DefaultWorkRoot).ToHashSet(StringComparer.Ordinal)
        : [];

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(25);
        Assert.True(condition());
    }

    private static void WriteWorkbook(string path)
    {
        var rows = new StringBuilder();
        for (var row = 1; row <= 4000; row++)
            rows.Append($"<row r=\"{row}\"><c r=\"A{row}\"><v>{row}</v></c></row>");
        var parts = new Dictionary<string, string>
        {
            ["_rels/.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>",
            ["xl/workbook.xml"] = "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><bookViews><workbookView activeTab=\"0\"/></bookViews><sheets><sheet name=\"Data\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>",
            ["xl/_rels/workbook.xml.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/></Relationships>",
            ["xl/worksheets/sheet1.xml"] = $"<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetViews><sheetView workbookViewId=\"0\" tabSelected=\"1\"/></sheetViews><sheetData>{rows}</sheetData></worksheet>",
            ["[Content_Types].xml"] = "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/></Types>",
        };
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, xml) in parts)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
            writer.Write("<?xml version=\"1.0\" encoding=\"UTF-8\"?>" + xml);
        }
    }
}
