using Microsoft.Extensions.Logging;
using System.Security.Cryptography;

namespace Unlose.Service;

public class DiffEngine
{
    private readonly ILogger<DiffEngine> _logger;

    public DiffEngine(ILogger<DiffEngine> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Recursively compares two directories, returning a list of relative paths
    /// (format: [+] added / [M] modified / [-] deleted)
    /// baseSnapshotPath = mount path of the base snapshot
    /// compareSnapshotPath = mount path of the snapshot to compare (or current volume path)
    /// </summary>
    public async Task<IReadOnlyList<string>> ComputeDiffAsync(
        string baseSnapshotPath,
        string compareSnapshotPath,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Computing diff: {Base} vs {Compare}",
            baseSnapshotPath, compareSnapshotPath);

        var results = new List<string>();

        // Run the I/O-heavy hash traversal on the thread pool
        await Task.Run(() =>
        {
            var baseFiles    = IndexDirectory(baseSnapshotPath);
            var compareFiles = IndexDirectory(compareSnapshotPath);

            // Added files (present only in compare)
            foreach (var rel in compareFiles.Keys.Except(baseFiles.Keys, StringComparer.OrdinalIgnoreCase))
                results.Add($"[+] {rel}");

            // Deleted files (present only in base)
            foreach (var rel in baseFiles.Keys.Except(compareFiles.Keys, StringComparer.OrdinalIgnoreCase))
                results.Add($"[-] {rel}");

            // Modified files (present in both but with different hashes)
            foreach (var rel in baseFiles.Keys.Intersect(compareFiles.Keys, StringComparer.OrdinalIgnoreCase))
            {
                if (!string.Equals(baseFiles[rel], compareFiles[rel], StringComparison.Ordinal))
                    results.Add($"[M] {rel}");
            }

            results.Sort(StringComparer.OrdinalIgnoreCase);
        }, ct);

        _logger.LogInformation("Diff complete: {Count} changes found", results.Count);
        return results.AsReadOnly();
    }

    /// <summary>
    /// Walks a directory and returns a "relative path -> SHA-256 hash" dictionary.
    /// Files without access permission are skipped (no error).
    /// </summary>
    private Dictionary<string, string> IndexDirectory(string rootPath)
    {
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(rootPath))
        {
            _logger.LogWarning("Directory not found for diff indexing: {Path}", rootPath);
            return index;
        }

        foreach (var fullPath in Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories))
        {
            try
            {
                var rel  = Path.GetRelativePath(rootPath, fullPath);
                var hash = ComputeFileHash(fullPath);
                index[rel] = hash;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                _logger.LogDebug("Skipping inaccessible file during diff: {Path}", fullPath);
            }
        }

        return index;
    }

    /// <summary>Compute a file's SHA-256 and return an uppercase hex string</summary>
    public static string ComputeFileHash(string filePath)
    {
        using var sha    = SHA256.Create();
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, bufferSize: 65536, useAsync: false);
        var hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash);
    }
}