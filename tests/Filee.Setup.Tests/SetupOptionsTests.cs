// The command line callers already use: Filee 1.x's updater ("/SILENT /NORESTART"), winget's silent switches,
// build scripts and people who scripted the Inno Setup installer.

using Filee.Setup.Installation;

namespace Filee.Setup.Tests;

public class SetupOptionsTests
{
    [Fact]
    public void The_updater_of_filee_1_x_gets_the_progress_window_only()
    {
        // UpdateService.InstallerArguments in 1.x (and still in 2.0).
        var options = SetupOptions.Parse(["/SILENT", "/NORESTART"]);
        Assert.Equal(SetupUi.Silent, options.Ui);
        Assert.False(options.SuppressMessageBoxes);
        Assert.Contains("/NORESTART", options.Ignored);
        Assert.Empty(options.Unknown);
        Assert.True(options.IsSilent);
    }

    [Fact]
    public void Package_managers_get_no_window_at_all()
    {
        var options = SetupOptions.Parse(["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-"]);
        Assert.Equal(SetupUi.VerySilent, options.Ui);
        Assert.True(options.SuppressMessageBoxes);
        Assert.False(options.CanShowMessages);
        Assert.Equal(["/NORESTART", "/SP-"], options.Ignored);

        // /VERYSILENT wins whatever the order.
        Assert.Equal(SetupUi.VerySilent, SetupOptions.Parse(["/VERYSILENT", "/SILENT"]).Ui);
        Assert.Equal(SetupUi.VerySilent, SetupOptions.Parse(["/SILENT", "/VERYSILENT"]).Ui);
        Assert.Equal(SetupUi.Wizard, SetupOptions.Parse([]).Ui);
    }

    [Fact]
    public void Every_inno_setup_switch_is_understood()
    {
        var options = SetupOptions.Parse([
            "/silent", "/SuppressMsgBoxes", "/DIR=C:\\Tools\\Filee", "/LANG=zhcn", "/TASKS=cli,desktopicon",
            "/MERGETASKS=!startup", "/NOICONS", "/CURRENTUSER", "/LOG=C:\\logs\\setup.log", "/NOCLOSEAPPLICATIONS",
            "/NORESTART", "/SP-", "/NOCANCEL", "/RESTARTEXITCODE=3", "/CLOSEAPPLICATIONS", "/RESTARTAPPLICATIONS",
            "/NORESTARTAPPLICATIONS", "/NOFORCECLOSEAPPLICATIONS", "/LOGCLOSEAPPLICATIONS", "/COMPONENTS=a", "/TYPE=full",
            "/GROUP=Filee", "/NOGROUP", "/PASSWORD=x", "/LOADINF=a.inf", "/SAVEINF=b.inf",
        ]);
        Assert.Equal(SetupUi.Silent, options.Ui);
        Assert.True(options.SuppressMessageBoxes);
        Assert.Equal(@"C:\Tools\Filee", options.Directory);
        Assert.Equal("zhcn", options.Language);
        Assert.Equal("cli,desktopicon", options.Tasks);
        Assert.Equal("!startup", options.MergeTasks);
        Assert.True(options.NoIcons);
        Assert.Equal(InstallScope.User, options.Scope);
        Assert.Equal(@"C:\logs\setup.log", options.LogFile);
        // /CLOSEAPPLICATIONS came after /NOCLOSEAPPLICATIONS.
        Assert.True(options.ForceCloseApplications);
        Assert.Empty(options.Unknown);
        Assert.Equal(15, options.Ignored.Count);
    }

    [Fact]
    public void Scope_portable_and_logging()
    {
        Assert.Equal(InstallScope.Machine, SetupOptions.Parse([]).Scope);
        Assert.Equal(InstallScope.Machine, SetupOptions.Parse(["/CURRENTUSER", "/ALLUSERS"]).Scope);
        Assert.True(SetupOptions.Parse(["/PORTABLE=1"]).Portable);
        Assert.False(SetupOptions.Parse(["/PORTABLE=0"]).Portable);
        Assert.False(SetupOptions.Parse(["/NOCLOSEAPPLICATIONS"]).ForceCloseApplications);
        // Plain /LOG: Setup logs anyway, into %TEMP%.
        Assert.Null(SetupOptions.Parse(["/LOG"]).LogFile);
    }

    [Fact]
    public void Filee_switches_and_unknown_arguments()
    {
        var options = SetupOptions.Parse(["/NOLAUNCH", "/UNINSTALL", "/SANDBOX=D:\\scratch", "/LAUNCHFILE=C:\\t\\x.launch", "/FIRSTPHASE=4242", "--weird", "/WHAT", "file.txt"]);
        Assert.Equal(4242, options.FirstPhase);
        Assert.Null(SetupOptions.Parse(["/FIRSTPHASE=-1"]).FirstPhase);
        Assert.True(options.NoLaunch);
        Assert.True(options.Uninstall);
        Assert.Equal(@"D:\scratch", options.Sandbox);
        Assert.Equal(@"C:\t\x.launch", options.LaunchFile);
        Assert.Equal(["--weird", "/WHAT", "file.txt"], options.Unknown);
    }

