// Generates src/Filee.App/Assets/Icons/filee.ico and filee.png (the donut logo).
// Run from the repository root:  dotnet run build/tools/make-icon.cs
#:package Magick.NET-Q8-AnyCPU

using System.Globalization;
using System.Text;
using ImageMagick;

const int Size = 512;
const double C = Size / 2.0;
const double R2 = 236, R1 = 104;
const int Segments = 6;
string[] colors = ["#7F77DD", "#6A62C9", "#7F77DD", "#6A62C9", "#7F77DD", "#6A62C9"];
const int Highlight = 1; // this slice pops out in teal

string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
(double X, double Y) P(double r, double a) => (C + r * Math.Cos(a), C + r * Math.Sin(a));

var svg = new StringBuilder($"<svg xmlns='http://www.w3.org/2000/svg' width='{Size}' height='{Size}' viewBox='0 0 {Size} {Size}'>");
var span = 2 * Math.PI / Segments;
const double Gap = 0.07; // radians between slices
for (var i = 0; i < Segments; i++)
{
    var mid = -Math.PI / 2 + i * span;
    var s = mid - span / 2 + Gap / 2;
    var e = mid + span / 2 - Gap / 2;
    var push = i == Highlight ? 16 : 0;
    var (dx, dy) = (Math.Cos(mid) * push, Math.Sin(mid) * push);
    var r2 = i == Highlight ? R2 - 14 : R2 - 20;
    var (x1, y1) = P(r2, s); var (x2, y2) = P(r2, e);
    var (x3, y3) = P(R1, e); var (x4, y4) = P(R1, s);
    var fill = i == Highlight ? "#1D9E75" : colors[i];
    svg.Append($"<path transform='translate({F(dx)},{F(dy)})' fill='{fill}' d='M{F(x1)} {F(y1)} A{F(r2)} {F(r2)} 0 0 1 {F(x2)} {F(y2)} L{F(x3)} {F(y3)} A{R1} {R1} 0 0 0 {F(x4)} {F(y4)} Z'/>");
}
// Centre: a small "file" glyph.
svg.Append($"<path fill='#3C3489' d='M{F(C - 34)} {F(C - 44)} h44 l24 24 v64 h-68 z'/>");
svg.Append($"<path fill='#AFA9EC' d='M{F(C + 10)} {F(C - 44)} v24 h24 z'/>");
svg.Append("</svg>");

var outDir = Path.Combine("src", "Filee.App", "Assets", "Icons");
Directory.CreateDirectory(outDir);
File.WriteAllText(Path.Combine(outDir, "filee.svg"), svg.ToString());

var settings = new MagickReadSettings { BackgroundColor = MagickColors.Transparent, Format = MagickFormat.Svg };
using var master = new MagickImage(Encoding.UTF8.GetBytes(svg.ToString()), settings);
master.Write(Path.Combine(outDir, "filee.png"), MagickFormat.Png);

// One image + "auto-resize" makes ImageMagick embed every size into a single .ico file.
master.Settings.SetDefine(MagickFormat.Icon, "auto-resize", "256,64,48,32,24,16");
master.Write(Path.Combine(outDir, "filee.ico"), MagickFormat.Icon);
Console.WriteLine($"Icons written to {outDir}");
