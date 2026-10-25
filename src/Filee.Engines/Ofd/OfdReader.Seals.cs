// Seals for OfdReader: an e-invoice's or a contract's seal is not drawn on the page but stored in its signature
// (Signs/Sign_n/SignedValue.dat, GM/T 0031 SES_Signature, DER). The seal picture inside it — PNG, JPEG, GIF, BMP or a
// small OFD package of its own (the red oval of 全国统一发票监制章 drawn with paths and rotated characters) — is shown
// where the signature's StampAnnot puts it, floating over the text like the stamp on paper. The signature itself is
// not checked: what is shown is the picture the file carries, not a verified seal.
//
// The DER is walked with bounds and a budget (lengths are checked against what is left, nesting and the number of
// elements are capped); a seal OFD is opened from memory with its own small limit and drawn with SkiaSharp: text with
// each character at its place and rotation (CTM), paths (lines, curves, arcs) stroked and filled, pictures.

using System.Xml.Linq;
using Filee.Engines.Cad;
using SkiaSharp;

namespace Filee.Engines.Ofd;

internal sealed partial class OfdReader
{
    /// <summary>Signatures read per document; stamps per signature.</summary>
    private const int MaxSignatures = 64, MaxStamps = 16;

    /// <summary>Largest SignedValue read (a signature with its certificates is a few KB).</summary>
    private const long MaxSignedValueBytes = 4L * 1024 * 1024;

    /// <summary>What a seal OFD may unpack to.</summary>
    private const long MaxSealPackageBytes = 16L * 1024 * 1024;

    /// <summary>Seal drawing resolution (pixels per millimetre, about 300 dpi) and largest seal side (mm).</summary>
    private const float SealScale = 12, MaxSealSize = 200;

    /// <summary>Drawing objects drawn per seal.</summary>
    private const int MaxSealObjects = 20_000;

    /// <summary>
    /// Adds the seal of each signature (OFD.xml → Signatures.xml → Signature.xml) as a picture at its StampAnnot's
    /// boundary on the page it names. A damaged signature part leaves its seal out; the document still converts.
    /// </summary>
    private void AddSeals(XElement ofd, List<PageContent> pages)
    {
        var listPath = OfdPackage.Resolve("", Child(Child(ofd, "DocBody"), "Signatures")?.Value);
        if (Optional(listPath) is not { } list)
            return;
        var byId = pages.Where(p => p.Id is not null).GroupBy(p => p.Id!).ToDictionary(g => g.Key, g => g.First());
        foreach (var signature in Children(list, "Signature").Take(MaxSignatures))
        {
            var path = OfdPackage.Resolve(OfdPackage.DirName(listPath!), Attr(signature, "BaseLoc"));
            if (Optional(path) is not { } root)
                continue;
            var info = Child(root, "SignedInfo");
            var stamps = Children(info, "StampAnnot").Take(MaxStamps)
                .Select(s => (Page: Attr(s, "PageRef") is { } id ? byId.GetValueOrDefault(id) : null, Box: Box(Attr(s, "Boundary"))))
                .Where(s => s.Page is not null && s.Box is { Width: >= 1 and <= MaxSealSize, Height: >= 1 and <= MaxSealSize })
                .ToList();
            if (stamps.Count == 0)
                continue;
            var valuePath = OfdPackage.Resolve(OfdPackage.DirName(path!), Child(root, "SignedValue")?.Value);
            if (valuePath is null || _package.Read(valuePath, MaxSignedValueBytes) is not { } signed || SealFile(signed) is not { } file)
                continue;
            foreach (var (page, box) in stamps)
            {
                var (x, y, width, height) = box!.Value;
                if (x < page!.Width && y < page.Height && x + width > 0 && y + height > 0)
                    page.Pictures.Add(new Picture(null, file, x, y, width, height));
            }
        }
    }

