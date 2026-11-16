namespace LocalLLM.Gateway;

/// <summary>
/// Todas las rutas de la solución, resueltas SIEMPRE de forma relativa a la carpeta del ejecutable
/// (AppContext.BaseDirectory). Nada fuera de esta carpeta, excepto el acceso directo opcional de
/// "Iniciar con Windows" en la carpeta Startup del usuario.
/// </summary>
public static class PortablePaths
{
    public static string Root { get; } = ResolveRoot();

    public static string Bin => Path.Combine(Root, "bin");
    public static string UvDir => Path.Combine(Bin, "uv");
    public static string OllamaDir => Path.Combine(Bin, "ollama");
    public static string GitDir => Path.Combine(Bin, "git");
    public static string DotnetDir => Path.Combine(Bin, "dotnet");
    public static string PythonDir => Path.Combine(Bin, "python");

    public static string Models => Path.Combine(Root, "models");
    public static string Env => Path.Combine(Root, "env");
    public static string Venv => Path.Combine(Env, ".venv");
    public static string Repo => Path.Combine(Root, "repo");

    public static string Data => Path.Combine(Root, "data");
    public static string Logs => Path.Combine(Data, "logs");
    public static string Cache => Path.Combine(Data, "cache");
    public static string Downloads => Path.Combine(Cache, "downloads");
    public static string Tmp => Path.Combine(Data, "tmp");
    public static string Build => Path.Combine(Data, "build");
    public static string HfHome => Path.Combine(Cache, "huggingface");
    public static string UvCache => Path.Combine(Cache, "uv");
    public static string NuGet => Path.Combine(Cache, "nuget");
    public static string DotnetHome => Path.Combine(Data, "dotnet-home");

    public static string TelemetryDb => Path.Combine(Data, "telemetry.db");
    public static string HardwareSnapshot => Path.Combine(Data, "hardware.json");
    public static string UpdateState => Path.Combine(Data, "update-state.json");
    public static string AdminTokenFile => Path.Combine(Data, "admin-token.txt");

    public static string UvExe => Path.Combine(UvDir, "uv.exe");
    public static string OllamaExe => Path.Combine(OllamaDir, "ollama.exe");
    public static string PortableGitExe => Path.Combine(GitDir, "cmd", "git.exe");
    public static string PortableDotnetExe => Path.Combine(DotnetDir, "dotnet.exe");

    public static string GatewayExe => Path.Combine(Root, "LocalLLM-Gateway.exe");

    /// <summary>profiles.json activo: el del repo Git (actualizable) o el incluido en el paquete.</summary>
    public static string ActiveProfilesFile
    {
        get
        {
            var fromRepo = Path.Combine(Repo, "profiles.json");
            return File.Exists(fromRepo) ? fromRepo : Path.Combine(Root, "profiles.json");
        }
    }

    public static void EnsureDirectories()
    {
        foreach (var d in new[] { Bin, Models, Env, Data, Logs, Cache, Downloads, Tmp, Build, HfHome, UvCache })
            Directory.CreateDirectory(d);
    }

    private static string ResolveRoot()
    {
        // Permite sobreescribir la raíz solo para el proceso (útil al aplicar una actualización desde data\build\out).
        var overrideRoot = Environment.GetEnvironmentVariable("LOCALLLM_ROOT");
        if (!string.IsNullOrWhiteSpace(overrideRoot) && Directory.Exists(overrideRoot))
            return Path.GetFullPath(overrideRoot);
        return Path.GetFullPath(AppContext.BaseDirectory);
    }
}

/// <summary>Logger mínimo a archivo (data\logs\gateway-yyyyMMdd.log), seguro para múltiples hilos.</summary>
public static class Log
{
    private static readonly object Sync = new();

    public static event Action<string>? MessageLogged;

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg, Exception? ex = null) => Write("ERROR", ex is null ? msg : $"{msg} :: {ex}");

    private static void Write(string level, string msg)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {msg}";
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(PortablePaths.Logs);
                File.AppendAllText(Path.Combine(PortablePaths.Logs, $"gateway-{DateTime.Now:yyyyMMdd}.log"), line + Environment.NewLine);
            }
        }
        catch { /* el logging nunca debe tumbar el proceso */ }
        MessageLogged?.Invoke(line);
    }
}
