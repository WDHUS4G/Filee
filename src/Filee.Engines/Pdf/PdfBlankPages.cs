// Removes pages that contain no painting operations at all. This is deliberately stricter than "no text": charts,
// borders, pictures, annotations and even a deliberately painted white rectangle keep a page. LibreOffice Calc can
// append empty tiles when an imported spreadsheet's saved print range is wider or taller than its actual content.

using PdfSharp.Pdf;
using PdfSharp.Pdf.Content;
using PdfSharp.Pdf.Content.Objects;
using PdfSharp.Pdf.IO;

namespace Filee.Engines.Pdf;

internal static class PdfBlankPages
{
    /// <summary>Operators that can put a visible mark on a page.</summary>
    private static readonly HashSet<OpCodeName> Paint =
    [
        // Stroke / fill the current path.
        OpCodeName.S, OpCodeName.s, OpCodeName.f, OpCodeName.F, OpCodeName.fx,
        OpCodeName.B, OpCodeName.Bx, OpCodeName.b, OpCodeName.bx,
        // XObjects, inline images and shadings.
        OpCodeName.Do, OpCodeName.BI, OpCodeName.sh,
        // Show text. Count it even when it happens to be empty or invisible: uncertain pages must stay.
        OpCodeName.Tj, OpCodeName.TJ, OpCodeName.QuoteSingle, OpCodeName.QuoteDouble, OpCodeName.Tx,
    ];

    /// <summary>
    /// Removes pages with no painting operators from <paramref name="path"/>, replacing it only after the cleaned PDF
    /// was saved successfully. A wholly blank document is left alone: a valid PDF still needs a page, and an empty
    /// workbook may have meant to produce one.
    /// </summary>
    /// <returns>The number of pages removed.</returns>
    public static int Remove(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var temp = Path.Combine(Path.GetDirectoryName(fullPath)!, $".{Path.GetFileNameWithoutExtension(path)}-{Guid.NewGuid():N}.pdf");
        try
        {
            int removed;
            using (var document = PdfReader.Open(fullPath, PdfDocumentOpenMode.Modify))
            {
                var blank = Enumerable.Range(0, document.PageCount).Where(i => IsBlank(document.Pages[i])).ToList();
                if (blank.Count == 0 || blank.Count == document.PageCount)
                    return 0;

                for (var i = blank.Count - 1; i >= 0; i--)
                    document.Pages.RemoveAt(blank[i]);
                removed = blank.Count;
                document.Save(temp);
            }
            File.Move(temp, fullPath, overwrite: true);
            return removed;
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Unreadable content is not blank: uncertainty must never delete a user's page.</summary>
    internal static bool IsBlank(PdfPage page)
    {
        if (page.Elements.GetArray("/Annots") is { Elements.Count: > 0 })
            return false;
        try
        {
            return !Paints(ContentReader.ReadContent(page));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    private static bool Paints(CSequence content)
    {
        foreach (var item in content)
        {
            if (item is COperator op && Paint.Contains(op.OpCode.OpCodeName))
                return true;
            if (item is CSequence nested && Paints(nested))
                return true;
        }
        return false;
    }
}
