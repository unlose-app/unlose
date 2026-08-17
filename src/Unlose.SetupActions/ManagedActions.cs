using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using WixToolset.Dtf.WindowsInstaller;

namespace Unlose.SetupActions
{
    /// <summary>
    /// In-process MSI custom actions (WiX DTF). They exist to keep the install 100% shell-free:
    /// the previous sc.exe/ping/taskkill exe custom actions flashed console windows on the user's
    /// desktop (HideTarget did not suppress them in host testing), and an exe CA is a spawned
    /// console process by definition. These actions call SCM/process APIs directly inside
    /// msiexec, so no process is spawned and nothing can flash.
    /// </summary>
    public class ManagedActions
    {
        private const string ServiceName = "unloseService";

        // Process names (image names without .exe) that may hold handles on files being replaced.
        // unlose.exe (CLI) is deliberately NOT killed: a live agent session degrades to the
        // packaged MsiRMFilesInUse dialog instead of being torn down mid-conversation.
        private static readonly string[] ResidentProcesses =
        {
            "unlose.Service",
            "unlose.UI",
            "unlose.McpServer",
        };

        /// <summary>
        /// Immediate, scheduled right before InstallValidate (the Restart Manager file-lock check).
        /// Replaces the old StopServiceBeforeValidate / WaitServiceStop / KillResidentProcesses
        /// exe chain. Never fails the install: every path returns Success (same semantics as the
        /// old Return="ignore" chain — nothing running on a fresh install is the normal case).
        /// </summary>
        [CustomAction]
        public static ActionResult PrepareUpgrade(Session session)
        {
            try
            {
                // 1. Stop the protection service gracefully via SCM. A bare process kill would be
                //    counted as a service crash and the INST-001 recovery policy would restart the
                //    service seconds later, re-locking unlose.Service.exe mid-install.
                try
                {
                    using (var controller = new ServiceController(ServiceName))
                    {
                        if (controller.Status == ServiceControllerStatus.Running ||
                            controller.Status == ServiceControllerStatus.StartPending)
                        {
                            controller.Stop();
                            controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(12));
                            session.Log("PrepareUpgrade: {0} stopped via SCM.", ServiceName);
                        }
                        else
                        {
                            session.Log("PrepareUpgrade: {0} not running (status {1}).", ServiceName, controller.Status);
                        }
                    }
                }
                catch (Exception ex)
                {
                    // No service installed (fresh install), already stopped, or stop timed out —
                    // the process backstop below handles any leftover lock holders.
                    session.Log("PrepareUpgrade: service stop skipped ({0}).", ex.Message);
                }

                // 2. Backstop: kill leftover processes that still hold handles (stopped-service
                //    race, hung UI). Same trade-off as before: a killed service host is logged as
                //    a crash, but this branch only fires when the graceful stop already failed.
                foreach (var name in ResidentProcesses)
                {
                    foreach (var process in Process.GetProcessesByName(name))
                    {
                        try
                        {
                            process.Kill();
                            session.Log("PrepareUpgrade: terminated leftover process {0} (PID {1}).", name, process.Id);
                        }
                        catch (Exception ex)
                        {
                            session.Log("PrepareUpgrade: could not terminate {0} ({1}).", name, ex.Message);
                        }
                        finally
                        {
                            process.Dispose();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                session.Log("PrepareUpgrade: unexpected failure, continuing install ({0}).", ex.Message);
            }

            return ActionResult.Success;
        }

        /// <summary>
        /// Immediate, scheduled right after InstallInitialize (the moment the engine resets the progress
        /// bar with its master tick total). The bar otherwise sits at 0% through the whole script +
        /// validate + prepare phase — users read that as "the install has not started". This action
        /// reserves a small share of the master total and advances the bar immediately (the documented
        /// ProgressAddition + ProgressReport pair from the MsiProcessMessage progress protocol).
        /// </summary>
        [CustomAction]
        public static ActionResult NudgeInitialProgress(Session session)
        {
            try
            {
                // DTF Record fields are 1-based (field 0 is the special format field): the progress
                // protocol puts the record type in field 1 and the tick count in field 2.
                // ProgressAddition first: add ticks to the expected total so the engine's later ticks
                // still fit — otherwise our addition would push the bar past 100% before InstallFinalize.
                using (var addition = new Record(2))
                {
                    addition[1] = ProgressAddition;
                    addition[2] = InitialProgressTicks;
                    session.Message(InstallMessage.Progress, addition);
                }

                // ProgressReport: move the bar right now.
                using (var report = new Record(2))
                {
                    report[1] = ProgressReport;
                    report[2] = InitialProgressTicks;
                    session.Message(InstallMessage.Progress, report);
                }

                session.Log("NudgeInitialProgress: bar advanced {0} ticks.", InitialProgressTicks);
            }
            catch (Exception ex)
            {
                session.Log("NudgeInitialProgress: skipped ({0}).", ex.Message);
            }

            return ActionResult.Success;
        }

        /// <summary>
        /// Deferred (SYSTEM, uninstall only), replacing the old `cmd.exe /c unlose.exe uninstall-cleanup`
        /// exe CA: the cleanup logic stays in the CLI, but this CA spawns it with CreateNoWindow so no
        /// console window can flash (HideTarget on the old exe CA was unreliable). Receives INSTALLFOLDER
        /// via CustomActionData; output goes to %ProgramData%\unlose\ca-cleanup.log like before.
        /// </summary>
        [CustomAction]
        public static ActionResult UninstallGuardCleanup(Session session)
        {
            try
            {
                string cli = Path.Combine(session.CustomActionData.ToString(), "unlose.exe");
                if (!File.Exists(cli))
                {
                    session.Log("UninstallGuardCleanup: {0} not found, skipping.", cli);
                    return ActionResult.Success;
                }

                string logDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "unlose");
                Directory.CreateDirectory(logDirectory);
                string logPath = Path.Combine(logDirectory, "ca-cleanup.log");

                var output = new System.Text.StringBuilder();
                var startInfo = new ProcessStartInfo
                {
                    FileName = cli,
                    Arguments = "uninstall-cleanup",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };

                using (var process = Process.Start(startInfo))
                {
                    // Async line handlers: reading both streams synchronously can deadlock when the
                    // child fills one pipe buffer while we block on the other.
                    process.OutputDataReceived += (sender, e) =>
                    {
                        if (e.Data != null) lock (output) output.AppendLine(e.Data);
                    };
                    process.ErrorDataReceived += (sender, e) =>
                    {
                        if (e.Data != null) lock (output) output.AppendLine("[stderr] " + e.Data);
                    };
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    if (!process.WaitForExit(180000))
                    {
                        session.Log("UninstallGuardCleanup: timed out, terminating cleanup process.");
                        try { process.Kill(); } catch { /* already gone */ }
                    }

                    File.WriteAllText(logPath, output.ToString());
                    session.Log("UninstallGuardCleanup: exit code {0}, log at {1}.",
                        process.HasExited ? process.ExitCode.ToString() : "n/a", logPath);
                }
            }
            catch (Exception ex)
            {
                session.Log("UninstallGuardCleanup: skipped ({0}).", ex.Message);
            }

            return ActionResult.Success;
        }

        // Progress message record types (record field 1), per the MsiProcessMessage progress protocol.
        private const int ProgressReport = 2;      // field 2 = ticks the bar should move
        private const int ProgressAddition = 3;    // field 2 = ticks added to the expected total

        // ~5% of the engine's typical master total (~600–800 ticks). Intentionally small: on upgrades
        // the RemoveExistingProducts phase consumes a comparable budget in the opposite direction, and
        // the bar must never overflow before InstallFinalize finishes.
        private const int InitialProgressTicks = 30;

        /// <summary>
        /// Deferred (SYSTEM context, after StartServices): implements the INST-001 tiered recovery
        /// policy via ChangeServiceConfig2 instead of `sc.exe failure ...` — restart after 5 s on the
        /// 1st/2nd failure, after 60 s on the 3rd, counter reset daily. Replaces the console sc.exe CA.
        /// </summary>
        [CustomAction]
        public static ActionResult ConfigureServiceFailureActions(Session session)
        {
            IntPtr scmHandle = IntPtr.Zero;
            IntPtr serviceHandle = IntPtr.Zero;
            IntPtr actionsBuffer = IntPtr.Zero;
            IntPtr infoBuffer = IntPtr.Zero;

            try
            {
                // Diagnostics: who is running this deferred CA (elevation decides whether the token
                // holds SeShutdownPrivilege at all).
                try
                {
                    var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                    var principal = new System.Security.Principal.WindowsPrincipal(identity);
                    session.Log("ConfigureServiceFailureActions: identity {0}, elevated admin: {1}.",
                        identity.Name, principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator));
                }
                catch (Exception ex)
                {
                    session.Log("ConfigureServiceFailureActions: identity probe failed ({0}).", ex.Message);
                }

                // Setting a restart/reboot failure action requires SE_SHUTDOWN_NAME *enabled* in the
                // caller's token (sc.exe enables it itself; a bare admin token has it disabled and
                // ChangeServiceConfig2W answers ERROR_ACCESS_DENIED — the v1.0.9.4 first-run failure).
                EnableShutdownPrivilege(session);

                scmHandle = OpenSCManagerW(null, null, ScManagerConnect);
                if (scmHandle == IntPtr.Zero)
                {
                    session.Log("ConfigureServiceFailureActions: OpenSCManager failed ({0}).", Marshal.GetLastWin32Error());
                    return ActionResult.Success;
                }

                serviceHandle = OpenServiceW(scmHandle, ServiceName, ServiceAllAccess);
                if (serviceHandle == IntPtr.Zero)
                {
                    // Fall back to the minimal right so a locked-down service DACL does not break the CA.
                    // Host testing (v1.0.9.5→1.0.9.6): opening with SERVICE_CHANGE_CONFIG alone succeeded,
                    // but ChangeServiceConfig2W then answered ERROR_ACCESS_DENIED anyway; SERVICE_ALL_ACCESS
                    // made the same call succeed (running as SYSTEM, policy verified via sc qfailure).
                    session.Log("ConfigureServiceFailureActions: OpenService ALL_ACCESS failed ({0}), retrying with SERVICE_CHANGE_CONFIG.", Marshal.GetLastWin32Error());
                    serviceHandle = OpenServiceW(scmHandle, ServiceName, ServiceChangeConfig);
                }
                if (serviceHandle == IntPtr.Zero)
                {
                    session.Log("ConfigureServiceFailureActions: OpenService failed ({0}).", Marshal.GetLastWin32Error());
                    return ActionResult.Success;
                }

                var actions = new[]
                {
                    new SC_ACTION { Type = ScActionRestart, Delay = 5000 },
                    new SC_ACTION { Type = ScActionRestart, Delay = 5000 },
                    new SC_ACTION { Type = ScActionRestart, Delay = 60000 },
                };

                int actionSize = Marshal.SizeOf(typeof(SC_ACTION));
                actionsBuffer = Marshal.AllocHGlobal(actionSize * actions.Length);
                for (int i = 0; i < actions.Length; i++)
                {
                    Marshal.StructureToPtr(actions[i], new IntPtr(actionsBuffer.ToInt64() + i * actionSize), false);
                }

                var failureActions = new SERVICE_FAILURE_ACTIONS
                {
                    dwResetPeriod = 86400,
                    lpRebootMsg = null,
                    lpCommand = null,
                    cActions = actions.Length,
                    lpsaActions = actionsBuffer,
                };

                infoBuffer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(SERVICE_FAILURE_ACTIONS)));
                Marshal.StructureToPtr(failureActions, infoBuffer, false);

