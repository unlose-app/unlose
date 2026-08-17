using Unlose.Service;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Unlose.Tests;

public class DiffEngineTests
{
    [Fact]
    public async Task ComputeDiffAsync_RealDirectories_ReturnsAddedModifiedDeleted()
    {
        var engine = new DiffEngine(NullLogger<DiffEngine>.Instance);
        var root = Path.Combine(Path.GetTempPath(), "Unlose_DiffTests_" + Guid.NewGuid().ToString("N"));
        var baseDir = Path.Combine(root, "base");
        var compareDir = Path.Combine(root, "compare");

        Directory.CreateDirectory(baseDir);
        Directory.CreateDirectory(compareDir);
        Directory.CreateDirectory(Path.Combine(baseDir, "sub"));
        Directory.CreateDirectory(Path.Combine(compareDir, "sub"));

        try
        {
            File.WriteAllText(Path.Combine(baseDir, "unchanged.txt"), "same");
            File.WriteAllText(Path.Combine(compareDir, "unchanged.txt"), "same");

            File.WriteAllText(Path.Combine(baseDir, "modified.txt"), "before");
            File.WriteAllText(Path.Combine(compareDir, "modified.txt"), "after");

            File.WriteAllText(Path.Combine(baseDir, "deleted.txt"), "gone");
            File.WriteAllText(Path.Combine(compareDir, "added.txt"), "new");

            File.WriteAllText(Path.Combine(baseDir, "sub", "nested-keep.txt"), "same");
            File.WriteAllText(Path.Combine(compareDir, "sub", "nested-keep.txt"), "same");
            File.WriteAllText(Path.Combine(compareDir, "sub", "nested-added.txt"), "nested");

            var result = await engine.ComputeDiffAsync(baseDir, compareDir);

            Assert.Contains("[+] added.txt", result);
            Assert.Contains("[+] sub" + Path.DirectorySeparatorChar + "nested-added.txt", result);
            Assert.Contains("[-] deleted.txt", result);
            Assert.Contains("[M] modified.txt", result);
            Assert.DoesNotContain(result, x => x.Contains("unchanged.txt", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ComputeFileHash_ValidFile_ReturnsHexString()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, "test content");
            var hash = DiffEngine.ComputeFileHash(tempFile);
            Assert.Equal(64, hash.Length); // SHA-256 hex = 64 chars
            Assert.Matches("^[0-9A-F]+$", hash);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}
