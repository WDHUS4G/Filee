// Word equations (Office Math) in DOCX → HWPX, HWP and PDF (#143): every equation becomes a 한글 equation object
// with an equation script instead of its characters run together ("Jdωg-ωrefdt=..."), sized so that rhwp draws it
// unstretched, and the text after it still arrives. Formats without equations get a linear form ("(a+b)/(2c)").

using System.Xml.Linq;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Hwp;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Docx;
using Filee.Engines.Infrastructure;
using UglyToad.PdfPig;
using static Filee.Engines.Tests.DocxBuilder;
using static Filee.Engines.Tests.HwpxAssert;

namespace Filee.Engines.Tests;

public class DocxEquationTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    // ───────── OMML snippets ─────────

    private static string MR(string text, string sty = "") =>
        $"<m:r>{(sty.Length > 0 ? $"<m:rPr><m:sty m:val=\"{sty}\"/></m:rPr>" : "")}<m:t>{System.Security.SecurityElement.Escape(text)}</m:t></m:r>";

    private static string Frac(string numerator, string denominator) => $"<m:f><m:num>{numerator}</m:num><m:den>{denominator}</m:den></m:f>";

    private static string Sup(string body, string sup) => $"<m:sSup><m:e>{body}</m:e><m:sup>{sup}</m:sup></m:sSup>";

    private static string Sub(string body, string sub) => $"<m:sSub><m:e>{body}</m:e><m:sub>{sub}</m:sub></m:sSub>";

    private static string SubSup(string body, string sub, string sup) =>
        $"<m:sSubSup><m:e>{body}</m:e><m:sub>{sub}</m:sub><m:sup>{sup}</m:sup></m:sSubSup>";

    private static string Rad(string body, string degree = "") =>
        $"<m:rad><m:radPr>{(degree.Length == 0 ? "<m:degHide m:val=\"1\"/>" : "")}</m:radPr><m:deg>{degree}</m:deg><m:e>{body}</m:e></m:rad>";

    private static string Nary(string chr, string sub, string sup, string body) =>
        $"<m:nary><m:naryPr><m:chr m:val=\"{chr}\"/><m:limLoc m:val=\"undOvr\"/></m:naryPr><m:sub>{sub}</m:sub><m:sup>{sup}</m:sup><m:e>{body}</m:e></m:nary>";

    private static string Delimited(string content, string open = "", string close = "") =>
        $"<m:d><m:dPr>{(open.Length > 0 ? $"<m:begChr m:val=\"{open}\"/>" : "")}{(close.Length > 0 || open.Length > 0 ? $"<m:endChr m:val=\"{close}\"/>" : "")}</m:dPr><m:e>{content}</m:e></m:d>";

    private static string Matrix(params string[][] rows) =>
        "<m:m>" + string.Concat(rows.Select(r => "<m:mr>" + string.Concat(r.Select(c => $"<m:e>{c}</m:e>")) + "</m:mr>")) + "</m:m>";

    private static string Math(string content) => $"<m:oMath>{content}</m:oMath>";

    private static string Display(string content) => $"<m:oMathPara><m:oMath>{content}</m:oMath></m:oMathPara>";

    /// <summary>The equations of the issue and of a math class: one display equation per paragraph, then text.</summary>
    private static readonly (string Omml, string Script, string Linear)[] Samples =
    [
        (Frac(MR("a+b"), MR("2c")), "{{a + b} over {2 c}}", "(a+b)/(2c)"),
        (Sup(MR("x"), MR("2")) + MR("+") + SubSup(MR("y"), MR("i"), MR("n")), "x ^{2} + y _{i} ^{n}", "x^2+y_i^n"),
        (Rad(Sup(MR("b"), MR("2")) + MR("−4ac")) + MR("+") + Rad(MR("x"), MR("3")), "sqrt {b ^{2} - 4 ac} + root {3} of {x}", "√(b^2-4ac)+[3]√x"),
        (Nary("∑", MR("i=1"), MR("n"), Sup(MR("i"), MR("2"))), "SUM _{i = 1} ^{n} i ^{2}", "∑_(i=1)^n i^2"),
        (Nary("∫", MR("0"), MR("∞"), Sup(MR("e"), MR("−") + Sup(MR("x"), MR("2"))) + MR("dx")), "INT _{0} ^{INF} {e ^{- x ^{2}} dx}", "∫_0^∞ e^(-x^2)dx"),
        (MR("α+β=γ, Ω"), "alpha + beta = gamma , Omega", "α+β=γ,Ω"),
        (MR("A=") + Delimited(Matrix([MR("1"), MR("2")], [MR("3"), MR("4")])), "A = pmatrix {1 & 2 # 3 & 4}", "A=(1, 2; 3, 4)"),
        (Sub(MR("ω"), MR("ref")) + MR("+") + Sub(MR("x"), MR("in")) + MR("+") + Delimited(MR("t−1")),
            "omega _{ref} + x _{i n} + LEFT ( t - 1 RIGHT )", "ω_(ref)+x_(in)+(t-1)"),
        ("<m:func><m:fName><m:limLow><m:e>" + MR("lim", "p") + "</m:e><m:lim>" + MR("x→0") + "</m:lim></m:limLow></m:fName><m:e>" +
            Frac("<m:func><m:fName>" + MR("sin", "p") + "</m:fName><m:e>" + MR("x") + "</m:e></m:func>", MR("x")) + "</m:e></m:func>",
            "lim _{x rarrow 0} {{sin x} over {x}}", "lim_(x→0) (sin x)/x"),
        ("<m:acc><m:e>" + MR("a") + "</m:e></m:acc>" + MR("+") + "<m:bar><m:barPr><m:pos m:val=\"top\"/></m:barPr><m:e>" + MR("AB") + "</m:e></m:bar>",
            "hat {a} + OVERLINE {AB}", "â+overline(AB)"),
        (MR("f(x)=") + Delimited("<m:eqArr><m:e>" + MR("x,&x≥0") + "</m:e><m:e>" + MR("−x,&x<0") + "</m:e></m:eqArr>", "{"),
            "f ( x ) = cases {x , & x GEQ 0 # - x , & x < 0}", "f(x)={x, x≥0; -x, x<0"),
    ];

    private DocxBuilder SampleDocument()
    {
        var docx = new DocxBuilder();
        docx.Paragraph(P(R("Inline ") + Math(MR("E=m") + Sup(MR("c"), MR("2"))) + R(" in a sentence.")));
        foreach (var (omml, _, _) in Samples)
            docx.Paragraph(P(Display(omml)));
        docx.Paragraph(P(R("Text after the equations.")));
        return docx;
    }

    private async Task<string> ConvertAsync(DocxBuilder docx, string name, string target)
    {
        var path = docx.Save(Path.Combine(fx.NewFolder(), name + ".docx"));
        var job = await fx.ConvertAsync([path], new Preset { TargetFormat = target });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        return job.Outputs.Single();
    }

    [Fact]
    public void Omml_becomes_equation_script_and_linear_text()
    {
        foreach (var (omml, script, linear) in Samples)
        {
            var math = OmmlReader.Read(XElement.Parse($"<m:oMath {Namespaces}>{omml}</m:oMath>"));
            Assert.Equal(script, EquationScript.Script(math));
            Assert.Equal(linear.Normalize(), EquationScript.Linear(math).Normalize()); // â is a + combining circumflex
            // Filee's own fixes before rhwp renders (RhwpInput) leave the script alone.
            Assert.Equal(script, RhwpInput.NormaliseEquation(script));
        }
    }

    [Fact]
    public void Equation_boxes_follow_the_layout_of_the_script()
    {
        static MathBox Box(string omml) => EquationScript.Measure(OmmlReader.Read(XElement.Parse($"<m:oMath {Namespaces}>{omml}</m:oMath>")), 1000);

        var letter = Box(MR("x"));
        Assert.Equal(577.5, letter.Width, 6); // rhwp: 0.55 em, italic 5 % wider
        Assert.Equal((1000, 800), (letter.Height, letter.Baseline));
        var fraction = Box(Frac(MR("a"), MR("b")));
        Assert.True(fraction.Height > 2 * letter.Height && fraction.Width > letter.Width);
        Assert.True(Box(Nary("∑", MR("i=1"), MR("n"), MR("i"))).Height > fraction.Height);
        Assert.True(Box(Sup(MR("x"), MR("2"))).Width < Box(MR("x2")).Width); // the exponent is smaller
    }

    [Fact]
    public async Task Word_equations_become_hwpx_equation_objects()
    {
        var hwpx = await ConvertAsync(SampleDocument(), "equations", "hwpx");
        ValidPackage(hwpx, 0);
        var section = Xml(hwpx);

        var equations = section.Descendants(Hp + "equation").ToList();
        Assert.Equal(["E = m c ^{2}", .. Samples.Select(s => s.Script)], equations.Select(e => e.Element(Hp + "script")!.Value));
        foreach (var equation in equations)
        {
            Assert.Equal("1", (string?)equation.Element(Hp + "pos")!.Attribute("treatAsChar"));
            Assert.Equal("1000", (string?)equation.Attribute("baseUnit")); // the 10 pt of the runs
            var size = equation.Element(Hp + "sz")!;
            Assert.True((int)size.Attribute("width")! > 0 && (int)size.Attribute("height")! >= 1000);
            Assert.NotEmpty(equation.Element(Hp + "shapeComment")!.Value);
        }
        Assert.Equal("(a+b)/(2c)", equations[1].Element(Hp + "shapeComment")!.Value);

        // The inline equation stays between its words; a display equation alone in its paragraph is centred.
        var inline = ParagraphWith(section, "Inline");
        Assert.Contains(inline.Descendants(Hp + "equation"), e => e.Element(Hp + "script")!.Value == "E = m c ^{2}");
        Assert.Contains("in a sentence.", inline.Value);
        var display = equations[1].Ancestors(Hp + "p").First();
        var paraPr = HeadItem(hwpx, "paraPr", (string)display.Attribute("paraPrIDRef")!);
        Assert.Equal("CENTER", (string?)paraPr.Element(Hh + "align")!.Attribute("horizontal"));

        await ReadableByH2OrestartAsync(fx, hwpx);
        RenderWithRhwp(fx, hwpx, "docx-equations");
    }

    [Fact]
    public async Task Rhwp_draws_the_equations_unstretched_and_keeps_the_text_after_them()
    {
        var rhwp = fx.Converters.Single(c => c.Id == "rhwp");
        Assert.SkipUnless(fx.Catalog.StatusOf(rhwp).IsAvailable, "rhwp not found (pwsh build/fetch-engines.ps1 -Only rhwp)");

        var pdf = await ConvertAsync(SampleDocument(), "equations", "pdf");
        using (var document = PdfDocument.Open(pdf))
        {
            var text = string.Concat(document.GetPages().Select(p => p.Text));
            Assert.Contains("sentence", text);
            Assert.Contains("equations", text); // the paragraph after them
            foreach (var symbol in new[] { "α", "β", "γ", "Ω", "∑" })
                Assert.Contains(symbol, text);
        }

        // rhwp stretches an equation whose stored width differs from its own layout by more than 1 %.
        var hwpx = await ConvertAsync(SampleDocument(), "equations-svg", "hwpx");
        var exe = Path.Combine(EngineEnvironment.FindBundled("rhwp")!, OperatingSystem.IsWindows() ? "rhwp.exe" : "rhwp");
        var folder = Path.Combine(Path.GetDirectoryName(hwpx)!, "svg");
        await ProcessRunner.RunAsync(exe, ["export-svg", hwpx, "-o", folder], TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
        var svg = string.Concat(Directory.GetFiles(folder, "*.svg").Select(File.ReadAllText));
        Assert.Contains("∑", svg);
        Assert.DoesNotContain("scale(", svg);
    }

    [Fact]
    public async Task Rhwp_gets_room_below_equations_that_reach_under_their_line()
    {
        var hwpx = await ConvertAsync(SampleDocument(), "equations-room", "hwpx");
        var copy = RhwpInput.Prepare(hwpx, Path.GetDirectoryName(hwpx)!, _ => new InstalledFont(true, _ => true));
        Assert.NotNull(copy);
        ValidPackage(copy);
        var section = Xml(copy);
        var head = Xml(copy, "Contents/header.xml");
        int After(XElement paragraph) =>
            (int)head.Descendants(Hh + "paraPr").Single(p => (string?)p.Attribute("id") == (string?)paragraph.Attribute("paraPrIDRef"))
                .Descendants(Hc + "next").First().Attribute("value")!;

        // rhwp puts a line's baseline at 85 % of its height; a matrix has its own baseline in the middle, so 35 % of
        // it hangs below the line.
        var matrix = section.Descendants(Hp + "equation").Single(e => e.Element(Hp + "script")!.Value.Contains("pmatrix"));
        var height = (int)matrix.Element(Hp + "sz")!.Attribute("height")!;
        Assert.Equal("50", (string?)matrix.Attribute("baseLine"));
        Assert.Equal((int)System.Math.Ceiling(height * 0.35), After(matrix.Ancestors(Hp + "p").First()));
        Assert.Equal(0, After(ParagraphWith(section, "Text after")));
        Assert.Equal(0, After(ParagraphWith(section, "Inline"))); // a text line holds E = mc^2
    }

    [Fact]
    public async Task Hwp_keeps_the_equations()
    {
        var rhwp = fx.Converters.Single(c => c.Id == "rhwp");
        Assert.SkipUnless(fx.Catalog.StatusOf(rhwp).IsAvailable, "rhwp not found (pwsh build/fetch-engines.ps1 -Only rhwp)");

        var hwp = await ConvertAsync(SampleDocument(), "equations-hwp", "hwp");
        var back = Path.Combine(Path.GetDirectoryName(hwp)!, "back.hwpx");
        await rhwp.ConvertAsync(new ConversionStep(hwp, "hwp", "hwpx", new Preset(), new FixedPath(back), Path.GetDirectoryName(hwp)!),
            null, TestContext.Current.CancellationToken);
        var scripts = Xml(back).Descendants(Hp + "equation").Select(e => e.Element(Hp + "script")!.Value).ToList();
        Assert.Contains("{{a + b} over {2 c}}", scripts);
        Assert.Contains("A = pmatrix {1 & 2 # 3 & 4}", scripts);
    }

    [Fact]
    public void Formats_without_equations_get_the_linear_form()
    {
        var dir = fx.NewFolder();
        var path = SampleDocument().Save(Path.Combine(dir, "linear.docx"));
        var document = DocxReader.Read(path, Path.Combine(dir, "media"));
        var text = string.Join("\n", document.Sections.SelectMany(s => s.Blocks).OfType<HParagraph>().Select(p => HDocumentWalker.Text(p.Inlines)));
        Assert.Contains("Inline E=mc^2 in a sentence.", text);
        Assert.Contains("(a+b)/(2c)", text);
        Assert.Contains("lim_(x→0) (sin x)/x", text);
    }
}
