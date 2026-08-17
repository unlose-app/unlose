using Unlose.Service;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Unlose.Tests;

/// <summary>
/// BUG-RESTORE-002 regression guard: pins the robocopy argument contract used by VssAdapter during restore.
///
/// Background: this round of VM e2e found the original arguments /xo (skip older files) + no purge meant:
///   - files encrypted/rewritten by ransomware (newer timestamps) were not overwritten
///   - ransomware-added README_LOCKED.txt was not deleted
/// For an anti-ransomware product this is a fatal flaw — files cannot be recovered during a real attack.
///
/// These tests ensure no future change regresses to the wrong synchronization semantics.
/// </summary>
public class VssAdapterRobocopySemanticsTests
{
    [Fact]
    public void RobocopyRestoreArguments_AreTrueRollbackSemantics()
    {
        // required elements
        Assert.Contains("/e", VssAdapter.RobocopyRestoreArguments);
        Assert.Contains("/b", VssAdapter.RobocopyRestoreArguments);
        Assert.Contains("/copy:DAT", VssAdapter.RobocopyRestoreArguments);
        Assert.Contains("/r:3", VssAdapter.RobocopyRestoreArguments);
        Assert.Contains("/w:5", VssAdapter.RobocopyRestoreArguments);
        Assert.Contains("/mt:8", VssAdapter.RobocopyRestoreArguments);
    }

    /// <summary>
    /// /purge must be present — it deletes files in the target that do not exist in the source (snapshot).
    /// This is key to removing ransom notes and newly added malicious files; dropping it breaks ransomware protection.
    /// </summary>
    [Fact]
    public void RobocopyRestoreArguments_MustContainPurge_ToCleanRansomNotes()
    {
        Assert.Contains("/purge", VssAdapter.RobocopyRestoreArguments);
    }

    /// <summary>
    /// /xo (skip older files) must NOT be present — otherwise files encrypted/rewritten by ransomware (newer
    /// timestamps) would not be overwritten by the snapshot version. Verified on VM: with /xo, secret.txt could not be restored after encryption.
    /// </summary>
    [Fact]
    public void RobocopyRestoreArguments_MustNotContainXo_ToOverwriteEncryptedFiles()
    {
        Assert.DoesNotContain("/xo", VssAdapter.RobocopyRestoreArguments);
    }

    /// <summary>
    /// /copyall should not appear — ACLs are not writable when restoring across volumes to the system drive and would error.
    /// /copy:DAT (data + attributes + timestamps) is safer.
    /// </summary>
    [Fact]
    public void RobocopyRestoreArguments_MustNotUseCopyAll_ToAvoidAclErrors()
    {
        Assert.DoesNotContain("/copyall", VssAdapter.RobocopyRestoreArguments);
    }

    /// <summary>
    /// Count contract: currently 9 arguments. Update this assertion when adding/removing any, forcing reviewer attention.
    /// </summary>
    [Fact]
    public void RobocopyRestoreArguments_KnownCount()
    {
        Assert.Equal(9, VssAdapter.RobocopyRestoreArguments.Length);
    }
}