    /// <summary>An XML part that may be missing or damaged (null then) without failing the conversion.</summary>
    private XElement? Optional(string? name)
    {
        try
        {
            return _package.LoadXml(name)?.Root;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>The seal picture of a signature as a file in the media folder; null when there is none Filee can show.</summary>
    private string? SealFile(byte[] signed)
    {
        if (SealPicture(signed) is not { } data)
            return null;
        try
        {
            return data is [0x50, 0x4B, ..] ? DrawSeal(data) : Save(data);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or System.Xml.XmlException)
        {
            return null; // a damaged seal is left out; the page still converts
        }
    }

    // ───────────────────────── DER ─────────────────────────

    /// <summary>
    /// The picture data of a seal (SES_ESPictrueInfo: SEQUENCE { type IA5String, data OCTET STRING, width, height }),
    /// the first such sequence in the signature whose type is a picture format; null when there is none.
    /// </summary>
    private static byte[]? SealPicture(byte[] der)
    {
        var budget = 20_000;
        return Find(0, der.Length, 0);

        byte[]? Find(int start, int end, int depth)
        {
            for (var pos = start; pos < end && budget-- > 0;)
            {
                if (!Header(pos, end, out var tag, out var body, out var length))
                    return null;
                if (tag is 0x30 or 0xA0 or 0xA1 or 0xA2 or 0xA3)
                {
                    if (tag == 0x30 && PictureInfo(body, body + length) is { } found)
                        return found;
                    if (depth < 12 && Find(body, body + length, depth + 1) is { } inner)
                        return inner;
                }
                pos = body + length;
            }
            return null;
        }

        byte[]? PictureInfo(int start, int end)
        {
            if (!Header(start, end, out var typeTag, out var typeBody, out var typeLength) || typeTag is not (0x16 or 0x0C or 0x13) || typeLength is < 2 or > 8)
                return null;
            var type = System.Text.Encoding.ASCII.GetString(der, typeBody, typeLength).Trim().ToLowerInvariant();
            if (type is not ("ofd" or "png" or "jpg" or "jpeg" or "gif" or "bmp" or "tif" or "tiff")
                || !Header(typeBody + typeLength, end, out var dataTag, out var dataBody, out var dataLength) || dataTag != 0x04 || dataLength == 0)
                return null;
            return der[dataBody..(dataBody + dataLength)];
        }

        // Tag, start and length of the element at pos; false when it is not well formed or runs past end.
        bool Header(int pos, int end, out int tag, out int body, out int length)
        {
            (tag, body, length) = (0, 0, 0);
            if (pos + 2 > end)
                return false;
            tag = der[pos];
            if ((tag & 0x1F) == 0x1F)
                return false; // multi-byte tags do not occur in a signature
            int first = der[pos + 1];
            body = pos + 2;
            if (first < 0x80)
            {
                length = first;
            }
            else
            {
                var count = first & 0x7F;
                if (count is 0 or > 4 || body + count > end)
                    return false;
                long value = 0;
                for (var i = 0; i < count; i++)
                    value = value << 8 | der[body + i];
                body += count;
                if (value > int.MaxValue)
                    return false;
                length = (int)value;
            }
            return length <= end - body;
        }
    }

    // ───────────────────────── Drawing a seal OFD ─────────────────────────

    /// <summary>The first page of a seal OFD drawn on a transparent PNG at its own size; null when it has none.</summary>
    private string? DrawSeal(byte[] zip)
    {
        using var package = new OfdPackage(zip, MaxSealPackageBytes);
        var reader = new OfdReader(package, _mediaFolder);
        var ofd = package.LoadXml("OFD.xml")?.Root;
        var docPath = OfdPackage.Resolve("", Child(Child(ofd, "DocBody"), "DocRoot")?.Value);
        if (ofd is null || package.LoadXml(docPath)?.Root is not { } document)
            return null;
        var docDir = OfdPackage.DirName(docPath!);
        var common = Child(document, "CommonData");
        var res = new Resources(null);
        foreach (var name in Children(common, "PublicRes").Concat(Children(common, "DocumentRes")))
            reader.LoadRes(res, OfdPackage.Resolve(docDir, name.Value), docDir);
        var pagePath = PagePath(docDir, Attr(Child(Child(document, "Pages"), "Page"), "BaseLoc"));
        if (package.LoadXml(pagePath)?.Root is not { } page)
            return null;
        var pageRes = reader.PageResources(page, pagePath!, docDir, res);
        var (width, height) = PageArea(Child(page, "Area")) ?? PageArea(Child(common, "PageArea")) ?? (0, 0);
        if (width is < 1 or > MaxSealSize || height is < 1 or > MaxSealSize)
            return null;

        using var surface = SKSurface.Create(new SKImageInfo((int)Math.Ceiling(width * SealScale), (int)Math.Ceiling(height * SealScale), SKColorType.Rgba8888, SKAlphaType.Premul));
        if (surface is null)
            return null;
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.Scale(SealScale);
        var objects = 0;
        reader.DrawContent(canvas, Child(page, "Content"), pageRes, null, 0, ref objects);

        using var image = surface.Snapshot();
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        Directory.CreateDirectory(_mediaFolder);
        var file = Path.Combine(_mediaFolder, $"ofd-{Guid.NewGuid():N}.png");
        using (var stream = File.Create(file))
            png.SaveTo(stream);
        return file;
    }

    private void DrawContent(SKCanvas canvas, XElement? container, Resources res, string? layerDraw, int depth, ref int objects)
    {
        if (container is null || depth > MaxDepth)
            return;
        foreach (var element in container.Elements())
        {
            if (Attr(element, "Visible") == "false" || ++objects > MaxSealObjects)
                continue;
            switch (element.Name.LocalName)
            {
                case "Layer":
                    DrawContent(canvas, element, res, Attr(element, "DrawParam") ?? layerDraw, depth + 1, ref objects);
                    break;
                case "PageBlock":
                    DrawContent(canvas, element, res, layerDraw, depth + 1, ref objects);
                    break;
                case "TextObject":
                    DrawText(canvas, element, res, layerDraw);
                    break;
                case "PathObject":
                    DrawPath(canvas, element, res, layerDraw);
                    break;
                case "ImageObject":
                    DrawImage(canvas, element, res);
                    break;
            }
        }
    }

    /// <summary>Moves the canvas into an object's space: its boundary's corner, then its CTM.</summary>
    private static void Enter(SKCanvas canvas, XElement element)
    {
        var box = Box(Attr(element, "Boundary")) ?? default;
        var ctm = Matrix.Parse(Attr(element, "CTM"));
        canvas.Save();
        canvas.Translate((float)box.X, (float)box.Y);
        var matrix = new SKMatrix((float)ctm.A, (float)ctm.C, (float)ctm.E, (float)ctm.B, (float)ctm.D, (float)ctm.F, 0, 0, 1);
        canvas.Concat(in matrix);
    }

    private static SKColor? SkColor(string? hex) => hex is null ? null : SKColor.TryParse(hex, out var color) ? color : null;

    private static void DrawText(SKCanvas canvas, XElement element, Resources res, string? layerDraw)
    {
        var size = Number(Attr(element, "Size")) is { } s and > 0 and < 1000 ? s : DefaultSize;
        var info = Attr(element, "Font") is { } fontId ? res.Font(fontId) : null;
        var own = res.Draw(Attr(element, "DrawParam"));
        var layer = res.Draw(layerDraw);
        var color = SkColor(Color(Attr(element, "FillColor")) ?? ColorOf(Child(element, "FillColor")) ?? own.Fill ?? layer.Fill) ?? SKColors.Black;
        var primary = CadFonts.Resolve(FontFamily(info?.Name), Number(Attr(element, "Weight")) >= 600 || info?.Bold == true, info?.Italic == true);
        using var paint = new SKPaint { Color = color, IsAntialias = true };

        Enter(canvas, element);
        double penX = 0, penY = 0;
        foreach (var code in Children(element, "TextCode"))
        {
            if (Glyphs(code, size, ref penX, ref penY) is not { } glyphs)
                continue;
            var (text, xs, ys, _) = glyphs;
            for (var i = 0; i < text.Length; i++)
            {
                if (char.IsWhiteSpace(text[i]) || char.IsSurrogate(text[i]))
                    continue;
                var typeface = CadFonts.Layout(text[i].ToString(), primary) is [var run, ..] ? run.Typeface : primary;
                using var font = CadFonts.CreateFont(typeface, (float)size);
                canvas.DrawText(text[i].ToString(), (float)xs[i], (float)ys[i], font, paint);
            }
        }
        canvas.Restore();
    }

    private static void DrawPath(SKCanvas canvas, XElement element, Resources res, string? layerDraw)
    {
        if (Child(element, "AbbreviatedData")?.Value is not { Length: > 0 and < 1_000_000 } data)
            return;
        var own = res.Draw(Attr(element, "DrawParam"));
        var layer = res.Draw(layerDraw);
        var stroke = Attr(element, "Stroke") != "false" ? SkColor(ColorOf(Child(element, "StrokeColor")) ?? own.Stroke ?? layer.Stroke) ?? SKColors.Black : (SKColor?)null;
        var fill = Attr(element, "Fill") == "true" ? SkColor(ColorOf(Child(element, "FillColor")) ?? own.Fill ?? layer.Fill) ?? SKColors.Black : (SKColor?)null;
        var lineWidth = Number(Attr(element, "LineWidth")) ?? own.LineWidth ?? layer.LineWidth ?? 0.353;

        using var path = new SKPath();
        var tokens = data.Split([' ', '\t', '\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries);
        float At(int i) => i < tokens.Length ? (float)(Number(tokens[i]) ?? 0) : 0;
        for (var i = 0; i < tokens.Length;)
        {
            switch (tokens[i])
            {
                case "S" or "M":
                    path.MoveTo(At(i + 1), At(i + 2));
                    i += 3;
                    break;
                case "L":
                    path.LineTo(At(i + 1), At(i + 2));
                    i += 3;
                    break;
                case "Q":
                    path.QuadTo(At(i + 1), At(i + 2), At(i + 3), At(i + 4));
                    i += 5;
                    break;
                case "B":
                    path.CubicTo(At(i + 1), At(i + 2), At(i + 3), At(i + 4), At(i + 5), At(i + 6));
                    i += 7;
                    break;
                case "A":
                    path.ArcTo(At(i + 1), At(i + 2), At(i + 3), At(i + 4) != 0 ? SKPathArcSize.Large : SKPathArcSize.Small,
                        At(i + 5) != 0 ? SKPathDirection.Clockwise : SKPathDirection.CounterClockwise, At(i + 6), At(i + 7));
                    i += 8;
                    break;
                case "C":
                    path.Close();
                    i++;
                    break;
                default:
                    i++;
                    break;
            }
        }

        Enter(canvas, element);
        if (fill is { } fillColor)
        {
            using var paint = new SKPaint { Color = fillColor, IsAntialias = true, Style = SKPaintStyle.Fill };
            canvas.DrawPath(path, paint);
        }
        if (stroke is { } strokeColor)
        {
            using var paint = new SKPaint { Color = strokeColor, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = (float)Math.Clamp(lineWidth, 0.05, 20) };
            canvas.DrawPath(path, paint);
        }
        canvas.Restore();
    }

    /// <summary>A picture of the seal (PNG, JPEG, GIF, BMP), filling its unit square in the object's space.</summary>
    private void DrawImage(SKCanvas canvas, XElement element, Resources res)
    {
        if (Attr(element, "ResourceID") is not { } id || res.Media(id) is not { } part
            || _package.Read(part, OfdPackage.MaxPictureBytes) is not { } bytes)
            return;
        using var codec = SKCodec.Create(new MemoryStream(bytes));
        if (codec is null || (long)codec.Info.Width * codec.Info.Height > MaxPicturePixels)
            return;
        using var bitmap = SKBitmap.Decode(codec);
        if (bitmap is null)
            return;
        Enter(canvas, element);
        canvas.DrawBitmap(bitmap, new SKRect(0, 0, 1, 1));
        canvas.Restore();
    }
}
