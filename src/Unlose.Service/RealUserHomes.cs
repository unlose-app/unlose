namespace Unlose.Service;

/// <summary>
/// Real user directory enumeration: when the service runs as LocalSystem, %USERPROFILE%
/// points to systemprofile, so global memory injection and skill package deployment
/// must enumerate the real user directories under Users on the system drive.
/// Excludes public/system profile directories.
/// </summary>
internal static class RealUserHomes
{
    public static IEnumerable<string> Enumerate()
    {
        var usersRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System).Split('\\')[0],
            "Users");
        if (!Directory.Exists(usersRoot))
            yield break;

        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "Public", "Default", "Default User", "All Users", "systemprofile" };

        foreach (var homeDir in Directory.GetDirectories(usersRoot))
        {
            if (!excluded.Contains(Path.GetFileName(homeDir)))
                yield return homeDir;
        }
    }
}
