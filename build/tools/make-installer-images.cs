// Generates the installer's wizard images from the logo (src/Filee.App/Assets/Icons/filee.svg, see make-icon.cs):
// installer/wizard-light.png and installer/wizard-dark.png, shown on the left of the first and last wizard page.
// Run from the repository root:  dotnet run build/tools/make-installer-images.cs
#:package Magick.NET-Q8-AnyCPU

using System.Text;
using System.Text.RegularExpressions;
using ImageMagick;

// Aspect ratio 164:314 (Inno Setup's image area), sized for 200 % display scaling so it stays sharp.
const int Width = 430, Height = 824;
const int LogoSize = 250;

var logo = File.ReadAllText(Path.Combine("src", "Filee.App", "Assets", "Icons", "filee.svg"));
var logoSize = double.Parse(Regex.Match(logo, "width='(\\d+)'").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
var shapes = Regex.Match(logo, "<svg[^>]*>(.*)</svg>", RegexOptions.Singleline).Groups[1].Value;

foreach (var (file, background, text) in new[] { ("wizard-light.png", "#F1F0FD", "#3C3489"), ("wizard-dark.png", "#221E4A", "#E4E2FB") })
{
    var scale = LogoSize / logoSize;
    var left = (Width - LogoSize) / 2.0;
    var top = Height * 0.38 - LogoSize / 2.0;
    var svg = $"<svg xmlns='http://www.w3.org/2000/svg' width='{Width}' height='{Height}' viewBox='0 0 {Width} {Height}'>" +
              $"<rect width='{Width}' height='{Height}' fill='{background}'/>" +
              $"<g transform='translate({left} {top}) scale({scale.ToString(System.Globalization.CultureInfo.InvariantCulture)})'>{shapes}</g>" +
              $"<text x='{Width / 2}' y='{(int)(top + LogoSize + 86)}' text-anchor='middle' font-family='Segoe UI Semibold, Segoe UI' " +
              $"font-weight='600' font-size='64' fill='{text}'>Filee</text></svg>";
    var settings = new MagickReadSettings { BackgroundColor = MagickColors.Transparent, Format = MagickFormat.Svg };
    using var image = new MagickImage(Encoding.UTF8.GetBytes(svg), settings);
    image.Strip();
    image.Write(Path.Combine("installer", file), MagickFormat.Png);
}
Console.WriteLine("Wizard images written to installer/");
