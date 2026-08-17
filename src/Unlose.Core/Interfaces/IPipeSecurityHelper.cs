using System.IO.Pipes;
using Microsoft.Win32.SafeHandles;

namespace Unlose.Core.Interfaces;

/// <summary>
/// Pipe ACL and client signature verification interface.
/// The concrete implementation is provided by PipeSecurityHelper in Unlose.Service; currently stubbed by NullPipeSecurityHelper.
/// </summary>
public interface IPipeSecurityHelper
{
    /// <summary>Sets an ACL on the NamedPipeServerStream (only LOCAL SYSTEM and the current user's SID may connect).</summary>
    void ApplyAcl(PipeSecurity security);

    /// <summary>Verifies the executable signature of the connected client.</summary>
    bool VerifyClientSignature(SafePipeHandle handle);
}
