// The sponsors and backers shipped with a release (Assets/Sponsors/sponsors.json): shown in the app while there are
// any, and listed in every README.

using Avalonia.Headless.XUnit;
using Filee.App.Services;
using Filee.App.Views;

namespace Filee.App.Tests;

public class SupportersTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Filee.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Filee.slnx");
    }

    [Fact]
    public void The_list_reads_logos_and_names_and_skips_unnamed_entries()
    {
        var list = Supporters.Parse("""
            {
              "sponsors": [ { "name": "Acme", "url": "https://acme.example", "logo": "acme.png", "logoDark": "acme-dark.png" } ],
              "backers": [ { "name": "Kim" }, { "name": " " } ]
            }
            """);

        var acme = Assert.Single(list.Sponsors);
        Assert.Equal(new Supporter("Acme", "https://acme.example", "acme.png", "acme-dark.png"), acme);
        Assert.Equal("Kim", Assert.Single(list.Backers).Name);
        Assert.True(Supporters.Parse("""{ "sponsors": [], "backers": [] }""").IsEmpty);
    }

    [Fact]
    public void Every_listed_supporter_is_in_every_readme_and_has_its_logo()
    {
        var folder = Path.Combine(RepoRoot, "src", "Filee.App", "Assets", "Sponsors");
        var list = Supporters.Parse(File.ReadAllText(Path.Combine(folder, "sponsors.json")));

        foreach (var readme in new[] { "README.md", "README.ko.md", "README.zh-CN.md" })
        {
            var text = File.ReadAllText(Path.Combine(RepoRoot, readme));
            Assert.Contains("<!-- sponsors: src/Filee.App/Assets/Sponsors/sponsors.json -->", text);
            Assert.All(list.Sponsors.Concat(list.Backers), s => Assert.Contains(s.Name, text));
        }
        Assert.All(list.Sponsors.SelectMany(s => new[] { s.Logo, s.LogoDark }).OfType<string>(),
            logo => Assert.True(File.Exists(Path.Combine(folder, logo)), logo));
    }

    [AvaloniaFact]
    public void Nothing_shows_while_nobody_is_listed()
    {
        TestServices.EnsureInitialized("en");
        var view = new SupportersView();

        view.Show(SupporterList.Empty, _ => null);
        Assert.False(view.IsVisible);

        view.Show(new SupporterList([new Supporter("Acme", "https://acme.example", "acme.png")], [new("Kim"), new("Lee")]), _ => null);
        Assert.True(view.IsVisible);
        var acme = Assert.IsAssignableFrom<IEnumerable<SupporterItem>>(view.Logos.ItemsSource).Single();
        Assert.Null(acme.Logo); // a missing logo shows the name instead
        Assert.Equal("Kim · Lee", view.Names.Text);
    }

    [AvaloniaFact]
    public void In_the_dark_theme_a_logo_without_a_dark_version_keeps_a_light_tile()
    {
        TestServices.EnsureInitialized("en");
        var app = Avalonia.Application.Current!;
        var theme = app.RequestedThemeVariant;
        app.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        try
        {
            var view = new SupportersView();
            var read = new List<string>();
            view.Show(new SupporterList([new("Acme", null, "acme.png", "acme-dark.png"), new("Globex", null, "globex.png")], []),
                file => { read.Add(file); return new Avalonia.Media.Imaging.WriteableBitmap(new Avalonia.PixelSize(4, 2), new Avalonia.Vector(96, 96)); });

            var items = Assert.IsAssignableFrom<IEnumerable<SupporterItem>>(view.Logos.ItemsSource).ToList();
            Assert.Equal(["acme-dark.png", "globex.png"], read);
            Assert.False(items[0].LightOnly);
            Assert.True(items[1].LightOnly);
        }
        finally
        {
            app.RequestedThemeVariant = theme;
        }
    }
}