    [Theory]
    [InlineData("/DIR=\"C:\\Program Files\\Filee\"", @"C:\Program Files\Filee")]
    [InlineData("/DIR=C:\\Program Files\\Filee", @"C:\Program Files\Filee")]
    [InlineData("/dir=  D:\\Apps\\Filee  ", @"D:\Apps\Filee")]
    public void Folders_with_or_without_quotes(string arg, string folder) =>
        Assert.Equal(folder, SetupOptions.Parse([arg]).Directory);

    [Fact]
    public void Empty_values_are_not_taken()
    {
        var options = SetupOptions.Parse(["/DIR=", "/LANG=", "/SANDBOX="]);
        Assert.Null(options.Directory);
        Assert.Null(options.Language);
        Assert.Null(options.Sandbox);
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("/DIR=C:\\Program Files\\Filee")]
    [InlineData("C:\\folder with space\\")]
    [InlineData("say \"hi\"")]
    [InlineData("back\\\\slashes\\\"quote")]
    [InlineData("")]
    public void Arguments_survive_being_passed_on(string arg)
    {
        // The elevated Setup gets the arguments again: Windows must split the joined line into the same ones.
        var line = SetupOptions.Join(["first", arg, "last"]);
        Assert.Equal(["first", arg, "last"], SplitLikeWindows(line));
    }

    /// <summary>CommandLineToArgvW's rules (what .NET does with a new process's command line).</summary>
    private static List<string> SplitLikeWindows(string line)
    {
        var args = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        var started = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '\\')
            {
                var backslashes = 0;
                while (i < line.Length && line[i] == '\\')
                {
                    backslashes++;
                    i++;
                }
                if (i < line.Length && line[i] == '"')
                {
                    current.Append('\\', backslashes / 2);
                    if (backslashes % 2 == 1)
                        current.Append('"');
                    else
                        quoted = !quoted;
                }
                else
                {
                    current.Append('\\', backslashes);
                    i--;
                }
                started = true;
            }
            else if (c == '"')
            {
                quoted = !quoted;
                started = true;
            }
            else if (c == ' ' && !quoted)
            {
                if (started)
                    args.Add(current.ToString());
                current.Clear();
                started = false;
            }
            else
            {
                current.Append(c);
                started = true;
            }
        }
        if (started)
            args.Add(current.ToString());
        return args;
    }
}

public class SetupTasksTests
{
    [Fact]
    public void A_fresh_install_ticks_everything_but_the_desktop_icon()
    {
        var tasks = SetupTasks.FromCommandLine(null, null);
        Assert.Equal(SetupTasks.Defaults, tasks);
        Assert.Equal(@"contextmenu,contextmenu\top,startup,cli", tasks.ToInnoString());
    }

    [Fact]
    public void Tasks_lists_exactly_the_ticked_ones()
    {
        Assert.Equal(new SetupTasks(false, false, false, true, true), SetupTasks.FromCommandLine("cli,desktopicon", null));
        // A parent alone doesn't tick its children; "*" does.
        Assert.Equal(new SetupTasks(true, false, false, false, false), SetupTasks.FromCommandLine("contextmenu", null));
        Assert.Equal(new SetupTasks(true, true, false, false, false), SetupTasks.FromCommandLine("*contextmenu", null));
        // A child ticks its parent.
        Assert.Equal(new SetupTasks(true, true, false, false, false), SetupTasks.FromCommandLine(@"contextmenu\top", null));
        Assert.Equal(new SetupTasks(false, false, false, false, false), SetupTasks.FromCommandLine("", null));
    }

    [Fact]
    public void Merge_tasks_change_the_defaults()
    {
        Assert.Equal(new SetupTasks(true, true, false, true, true), SetupTasks.FromCommandLine(null, "desktopicon,!startup"));
        // Unticking a parent unticks its children.
        Assert.Equal(new SetupTasks(false, false, true, true, false), SetupTasks.FromCommandLine(null, "!contextmenu"));
        Assert.Equal(new SetupTasks(true, false, true, true, false), SetupTasks.FromCommandLine(null, @"!contextmenu\top"));
        Assert.Equal(new SetupTasks(false, false, false, true, false), SetupTasks.FromCommandLine("cli", "!desktopicon"));
    }

    [Fact]
    public void Names_are_case_insensitive_and_unknown_ones_are_reported()
    {
        Assert.Equal(new SetupTasks(false, false, true, true, false), SetupTasks.FromCommandLine("STARTUP, Cli, engines", null));
        Assert.Equal(["engines", "foo"], SetupTasks.UnknownNames("STARTUP,engines,!foo"));
    }

    [Theory]
    [InlineData(@"contextmenu,contextmenu\top,startup,cli", true, true, true, true, false)]
    [InlineData("contextmenu,startup", true, false, true, false, false)]
    [InlineData("cli,desktopicon", false, false, false, true, true)]
    [InlineData("", false, false, false, false, false)]
    public void Inno_setup_registry_values_are_read(string value, bool menu, bool top, bool startup, bool cli, bool desktop)
    {
        // "Inno Setup: Selected Tasks" as Filee 1.x's installer wrote it.
        var tasks = SetupTasks.FromInnoString(value);
        Assert.Equal(new SetupTasks(menu, top, startup, cli, desktop), tasks);
        Assert.Equal(tasks, SetupTasks.FromInnoString(tasks.ToInnoString()));
    }
}
