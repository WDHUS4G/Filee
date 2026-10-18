# Adding a converter

A converter is one class implementing `IConverter` (`src/Filee.Core/Conversion/IConverter.cs`).
The route planner combines converters automatically, so a new edge can unlock many conversions at once
(for example, adding `epub → pdf` also gives `epub → png` through PDFium).

## 1. New format? Register it

If the format is new, add one line to `FormatRegistry.Known` (`src/Filee.Core/Formats/FormatRegistry.cs`):

```csharp
new("epub", "EPUB", FormatCategory.Document, ["epub"]),
```

## 2. Write the converter

Create `src/Filee.Engines/<Area>/<Name>Converter.cs`:

```csharp
// EPUB → PDF with the (hypothetical) SuperEpub library (MIT).

using Filee.Core.Conversion;

namespace Filee.Engines.Documents;

public sealed class SuperEpubConverter : IConverter
{
    public string Id => "superepub";                 // stable, used in settings
    public string DisplayName => "SuperEpub";
    public int MaxParallelism => 0;                  // 0 = one per CPU core; 1 for non thread-safe engines

    public IReadOnlyList<ConversionEdge> Edges { get; } =
    [
        new("epub", "pdf"),                          // cost 10 = normal; higher for lossy/slow paths
    ];

    public EngineStatus GetStatus() => EngineStatus.Available();

    public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken ct) =>
        Task.Run<IReadOnlyList<string>>(() =>
        {
            var output = step.Output.Allocate("pdf");     // null = skip (file exists + "skip" policy)
            if (output is null)
                return [];
            SuperEpub.Convert(step.InputPath, output);   // honour ct in long loops
            progress?.Report(1);
            return [output];
        }, ct);
}
```

Rules:

- Never modify `step.InputPath`.
- Write outputs only to paths from `step.Output.Allocate(...)`. For several outputs from one input
  (pages), pass a suffix: `Allocate("png", $"_p{page}")`.
- Scratch files go to `step.WorkDirectory` (deleted after the job).
- External programs: use `ProcessRunner.RunAsync` (timeout + cancellation + no shell) and find them with
  `EngineEnvironment.FindBundled("<folder>")` only. Filee never uses programs installed on the system (nothing from
  `PATH`, no other application's install folder): a program is either bundled with the installer (small, pinned in
  `engines.json` and fetched by `build/fetch-engines.ps1`) or an optional download the user installs in Settings →
  Engines (an `EnginePackage` in `EngineDownloads.Packages`).
- Return `EngineStatus.Unavailable("engine.reason.not_installed")` when a dependency is missing; the UI then greys
  out affected slices with a reason and names the package to download. Report a version with
  `EngineStatus.Available(version: …)` (see `EngineVersions`) and add an `engine.<id>.description` text to the three
  `i18n/*.json` files: both are shown in Settings → Engines.

## 3. Register it

Add one line to `EngineRegistry.CreateAll` (`src/Filee.Engines/EngineRegistry.cs`).
If it should be preferred over existing engines by default, add its id to `AppSettings.EnginePriority`.

## 4. Test it

Add a test to `tests/Filee.Engines.Tests`. Generate the input files in the test (see `EngineFixture`) so the test
runs everywhere without sample documents, and skip when the engine is not available:

```csharp
[Fact]
public async Task Epub_to_pdf()
{
    var job = await fx.ConvertAsync([MakeEpub()], new Preset { TargetFormat = "pdf" });
    Assert.Equal(JobState.Completed, job.State);
}
```

## 5. Ship it (optional)

Command line tools are pinned in `src/Filee.Engines/Infrastructure/engines.json` (URL, SHA-256, size, archive kind),
which both `build/fetch-engines.ps1` and the app read. Small tools can be bundled with the installer (add the id to
the `fetch-engines.ps1 -Only` list in `.github/workflows/release.yml`); large ones become an optional download by
adding an `EnginePackage` to `EngineDownloads.Packages` plus its name and description in the three `i18n/*.json`
files. Either way, add a line to `THIRD-PARTY-NOTICES.md`.
