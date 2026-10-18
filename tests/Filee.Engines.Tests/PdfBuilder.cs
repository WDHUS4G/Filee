// Builds small PDF files for the PDF reader tests with PdfPig's writer: text in the standard 14 fonts (every PDF
// reader has them, so no font file is needed and the tests run on machines without Windows fonts), wrapped
// paragraphs, colours, pictures, links and several pages. PDFsharp adds password protection to a finished file.

using ImageMagick;
using PdfSharp.Pdf.IO;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Filee.Engines.Tests;

/// <summary>A PDF written page by page; <c>y</c> coordinates are points from the top of the page.</summary>
internal sealed class PdfBuilder
{
    private readonly PdfDocumentBuilder _builder = new();
    private readonly Dictionary<Standard14Font, PdfDocumentBuilder.AddedFont> _fonts = [];
    private PdfPageBuilder? _page;
    private double _height;

    private PdfPageBuilder Page => _page ?? throw new InvalidOperationException("Add a page first.");

    /// <summary>Starts a new page (A4 by default).</summary>
    public PdfBuilder NewPage(double width = 595, double height = 842)
    {
        _page = _builder.AddPage(width, height);
        _height = height;
        return this;
    }

    /// <summary>Helvetica in the given style (a standard font: nothing is embedded).</summary>
    public PdfDocumentBuilder.AddedFont Font(bool bold = false, bool italic = false)
    {
        var standard = (bold, italic) switch
        {
            (true, true) => Standard14Font.HelveticaBoldOblique,
            (true, false) => Standard14Font.HelveticaBold,
            (false, true) => Standard14Font.HelveticaOblique,
            _ => Standard14Font.Helvetica,
        };
        if (!_fonts.TryGetValue(standard, out var font))
            _fonts[standard] = font = _builder.AddStandard14Font(standard);
        return font;
    }

    /// <summary>A TrueType font to embed (for scripts the standard fonts lack, such as Hangul).</summary>
    public PdfDocumentBuilder.AddedFont TrueType(byte[] font) => _builder.AddTrueTypeFont(font);

    public double Width(string text, double size, PdfDocumentBuilder.AddedFont font)
    {
        var letters = Page.MeasureText(text, size, new PdfPoint(0, 0), font);
        return letters.Count == 0 ? 0 : letters.Max(l => l.EndBaseLine.X) - letters.Min(l => l.StartBaseLine.X);
    }

    /// <summary>One line of text with its baseline <paramref name="y"/> points below the top of the page.</summary>
    public PdfBuilder Line(string text, double size, PdfDocumentBuilder.AddedFont font, double x, double y, (byte R, byte G, byte B)? color = null)
    {
        if (color is { } c)
            Page.SetTextAndFillColor(c.R, c.G, c.B);
        Page.AddText(text, size, new PdfPoint(x, _height - y), font);
        if (color is not null)
            Page.ResetColor();
        return this;
    }

    /// <summary>Text wrapped at word boundaries into <paramref name="width"/>; returns the baseline below the last line.</summary>
    public double Paragraph(string text, double size, PdfDocumentBuilder.AddedFont font, double x, double y, double width, double leading, char separator = ' ')
    {
        var line = "";
        foreach (var word in text.Split(separator))
        {
            var candidate = line.Length == 0 ? word : line + separator + word;
            if (line.Length > 0 && Width(candidate, size, font) > width)
            {
                Line(line, size, font, x, y);
                y += leading;
                line = word;
            }
            else
            {
                line = candidate;
            }
        }
        if (line.Length > 0)
        {
            Line(line, size, font, x, y);
            y += leading;
        }
        return y;
    }

    /// <summary>A JPEG (kept as a DCT stream) or PNG picture drawn into the rectangle.</summary>
    public PdfBuilder Picture(string path, double x, double y, double width, double height)
    {
        var bytes = File.ReadAllBytes(path);
        var rect = new PdfRectangle(x, _height - y - height, x + width, _height - y);
        if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            Page.AddPng(bytes, rect);
        else
            Page.AddJpeg(bytes, rect);
        return this;
    }

    /// <summary>A web link over the rectangle.</summary>
    public PdfBuilder Link(string url, double x, double y, double width, double height)
    {
        Page.AddLink(url, new PdfRectangle(x, _height - y - height, x + width, _height - y));
        return this;
    }

    public static string Photo(string folder, string name, MagickFormat format, uint width = 200, uint height = 120)
    {
        using var image = new MagickImage("gradient:#FF8800-#0044AA", width, height);
        var path = Path.Combine(folder, name);
        image.Write(path, format);
        return path;
    }

    public string Save(string path)
    {
        File.WriteAllBytes(path, _builder.Build());
        return path;
    }

    /// <summary>Encrypts a PDF with a password needed to open it (PDFsharp, no fonts involved).</summary>
    public static void Protect(string path, string password)
    {
        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Modify);
        document.SecuritySettings.UserPassword = password;
        document.SecuritySettings.OwnerPassword = password + "-owner";
        document.Save(path);
    }
}
