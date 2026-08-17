namespace Unlose.Core.Models;

/// <summary>Snapshot summary information returned by WMI queries</summary>
public class VssShadowInfo
{
    public string ShadowId { get; set; } = string.Empty;
    public string DeviceObject { get; set; } = string.Empty;
    public string VolumeName { get; set; } = string.Empty;
    public DateTime InstallDate { get; set; }
    public long SizeBytes { get; set; }
}
