using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace LocalLLM.Gateway;

/// <summary>P/Invoke Win32 disponible para usuarios estándar (sin privilegios elevados).</summary>
internal static class NativeMethods
{
    // ---------- Memoria física ----------
    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    public static MEMORYSTATUSEX GetMemoryStatus()
    {
        var s = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref s))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return s;
    }

    // ---------- Job Objects (JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE no requiere Admin) ----------
    public const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
    public const int JobObjectExtendedLimitInformation = 9;

    [StructLayout(LayoutKind.Sequential)]
    public struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint cbInfo);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr hObject);
}

/// <summary>
/// Job Object compartido: todo proceso hijo (ollama.exe, nvidia-smi, git, uv, dotnet) se asigna aquí;
/// al cerrarse el Gateway (incluso por crash) Windows mata el árbol completo.
/// </summary>
public sealed class ChildProcessJob : IDisposable
{
    public static ChildProcessJob Instance { get; } = new();

    private IntPtr _handle;

    private ChildProcessJob()
    {
        _handle = NativeMethods.CreateJobObject(IntPtr.Zero, null);
        if (_handle == IntPtr.Zero)
        {
            Log.Warn($"CreateJobObject falló (Win32={Marshal.GetLastWin32Error()}); los hijos no se limpiarán automáticamente.");
            return;
        }
        var info = new NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = { LimitFlags = NativeMethods.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE }
        };
        if (!NativeMethods.SetInformationJobObject(_handle, NativeMethods.JobObjectExtendedLimitInformation, ref info,
                (uint)Marshal.SizeOf<NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
            Log.Warn($"SetInformationJobObject falló (Win32={Marshal.GetLastWin32Error()}).");
    }

    public void Assign(Process p)
    {
        if (_handle == IntPtr.Zero) return;
        try
        {
            if (!NativeMethods.AssignProcessToJobObject(_handle, p.Handle))
                Log.Warn($"AssignProcessToJobObject({p.Id}) falló (Win32={Marshal.GetLastWin32Error()}).");
        }
        catch (Exception ex) { Log.Warn($"No se pudo asignar PID al Job Object: {ex.Message}"); }
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero) { NativeMethods.CloseHandle(_handle); _handle = IntPtr.Zero; }
    }
}

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;
}

/// <summary>Ejecuta procesos hijos ocultos, con entorno inyectado solo en el bloque del proceso hijo.</summary>
public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> args,
        string? workingDirectory = null,
        IDictionary<string, string>? env = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory ?? PortablePaths.Root
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        PortableEnvironment.Apply(psi.Environment);
        if (env != null) foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;

        using var p = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (stdout) stdout.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (stderr) stderr.AppendLine(e.Data); };

        p.Start();
        ChildProcessJob.Instance.Assign(p);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout.HasValue) cts.CancelAfter(timeout.Value);
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"'{Path.GetFileName(fileName)} {string.Join(' ', psi.ArgumentList)}' excedió el tiempo límite.");
        }
        p.WaitForExit(); // vacía los buffers asíncronos
        return new ProcessResult(p.ExitCode, stdout.ToString(), stderr.ToString());
    }
}

/// <summary>
/// Variables de entorno portables. Se inyectan SOLO en ProcessStartInfo.Environment de cada hijo:
/// nunca se modifican variables de usuario ni de máquina.
/// </summary>
public static class PortableEnvironment
{
    public static void Apply(IDictionary<string, string?> env)
    {
        env["OLLAMA_MODELS"] = PortablePaths.Models;
        env["OLLAMA_TMPDIR"] = PortablePaths.Tmp;
        env["UV_CACHE_DIR"] = PortablePaths.UvCache;
        env["UV_PYTHON_INSTALL_DIR"] = PortablePaths.PythonDir;
        env["UV_PROJECT_ENVIRONMENT"] = PortablePaths.Venv;
        env["UV_NO_MODIFY_PATH"] = "1";
        env["UV_PYTHON_PREFERENCE"] = "only-managed";
        env["HF_HOME"] = PortablePaths.HfHome;
        env["TEMP"] = PortablePaths.Tmp;
        env["TMP"] = PortablePaths.Tmp;
        env["DOTNET_CLI_HOME"] = PortablePaths.DotnetHome;
        env["NUGET_PACKAGES"] = PortablePaths.NuGet;
        env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        env["DOTNET_NOLOGO"] = "1";
        env["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        env["GIT_TERMINAL_PROMPT"] = "0";

        // Anteponer herramientas portables al PATH del proceso hijo únicamente.
        var extra = new[] { PortablePaths.UvDir, PortablePaths.OllamaDir, Path.Combine(PortablePaths.GitDir, "cmd"), PortablePaths.DotnetDir }
            .Where(Directory.Exists);
        var current = env.TryGetValue("PATH", out var p) ? p : Environment.GetEnvironmentVariable("PATH");
        env["PATH"] = string.Join(Path.PathSeparator, extra.Append(current ?? ""));
    }
}
