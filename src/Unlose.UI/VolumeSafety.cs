namespace Unlose.UI;

/// <summary>
/// UI-side helpers for the full-volume in-place restore gates. The authoritative checks live in
/// the service (CommandDispatcher); these only make the UI honest about them (button state and
/// the type-to-confirm token).
/// </summary>
public static class VolumeSafety
{
    /// <summary>
    /// Confirmation token for in-place full-volume restore: the volume root without trailing
    /// slash, e.g. "D:". Typing the volume letter forces the user to acknowledge WHICH volume is
    /// about to be rolled back, and avoids the IME friction of a long Chinese phrase.
    /// </summary>
    public static string VolumeToken(string? volumePath)
        => (volumePath ?? string.Empty).Trim().TrimEnd('\\', '/');

    /// <summary>Case-insensitive token match (trailing slashes tolerated); empty tokens never match.</summary>
    public static bool TokenMatches(string? input, string? volumePath)
    {
        var token = VolumeToken(volumePath);
        if (token.Length == 0) return false;
        var typed = (input ?? string.Empty).Trim().TrimEnd('\\', '/');
        return string.Equals(typed, token, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when the volume is the Windows system volume. In-place full-volume restore is refused
    /// there by the service (users must use Windows System Restore instead).
    /// </summary>
    public static bool IsSystemVolume(string? volumePath)
    {
        var token = VolumeToken(volumePath);
        if (token.Length == 0) return false;
        var sysRoot = (System.IO.Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.System)) ?? string.Empty)
            .Trim().TrimEnd('\\', '/');
        return sysRoot.Length > 0 && string.Equals(token, sysRoot, StringComparison.OrdinalIgnoreCase);
    }
}
