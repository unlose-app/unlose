using System.IO;
using Unlose.Core.Config;
using Xunit;

namespace Unlose.Tests;

/// <summary>
/// Tests for the first-run default-protection volume detection. DriveInfo cannot be mocked,
/// so the pure Filter() logic is tested with synthetic volume tuples.
/// </summary>
public class DefaultVolumeDetectorTests
{
    [Fact]
    public void Filter_KeepsOnlyFixedReadyNtfs()
    {
        var raw = new[]
        {
            (DriveType.Fixed,  true,  "NTFS", "C:\\"),
            (DriveType.Fixed,  true,  "NTFS", "D:\\"),
            (DriveType.Fixed,  true,  "FAT32", "F:\\"),          // VSS unsupported
            (DriveType.Removable, true, "FAT32", "E:\\"),        // removable
            (DriveType.Network,   true, "NTFS", "N:\\"),         // network
            (DriveType.CDRom,     true, "UDF",  "G:\\"),         // optical
            (DriveType.Fixed,     false, "NTFS", "H:\\"),        // not ready (no media)
            (DriveType.Fixed,     true,  "",     "I:\\")         // unknown format
        };

        var result = DefaultVolumeDetector.Filter(raw);

        Assert.Equal(new[] { "C:\\", "D:\\" }, result);
    }

    [Fact]
    public void Filter_OrdersByDriveLetter()
    {
        var raw = new[]
        {
            (DriveType.Fixed, true, "NTFS", "Z:\\"),
            (DriveType.Fixed, true, "NTFS", "A:\\"),
            (DriveType.Fixed, true, "NTFS", "M:\\")
        };

        var result = DefaultVolumeDetector.Filter(raw);

        Assert.Equal(new[] { "A:\\", "M:\\", "Z:\\" }, result);
    }

    [Fact]
    public void Filter_EmptyInput_ReturnsEmpty()
    {
        var result = DefaultVolumeDetector.Filter(Array.Empty<(DriveType, bool, string, string)>());
        Assert.Empty(result);
    }

    [Fact]
    public void Filter_IsCaseInsensitiveOnFormat()
    {
        var raw = new[] { (DriveType.Fixed, true, "ntfs", "C:\\") };
        Assert.Equal(new[] { "C:\\" }, DefaultVolumeDetector.Filter(raw));
    }
}
