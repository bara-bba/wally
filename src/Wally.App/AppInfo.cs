using System.Reflection;

namespace Wally.App;

/// <summary>
/// Runtime access to the application's version, read from the assembly that
/// MSBuild stamps at build time (the &lt;Version&gt; in Wally.App.csproj, or a
/// -p:Version override passed by build-dist.ps1).
/// </summary>
internal static class AppInfo
{
    /// <summary>
    /// Semantic version string, e.g. "0.2.0". Falls back to "0.0.0" if the
    /// assembly carries no version metadata (should not happen in a real build).
    /// </summary>
    public static string Version { get; } = ResolveVersion();

    /// <summary>Display name with version, e.g. "wally 0.2.0".</summary>
    public static string NameWithVersion => $"wally {Version}";

    private static string ResolveVersion()
    {
        var asm = Assembly.GetExecutingAssembly();

        // Prefer the informational version (honours full SemVer incl. suffixes).
        var info = asm
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            // Strip any build-metadata suffix the SDK appends (e.g. "+<sha>").
            var plus = info.IndexOf('+');
            return plus >= 0 ? info[..plus] : info;
        }

        var ver = asm.GetName().Version;
        if (ver is not null)
        {
            // Drop a trailing ".0" revision for a clean x.y.z display.
            return ver.Revision == 0
                ? $"{ver.Major}.{ver.Minor}.{ver.Build}"
                : ver.ToString();
        }

        return "0.0.0";
    }
}
