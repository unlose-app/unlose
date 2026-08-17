using Unlose.Core.Interfaces;
using System.IO.Pipes;
using Microsoft.Win32.SafeHandles;

namespace Unlose.Service;

/// <summary>
/// Placeholder implementation of IPipeSecurityHelper (always passes, no ACL constraints).
/// SEC-006 fix: marked Obsolete to produce compile warnings in production code,
/// preventing accidental injection of this implementation.
/// This class should only be used in test projects.
/// </summary>
[Obsolete("NullPipeSecurityHelper bypasses all security checks. Use PipeSecurityHelper in production. This class is for testing only.")]
public class NullPipeSecurityHelper : IPipeSecurityHelper
{
    public void ApplyAcl(PipeSecurity security)
    {
        // Placeholder: does not modify the ACL
    }

    public bool VerifyClientSignature(SafePipeHandle handle)
    {
        // Placeholder: always returns true
        return true;
    }
}
