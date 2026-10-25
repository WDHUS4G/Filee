// Where the pictures of a PDF are drawn and how large, for PdfCompressor: the content streams of the pages, of the
// forms they draw and of annotation appearances are read for "cm" (transformations) and "Do" (draw an XObject). A
// picture's largest drawn size decides how far it may be scaled down. Pictures whose drawing can't be followed (a
// content stream PDFsharp can't read) are taken as large as the page lets them be, so they are never scaled down too far.

using PdfSharp.Pdf;
using PdfSharp.Pdf.Content;
using PdfSharp.Pdf.Content.Objects;

namespace Filee.Engines.Pdf;

/// <summary>The largest size, in inches, a picture is drawn at (width and height of the picture, wherever it is turned).</summary>
internal readonly record struct PictureUse(double WidthInches, double HeightInches);

/// <summary>Finds the pictures (image XObjects) of a document and the size they are drawn at.</summary>
internal sealed class PdfPictureUses
{
    private const double PointsPerInch = 72;

    /// <summary>Forms drawing forms deeper than this are not followed (and nothing is drawn that deep in practice).</summary>
    private const int MaxDepth = 12;

    /// <summary>Forms followed per document at most, against forms drawing each other many times over.</summary>
    private const int MaxForms = 20_000;

    /// <summary>The part of a transformation that sizes things (no translation): x' = a·x + c·y, y' = b·x + d·y.</summary>
    private readonly record struct Matrix(double A, double B, double C, double D)
    {
        public static readonly Matrix Identity = new(1, 0, 0, 1);

        /// <summary>This transformation applied first, then <paramref name="outer"/> (PDF's "this × outer").</summary>
        public Matrix Then(Matrix outer) =>
            new(A * outer.A + B * outer.C, A * outer.B + B * outer.D, C * outer.A + D * outer.C, C * outer.B + D * outer.D);
    }

    private readonly Dictionary<PdfDictionary, (double Width, double Height)> _drawn = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<PdfDictionary> _found = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<PdfDictionary> _unsure = new(ReferenceEqualityComparer.Instance);
    private int _forms;

    /// <summary>Every picture in the resources of the pages, their forms and annotation appearances, with its size.</summary>
    public static IReadOnlyList<(PdfDictionary Image, PictureUse Use)> Find(PdfDocument document)
    {
        var uses = new PdfPictureUses();
        double pageWidth = 0, pageHeight = 0;
        foreach (var page in document.Pages)
        {
            pageWidth = Math.Max(pageWidth, page.Width.Point);
            pageHeight = Math.Max(pageHeight, page.Height.Point);
            var resources = page.Elements.GetDictionary("/Resources");
            uses.Collect(resources);
            CSequence? content = null;
            try
            {
                content = ContentReader.ReadContent(page);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                uses.Unsure(resources); // can't be read: its pictures are taken as large as the page
            }
            if (content is not null)
                uses.Walk(content, resources, Matrix.Identity, 0);
            uses.WalkAnnotations(page);
        }

        // Not drawn anywhere Filee could follow: as large as the largest page holds it (either way round).
        var pageWidthInches = Math.Max(pageWidth, 1) / PointsPerInch;
        var pageHeightInches = Math.Max(pageHeight, 1) / PointsPerInch;
        var result = new List<(PdfDictionary, PictureUse)>();
        foreach (var image in uses._found)
        {
            var width = Math.Max(1, image.Elements.GetInteger("/Width"));
            var height = Math.Max(1, image.Elements.GetInteger("/Height"));
            var dpi = Math.Min(Math.Max(width / pageWidthInches, height / pageHeightInches),
                Math.Max(width / pageHeightInches, height / pageWidthInches));
            var drawn = uses._drawn.TryGetValue(image, out var size) ? size : (0, 0);
            if (!uses._drawn.ContainsKey(image) || uses._unsure.Contains(image))
                drawn = (Math.Max(drawn.Width, width / dpi), Math.Max(drawn.Height, height / dpi));
            result.Add((image, new PictureUse(drawn.Width, drawn.Height)));
        }
        return result;
    }

    /// <summary>Remembers the pictures of a resource dictionary.</summary>
    private void Collect(PdfDictionary? resources)
    {
        if (resources?.Elements.GetDictionary("/XObject") is not { } xobjects)
            return;
        foreach (var key in xobjects.Elements.Keys)
        {
            if (PdfCompressor.Resolve(xobjects.Elements[key]) is PdfDictionary { Stream: not null } xobject
                && xobject.Elements.GetName("/Subtype") == "/Image")
                _found.Add(xobject);
        }
    }

    private void Unsure(PdfDictionary? resources)
    {
        if (resources?.Elements.GetDictionary("/XObject") is not { } xobjects)
            return;
        foreach (var key in xobjects.Elements.Keys)
        {
            if (PdfCompressor.Resolve(xobjects.Elements[key]) is PdfDictionary xobject)
                _unsure.Add(xobject);
        }
    }