                if (!ChangeServiceConfig2W(serviceHandle, ServiceConfigFailureActions, infoBuffer))
                {
                    session.Log("ConfigureServiceFailureActions: ChangeServiceConfig2 failed ({0}).", Marshal.GetLastWin32Error());
                }
                else
                {
                    session.Log("ConfigureServiceFailureActions: recovery policy applied (restart 5s/5s/60s, reset 86400s).");
                }
            }
            catch (Exception ex)
            {
                session.Log("ConfigureServiceFailureActions: unexpected failure, continuing install ({0}).", ex.Message);
            }
            finally
            {
                if (infoBuffer != IntPtr.Zero) Marshal.FreeHGlobal(infoBuffer);
                if (actionsBuffer != IntPtr.Zero) Marshal.FreeHGlobal(actionsBuffer);
                if (serviceHandle != IntPtr.Zero) CloseServiceHandle(serviceHandle);
                if (scmHandle != IntPtr.Zero) CloseServiceHandle(scmHandle);
            }

            return ActionResult.Success;
        }

        // ── advapi32 P/Invoke (service configuration) ──────────────────────────────────────────
        private const uint ScManagerConnect = 0x0001;
        private const uint ServiceChangeConfig = 0x0002;
        private const uint ServiceAllAccess = 0xF01FF;
        private const int ServiceConfigFailureActions = 2;
        private const int ScActionRestart = 1;
        private const uint TokenAdjustPrivileges = 0x0020;
        private const uint TokenQuery = 0x0008;
        private const uint SePrivilegeEnabled = 0x00000002;

        private static void EnableShutdownPrivilege(Session session)
        {
            IntPtr token = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(Process.GetCurrentProcess().Handle, TokenAdjustPrivileges | TokenQuery, out token))
                {
                    session.Log("ConfigureServiceFailureActions: OpenProcessToken failed ({0}).", Marshal.GetLastWin32Error());
                    return;
                }

                LUID luid;
                if (!LookupPrivilegeValueW(null, "SeShutdownPrivilege", out luid))
                {
                    session.Log("ConfigureServiceFailureActions: LookupPrivilegeValue failed ({0}).", Marshal.GetLastWin32Error());
                    return;
                }

                var privileges = new TOKEN_PRIVILEGES { PrivilegeCount = 1 };
                privileges.Privileges.Luid = luid;
                privileges.Privileges.Attributes = SePrivilegeEnabled;

                // AdjustTokenPrivileges "succeeds" (returns TRUE) even when the privilege is not
                // held by the token at all — the real answer is ERROR_NOT_ALL_ASSIGNED in GetLastError.
                if (!AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero))
                {
                    session.Log("ConfigureServiceFailureActions: AdjustTokenPrivileges failed ({0}).", Marshal.GetLastWin32Error());
                }
                else
                {
                    int adjustError = Marshal.GetLastWin32Error();
                    session.Log("ConfigureServiceFailureActions: SeShutdownPrivilege enable result {0} (1300=ERROR_NOT_ALL_ASSIGNED means the token does not hold the privilege).", adjustError);
                }
            }
            catch (Exception ex)
            {
                session.Log("ConfigureServiceFailureActions: privilege enable skipped ({0}).", ex.Message);
            }
            finally
            {
                if (token != IntPtr.Zero) CloseHandle(token);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID_AND_ATTRIBUTES
        {
            public LUID Luid;
            public uint Attributes;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TOKEN_PRIVILEGES
        {
            public uint PrivilegeCount;
            public LUID_AND_ATTRIBUTES Privileges;
        }

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool LookupPrivilegeValueW(string lpSystemName, string lpName, out LUID luid);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool AdjustTokenPrivileges(IntPtr tokenHandle, bool disableAllPrivileges,
            ref TOKEN_PRIVILEGES newState, uint bufferLength, IntPtr previousState, IntPtr returnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [StructLayout(LayoutKind.Sequential)]
        private struct SC_ACTION
        {
            public int Type;
            public int Delay; // milliseconds
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SERVICE_FAILURE_ACTIONS
        {
            public int dwResetPeriod;
            public string lpRebootMsg;
            public string lpCommand;
            public int cActions;
            public IntPtr lpsaActions;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenSCManagerW(string lpMachineName, string lpDatabaseName, uint dwDesiredAccess);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenServiceW(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool ChangeServiceConfig2W(IntPtr hService, int dwInfoLevel, IntPtr lpInfo);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool CloseServiceHandle(IntPtr hSCObject);
    }
}
