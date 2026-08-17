using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Unlose.Core;

/// <summary>
/// Caller host-process detection: CLI/MCP processes are launched by an agent host (or via its shell wrapper);
/// walk up the parent process chain, skipping shell processes, and take the nearest real host name (e.g. "kimi.exe")
/// as the snapshot source identifier. Falls back to the caller-supplied value when detection fails.
/// </summary>
public static class ProcessAncestry
{
    /// <summary>Gets the host process display name (e.g. "kimi.exe"); for agents hosted by runtimes such as node
    /// (deepcode-cli etc.), resolves the canonical name from the command line (e.g. "deepcode.exe");
    /// returns <paramref name="fallback"/> when detection fails.</summary>
    public static string ResolveCallerName(string fallback)
    {
        try
        {
            var ancestor = AncestorProcess();
            if (ancestor is null) return fallback;
            var (pid, name) = ancestor.Value;

            // node-hosted form (node.exe running deepcode-cli etc.): look up the ancestor command line → AgentRegistry resolves the canonical name
            if (name.Equals("node.exe", StringComparison.OrdinalIgnoreCase))
            {
                var resolved = Agents.AgentRegistry.ResolveProcessNameByCommandLine(GetCommandLine(pid));
                if (resolved is not null) return resolved;
            }
            return name;
        }
        catch
        {
            return fallback;
        }
    }

    private static string? GetCommandLine(int pid)
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                $"SELECT CommandLine FROM Win32_Process WHERE ProcessId={pid}");
            foreach (var obj in searcher.Get())
                if (obj["CommandLine"] is string cl && !string.IsNullOrWhiteSpace(cl))
                    return cl;
        }
        catch { /* fall back to the process name when WMI is unavailable */ }
        return null;
    }

    // When launched via a shell wrapper, skip shell processes on the way up (Git Bash etc. can produce multiple shim levels); at most 6 hops
    private static readonly HashSet<string> ShellHosts = new(StringComparer.OrdinalIgnoreCase)
        { "cmd", "powershell", "pwsh", "conhost", "bash", "sh", "zsh", "OpenConsole", "WindowsTerminal", "explorer" };

    private static (int Pid, string Name)? AncestorProcess()
    {
        var pid = Environment.ProcessId;
        for (var hop = 0; hop < 6; hop++)
        {
            var ppid = GetParentPid(pid);
            if (ppid <= 0) return null;
            using var proc = Process.GetProcessById(ppid);
            if (!ShellHosts.Contains(proc.ProcessName))
                return (ppid, proc.ProcessName + ".exe");
            pid = ppid;
        }
        return null;
    }

    private static int GetParentPid(int pid)
    {
        using var proc = Process.GetProcessById(pid);
        var pbi = new PROCESS_BASIC_INFORMATION();
        var status = NtQueryInformationProcess(proc.Handle, 0, ref pbi,
            Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _);
        return status == 0 ? pbi.InheritedFromUniqueProcessId.ToInt32() : 0;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr processHandle, int processInformationClass,
        ref PROCESS_BASIC_INFORMATION processInformation, int processInformationLength, out int returnLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2_0;
        public IntPtr Reserved2_1;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }
}