    /// <summary>Follows q / Q / cm and records what Do draws.</summary>
    private void Walk(CSequence content, PdfDictionary? resources, Matrix start, int depth)
    {
        var saved = new Stack<Matrix>();
        var current = start;
        foreach (var item in content)
        {
            if (item is not COperator op)
                continue;
            switch (op.OpCode.OpCodeName)
            {
                case OpCodeName.q:
                    saved.Push(current);
                    break;
                case OpCodeName.Q:
                    if (saved.Count > 0)
                        current = saved.Pop();
                    break;
                case OpCodeName.cm when op.Operands.Count == 6:
                    current = new Matrix(Number(op.Operands[0]), Number(op.Operands[1]), Number(op.Operands[2]), Number(op.Operands[3])).Then(current);
                    break;
                case OpCodeName.Do when op.Operands.Count == 1 && op.Operands[0] is CName name:
                    Draw(name.Name, resources, current, depth);
                    break;
            }
        }
    }

    private void Draw(string name, PdfDictionary? resources, Matrix ctm, int depth)
    {
        if (resources?.Elements.GetDictionary("/XObject") is not { } xobjects
            || PdfCompressor.Resolve(xobjects.Elements[name]) is not PdfDictionary { Stream: not null } xobject)
            return;
        switch (xobject.Elements.GetName("/Subtype"))
        {
            case "/Image":
                // The picture fills the unit square: its sides become the transformed unit vectors.
                var width = Math.Sqrt(ctm.A * ctm.A + ctm.B * ctm.B) / PointsPerInch;
                var height = Math.Sqrt(ctm.C * ctm.C + ctm.D * ctm.D) / PointsPerInch;
                _found.Add(xobject);
                _drawn[xobject] = _drawn.TryGetValue(xobject, out var known)
                    ? (Math.Max(known.Width, width), Math.Max(known.Height, height))
                    : (width, height);
                break;
            case "/Form":
                WalkForm(xobject, resources, ctm, depth + 1);
                break;
        }
    }

    private void WalkForm(PdfDictionary form, PdfDictionary? inherited, Matrix parent, int depth)
    {
        if (depth > MaxDepth || ++_forms > MaxForms)
        {
            Unsure(form.Elements.GetDictionary("/Resources") ?? inherited);
            return;
        }
        // Old PDFs leave out a form's resources: it uses the page's then.
        var resources = form.Elements.GetDictionary("/Resources") ?? inherited;
        Collect(resources);
        CSequence content;
        try
        {
            content = ContentReader.ReadContent(form.Stream.UnfilteredValue);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Unsure(resources);
            return;
        }
        Walk(content, resources, FormMatrix(form).Then(parent), depth);
    }

    /// <summary>
    /// Annotation appearances (stamps, signatures, form fields): the form's bounding box, transformed by its matrix,
    /// is fitted into the annotation's rectangle.
    /// </summary>
    private void WalkAnnotations(PdfPage page)
    {
        if (page.Elements.GetArray("/Annots") is not { } annotations)
            return;
        foreach (var item in annotations.Elements)
        {
            if (PdfCompressor.Resolve(item) is not PdfDictionary annotation
                || annotation.Elements.GetDictionary("/AP") is not { } appearances
                || annotation.Elements.GetArray("/Rect") is not { Elements.Count: 4 } rect)
                continue;
            var rectWidth = Math.Abs(rect.Elements.GetReal(2) - rect.Elements.GetReal(0));
            var rectHeight = Math.Abs(rect.Elements.GetReal(3) - rect.Elements.GetReal(1));
            foreach (var key in new[] { "/N", "/R", "/D" })
            {
                switch (PdfCompressor.Resolve(appearances.Elements[key]))
                {
                    case PdfDictionary { Stream: not null } form:
                        WalkAppearance(form, page, rectWidth, rectHeight);
                        break;
                    case PdfDictionary states:
                        foreach (var state in states.Elements.Keys)
                        {
                            if (PdfCompressor.Resolve(states.Elements[state]) is PdfDictionary { Stream: not null } stateForm)
                                WalkAppearance(stateForm, page, rectWidth, rectHeight);
                        }
                        break;
                }
            }
        }
    }

    private void WalkAppearance(PdfDictionary form, PdfPage page, double rectWidth, double rectHeight)
    {
        var matrix = FormMatrix(form);
        double boxWidth = 0, boxHeight = 0;
        if (form.Elements.GetArray("/BBox") is { Elements.Count: 4 } box)
        {
            var w = Math.Abs(box.Elements.GetReal(2) - box.Elements.GetReal(0));
            var h = Math.Abs(box.Elements.GetReal(3) - box.Elements.GetReal(1));
            boxWidth = Math.Abs(matrix.A) * w + Math.Abs(matrix.C) * h;
            boxHeight = Math.Abs(matrix.B) * w + Math.Abs(matrix.D) * h;
        }
        var fit = boxWidth > 0 && boxHeight > 0 ? new Matrix(rectWidth / boxWidth, 0, 0, rectHeight / boxHeight) : Matrix.Identity;
        WalkForm(form, page.Elements.GetDictionary("/Resources"), fit, 1);
    }

    private static Matrix FormMatrix(PdfDictionary form) =>
        form.Elements.GetArray("/Matrix") is { Elements.Count: 6 } m
            ? new Matrix(m.Elements.GetReal(0), m.Elements.GetReal(1), m.Elements.GetReal(2), m.Elements.GetReal(3))
            : Matrix.Identity;

    private static double Number(CObject item) => item switch
    {
        CInteger integer => integer.Value,
        CReal real => real.Value,
        _ => 0,
    };
}
