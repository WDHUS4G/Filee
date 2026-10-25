using System.Reflection;

namespace Filee.Setup;

/// <summary>Facts about this Setup program.</summary>
public static class SetupInfo
{
    /// <summary>GitHub repository from Directory.Build.props (FileeRepositoryUrl).</summary>
    public static string RepositoryUrl =>
        typeof(SetupInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "RepositoryUrl")?.Value ?? "https://github.com/KnifeLemon/Filee";

    /// <summary>The website: one big download button.</summary>
    public const string WebsiteUrl = "https://filee.sh/";

    /// <summary>The version this Setup program was built as (the payload says which Filee it installs).</summary>
    public static string Version =>
        typeof(SetupInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";
}
