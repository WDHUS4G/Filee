// The badge over a download's progress (the donut's ring, the toast): "2.4 MB/s · 12.3 / 48.0 MB", with "2 of 3
// files" when several are saved. Amounts and speed use the Engines page's units and the user's decimal separator;
// the speed is averaged over the last second and only shown once there is enough to tell.

using Filee.App.Services;
using Filee.Core.Conversion;
using Filee.Core.Localization;
using Filee.Engines.Infrastructure;

namespace Filee.App.ViewModels;

internal sealed class DownloadBadge(ILocalizer loc)
{
    /// <summary>How far back the speed is averaged.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(1);

    /// <summary>How long the download must run before the badge shows (a fast copy never shows one).</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(300);

    /// <summary>How often the badge is rewritten while it shows.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);

    private readonly TransferRate _rate = new(Window, Settle);
    private bool _measured;

    /// <summary>
    /// The badge's text for what the job saves at <paramref name="elapsed"/>, or null while it saves nothing or it is
    /// too early to tell a speed.
    /// </summary>
    public string? Update(TimeSpan elapsed, JobTransfer? transfer)
    {
        if (transfer is not { } now)
            return null;
        _rate.Add(elapsed, now.Received);
        _measured |= _rate.BytesPerSecond > 0;
        return _measured ? Format(loc, _rate.BytesPerSecond, now) : null;
    }

    /// <summary>"2.4 MB/s · 12.3 / 48.0 MB", "2.4 MB/s · 12.3 MB" without a size, "… · 2 of 3 files" for several.</summary>
    public static string Format(ILocalizer loc, double bytesPerSecond, JobTransfer transfer)
    {
        var text = loc.Format("download.badge", EnginePackageState.FormatAmount(bytesPerSecond), Amounts(transfer.Bytes, transfer.Total));
        return transfer.Count > 1 ? loc.Format("download.badge_files", text, Math.Min(transfer.Saved + 1, transfer.Count), transfer.Count) : text;
    }

    /// <summary>"12.3 / 48.0 MB" (one unit when both share it), "850 KB / 1.20 GB", or "12.3 MB" without a total.</summary>
    internal static string Amounts(long done, long? total)
    {
        var shown = EnginePackageState.FormatAmount(done);
        if (total is not { } size)
            return shown;
        var whole = EnginePackageState.FormatAmount(size);
        var (number, unit) = Split(shown);
        var (_, wholeUnit) = Split(whole);
        return unit == wholeUnit ? $"{number} / {whole}" : $"{shown} / {whole}";
    }

    private static (string Number, string Unit) Split(string amount)
    {
        var space = amount.LastIndexOf(' ');
        return space < 0 ? (amount, "") : (amount[..space], amount[(space + 1)..]);
    }
}
