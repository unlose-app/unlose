using Unlose.Core.Config;
using Unlose.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;

namespace Unlose.Service;

/// <summary>
/// Pipe ACL: SYSTEM and Administrators get full control; authenticated users get
/// ReadWrite (so the UI running as a regular local user can connect).
/// Release builds may verify the client certificate against a thumbprint whitelist,
/// or allow via the permissive policy in <see cref="UnloseConfig.Service"/>.
/// </summary>
public class PipeSecurityHelper : IPipeSecurityHelper
{
    private readonly ILogger<PipeSecurityHelper> _logger;
    private readonly string[] _trustedThumbprints;
    private readonly bool _allowAnySignedClientInProduction;
#if !DEBUG
    private static int _warnedNoWhitelist;
#endif

    public PipeSecurityHelper(UnloseConfig config, ILogger<PipeSecurityHelper> logger)
    {
        _logger = logger;
        _trustedThumbprints = (config.Service.TrustedClientThumbprints ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Replace(" ", string.Empty).ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _allowAnySignedClientInProduction = config.Service.AllowAnySignedClientInProduction;
    }

    public void ApplyAcl(PipeSecurity security)
    {
        // Remove any inherited rules first
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        // Allow only SYSTEM - full control
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        // Allow built-in Administrators - full control
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        // The desktop UI runs as a regular user, so signed-in users must be allowed to connect to the pipe; otherwise only admins could connect (SEC-001 addition)
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));
    }

    public bool VerifyClientSignature(SafePipeHandle handle)
    {
#if DEBUG
        // In debug builds, skip Authenticode check so unsigned dev executables can connect
        return true;
#else
        try
        {
            if (!GetNamedPipeClientProcessId(handle, out uint clientPid))
                return false;

            using var process = System.Diagnostics.Process.GetProcessById((int)clientPid);
            var exePath = process.MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath))
                return false;

            // Verify the Authenticode signature
            var cert = X509Certificate2.CreateFromSignedFile(exePath);
            var thumbprint = cert.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA1)
                .Replace(" ", string.Empty)
                .ToUpperInvariant();

            if (_trustedThumbprints.Length == 0)
            {
                if (_allowAnySignedClientInProduction)
                {
                    if (Interlocked.Exchange(ref _warnedNoWhitelist, 1) == 0)
                    {
                        _logger.LogWarning("TrustedClientThumbprints is empty; permissive mode enabled by AllowAnySignedClientInProduction.");
                    }
                    return true;
                }

                if (Interlocked.Exchange(ref _warnedNoWhitelist, 1) == 0)
                {
                    _logger.LogError("TrustedClientThumbprints is empty and permissive mode is disabled; rejecting signed pipe clients by default.");
                }
                return false;
            }

            if (!_trustedThumbprints.Contains(thumbprint, StringComparer.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Rejected pipe client process {Pid}: certificate thumbprint not in whitelist.", clientPid);
                return false;
            }

            return true;
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            // Client executable is unsigned (no Authenticode signature).
            // In permissive mode, allow it — the pipe ACL already restricts to AuthenticatedUsers on this machine.
            if (_allowAnySignedClientInProduction)
            {
                _logger.LogDebug("Pipe client has no Authenticode signature; permissive mode allows it. ({Msg})", ex.Message);
                return true;
            }
            _logger.LogWarning(ex, "Failed to verify pipe client signature.");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to verify pipe client signature.");
            return false;
        }
#endif
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);
}
