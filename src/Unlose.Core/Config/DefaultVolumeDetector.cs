using System.IO;

namespace Unlose.Core.Config;

/// <summary>
/// Detects the volumes that should be protected by default on first install.
/// VSS shadow copies are only supported on NTFS, and only fixed local drives make sense
/// for full-volume protection (removable/network/optical volumes are excluded).
/// The detection result is written into config.json on first run; afterwards the user's
/// explicit config always wins (this detector is not consulted again).
/// </summary>
public static class DefaultVolumeDetector
{
    /// <summary>
    /// Returns the root paths (e.g. "C:\") of every eligible volume, ordered by drive letter.
    /// </summary>
    public static string[] Detect() => Filter(GetRawVolumes());

    /// <summary>Raw volume snapshot, separated for testability (DriveInfo cannot be mocked).</summary>
    public static IEnumerable<(DriveType Type, bool Ready, string Format, string Root)> GetRawVolumes()
        => DriveInfo.GetDrives().Select(d =>
            (d.DriveType, d.IsReady, d.DriveFormat, d.RootDirectory.FullName));

    /// <summary>
    /// Eligibility filter: fixed local drives that are ready and formatted NTFS (VSS requirement).
    /// </summary>
    public static string[] Filter(IEnumerable<(DriveType Type, bool Ready, string Format, string Root)> volumes)
        => volumes
            .Where(v => v.Type == DriveType.Fixed && v.Ready)
            .Where(v => string.Equals(v.Format, "NTFS", System.StringComparison.OrdinalIgnoreCase))
            .Select(v => v.Root)
            .OrderBy(root => root, System.StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
