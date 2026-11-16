using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;

namespace LocalLLM.Gateway;

public enum UpdateLevel { None, HotReload, SelfRebuild }

public sealed record SyncResult(
    bool Success,
    string Trigger,
    UpdateLevel Level,
    string? FromCommit,
    string? ToCommit,
    List<string> ChangedFiles,
    string Message,
    DateTime FinishedUtc);

/// <summary>
/// Sincroniza repo\ con el remoto usando el git.exe portable (bin\git\cmd\git.exe) cada N minutos o bajo demanda.
///  - Nivel 1 (hot-reload): cambios en profiles.json o repo\python\ → recarga perfiles / uv sync sin cortar clientes.
///  - Nivel 2 (auto-actualización): cambios en src\ → dotnet publish en data\build\, graceful drain,
///    reemplazo atómico del .exe con File.Replace (con backup) y reinicio; si el health-check no responde
///    en 10 s se restaura el backup y se hace git reset --hard al commit anterior (ORIG_HEAD).
/// </summary>
public sealed class GitAutoUpdater : IAsyncDisposable
{
    private readonly AppServices _s;
    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public SyncResult? LastResult { get; private set; }
    public event Action<SyncResult>? Synced;

    public GitAutoUpdater(AppServices services) => _s = services;

    private GitSettings Settings => _s.Config.Git;
    private string? GitExe => _s.Bootstrapper.GitExe ?? ResolveGitExe();

    public static string? ResolveGitExe() =>
        File.Exists(PortablePaths.PortableGitExe) ? PortablePaths.PortableGitExe : PortableBootstrapper.FindOnPath("git.exe");

    private Task<ProcessResult> Git(params string[] args) =>
        ProcessRunner.RunAsync(GitExe!, new[] { "-C", PortablePaths.Repo }.Concat(args), PortablePaths.Root,
            timeout: TimeSpan.FromMinutes(5), ct: _cts.Token);

    // ------------------------------------------------------------------ inicialización

    /// <summary>Clona el repo si no existe. Devuelve true si se clonó (para recargar perfiles).</summary>
    public async Task<bool> InitializeAsync(CancellationToken ct)
    {
        if (!Settings.Enabled || string.IsNullOrWhiteSpace(Settings.Remote)) return false;
        if (GitExe is null) { Log.Warn("[git] git.exe no disponible; auto-actualización deshabilitada."); return false; }
        if (Directory.Exists(Path.Combine(PortablePaths.Repo, ".git"))) return false;

        var staging = PortablePaths.Repo + ".cloning";
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        Log.Info($"[git] Clonando {Settings.Remote} ({Settings.Branch}) en repo\\ ...");
        var r = await ProcessRunner.RunAsync(GitExe, new[]
        {
            "clone", "--branch", Settings.Branch, "--single-branch", "-c", "core.autocrlf=false", Settings.Remote, staging
        }, PortablePaths.Root, timeout: TimeSpan.FromMinutes(10), ct: ct);
        if (!r.Success) { Log.Warn($"[git] Clonado falló: {r.StdErr.Trim()}"); return false; }

        if (Directory.Exists(PortablePaths.Repo)) Directory.Delete(PortablePaths.Repo, true);
        Directory.Move(staging, PortablePaths.Repo);
        Log.Info("[git] Repositorio clonado.");
        return true;
    }

    public void StartTimer()
    {
        if (!Settings.Enabled) return;
        _loop = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(1, Settings.IntervalMinutes)));
            while (await timer.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false))
            {
                try { await SyncAsync("timer", _cts.Token); }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { Log.Error("[git] Sync periódico falló", ex); }
            }
        });
    }

    // ------------------------------------------------------------------ sincronización

    public async Task<SyncResult> SyncAsync(string trigger, CancellationToken ct)
    {
        if (!Settings.Enabled) return Done(false, trigger, UpdateLevel.None, null, null, new(), "Git deshabilitado en profiles.json.");
        if (GitExe is null) return Done(false, trigger, UpdateLevel.None, null, null, new(), "git.exe no disponible.");
        if (!await _sync.WaitAsync(0, ct)) return Done(false, trigger, UpdateLevel.None, null, null, new(), "Sincronización ya en curso.");
        try
        {
            if (!Directory.Exists(Path.Combine(PortablePaths.Repo, ".git")) && !await InitializeAsync(ct))
                return Done(false, trigger, UpdateLevel.None, null, null, new(), "No se pudo clonar el repositorio.");

            var fetch = await Git("fetch", "--prune", "origin", Settings.Branch);
            if (!fetch.Success) return Done(false, trigger, UpdateLevel.None, null, null, new(), $"git fetch falló: {fetch.StdErr.Trim()}");

            var from = (await Git("rev-parse", "HEAD")).StdOut.Trim();
            var to = (await Git("rev-parse", $"origin/{Settings.Branch}")).StdOut.Trim();
            if (from == to) return Done(true, trigger, UpdateLevel.None, from, to, new(), "Sin cambios.");

            // repo\ es gestionado: se intenta fast-forward; si diverge, se alinea con el remoto.
            var merge = await Git("merge", "--ff-only", $"origin/{Settings.Branch}");
            if (!merge.Success)
            {
                Log.Warn($"[git] ff-only falló ({merge.StdErr.Trim()}); reset --hard al remoto.");
                var reset = await Git("reset", "--hard", $"origin/{Settings.Branch}");
                if (!reset.Success) return Done(false, trigger, UpdateLevel.None, from, to, new(), $"git reset falló: {reset.StdErr.Trim()}");
            }

            var diff = await Git("diff", "--name-only", from, to);
            var files = diff.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            var level = Classify(files);
            Log.Info($"[git] {from[..7]} → {to[..7]}: {files.Count} archivos, nivel {level}.");

            return level switch
            {
                UpdateLevel.SelfRebuild when Settings.AllowSelfRebuild => await SelfRebuildAsync(trigger, from, to, files, ct),
                UpdateLevel.None => Done(true, trigger, level, from, to, files, "Cambios sin efecto en runtime (docs, etc.)."),
                _ => await HotReloadAsync(trigger, from, to, files, ct)
            };
        }
        finally { _sync.Release(); }
    }

    private static UpdateLevel Classify(List<string> files)
    {
        bool code = files.Any(f => f.StartsWith("src/", StringComparison.OrdinalIgnoreCase) ||
                                   f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
                                   f.Equals("Directory.Build.props", StringComparison.OrdinalIgnoreCase));
        if (code) return UpdateLevel.SelfRebuild;
        bool hot = files.Any(f => f.Equals("profiles.json", StringComparison.OrdinalIgnoreCase) ||
                                  f.StartsWith("python/", StringComparison.OrdinalIgnoreCase));
        return hot ? UpdateLevel.HotReload : UpdateLevel.None;
    }

    // ------------------------------------------------------------------ Nivel 1

    private async Task<SyncResult> HotReloadAsync(string trigger, string from, string to, List<string> files, CancellationToken ct)
    {
        var notes = new List<string>();
        if (files.Any(f => f.StartsWith("python/", StringComparison.OrdinalIgnoreCase)) &&
            File.Exists(Path.Combine(PortablePaths.Repo, "python", "pyproject.toml")))
        {
            var uv = await UvRunner.SyncAsync(ct);
            notes.Add(uv.Success ? "uv sync OK" : $"uv sync falló: {uv.StdErr.Trim()}");
        }

        try
        {
            await _s.ReloadProfilesAsync(ct);
            notes.Add("profiles.json recargado");
        }
        catch (Exception ex)
        {
            // profiles.json inválido: volver al commit anterior para no dejar la config rota.
            await Git("reset", "--hard", from);
            return Done(false, trigger, UpdateLevel.HotReload, from, to, files, $"profiles.json inválido, revertido a {from[..7]}: {ex.Message}");
        }

        var hook = Path.Combine(PortablePaths.Repo, "python", "hooks", "post_sync.py");
        if (File.Exists(hook) && File.Exists(PortablePaths.UvExe))
        {
            var r = await UvRunner.RunScriptAsync(Path.Combine("hooks", "post_sync.py"), new[] { from, to }, ct);
            notes.Add(r.Success ? "hook post_sync OK" : $"hook post_sync falló ({r.ExitCode})");
        }
        return Done(true, trigger, UpdateLevel.HotReload, from, to, files, string.Join("; ", notes));
    }

    // ------------------------------------------------------------------ Nivel 2

    private async Task<SyncResult> SelfRebuildAsync(string trigger, string from, string to, List<string> files, CancellationToken ct)
    {
        var outDir = Path.Combine(PortablePaths.Build, "out-" + to[..12]);
        try
        {
            var dotnet = await _s.Bootstrapper.EnsureDotnetSdkAsync(ct);
            var csproj = Path.Combine(PortablePaths.Repo, "src", "LocalLLM.Gateway", "LocalLLM.Gateway.csproj");
            if (Directory.Exists(outDir)) Directory.Delete(outDir, true);

            Log.Info($"[git] Compilando nueva versión en {outDir} ...");
            var build = await ProcessRunner.RunAsync(dotnet, new[]
            {
                "publish", csproj, "-c", "Release", "-r", "win-x64", "--self-contained", "true",
                "-p:PublishSingleFile=true", "-o", outDir, "--nologo"
            }, PortablePaths.Repo, timeout: TimeSpan.FromMinutes(20), ct: ct);

            var newExe = Path.Combine(outDir, "LocalLLM-Gateway.exe");
            if (!build.Success || !File.Exists(newExe))
            {
                File.WriteAllText(Path.Combine(PortablePaths.Logs, $"build-{to[..7]}.log"), build.StdOut + build.StdErr);
                await Git("reset", "--hard", from);
                return Done(false, trigger, UpdateLevel.SelfRebuild, from, to, files,
                    $"Compilación fallida; repo revertido a {from[..7]} (ver data\\logs\\build-{to[..7]}.log).");
            }

            // Los cambios de profiles.json del mismo commit se aplican al arrancar la nueva versión.
            var state = new UpdateState(from, to, DateTime.UtcNow, newExe);
            File.WriteAllText(PortablePaths.UpdateState, JsonSerializer.Serialize(state, GatewayConfig.JsonOptions));

            var result = Done(true, trigger, UpdateLevel.SelfRebuild, from, to, files, "Compilado; aplicando reemplazo atómico y reinicio.");
            _ = Task.Run(() => _s.ApplySelfUpdateAsync(newExe, from)); // tras responder al cliente de /git-sync
            return result;
        }
        catch (Exception ex)
        {
            Log.Error("[git] Nivel 2 falló", ex);
            await Git("reset", "--hard", from);
            return Done(false, trigger, UpdateLevel.SelfRebuild, from, to, files, $"Auto-actualización fallida y revertida: {ex.Message}");
        }
    }

    private SyncResult Done(bool ok, string trigger, UpdateLevel level, string? from, string? to, List<string> files, string msg)
    {
        var r = new SyncResult(ok, trigger, level, from, to, files, msg, DateTime.UtcNow);
        LastResult = r;
        (ok ? (Action<string>)Log.Info : Log.Warn)($"[git] {msg}");
        Synced?.Invoke(r);
        return r;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_loop != null) { try { await _loop; } catch { } }
    }
}

public sealed record UpdateState(string PreviousCommit, string NewCommit, DateTime BuiltUtc, string NewExe);

/// <summary>
/// Modo "--apply-update": lo ejecuta el .exe recién compilado (desde data\build\out-*). Espera a que el
/// proceso anterior termine (libera el .exe y el puerto), reemplaza con File.Replace (backup incluido),
/// lanza la nueva versión y verifica /healthz. Si falla en el plazo, restaura y hace git reset --hard.
/// </summary>
public static class UpdateApplier
{
    public static async Task<int> RunAsync(string[] args)
    {
        string Arg(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : ""; }

        var parentPid = int.TryParse(Arg("--parent-pid"), out var pp) ? pp : 0;
        var rollbackCommit = Arg("--rollback-commit");
        var root = PortablePaths.Root; // LOCALLLM_ROOT apunta a la carpeta portable real
        var target = PortablePaths.GatewayExe;
        var backup = Path.Combine(PortablePaths.Build, "LocalLLM-Gateway.backup.exe");
        var staged = Path.Combine(root, "LocalLLM-Gateway.new.exe"); // mismo volumen → File.Replace atómico
        var cfg = GatewayConfig.Load();

        Log.Info($"[update] Aplicando actualización. Padre={parentPid}, rollback={rollbackCommit}");
        try
        {
            if (parentPid > 0)
            {
                try { using var parent = Process.GetProcessById(parentPid); await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90)); }
                catch (ArgumentException) { /* ya terminó */ }
            }

            File.Copy(Environment.ProcessPath!, staged, overwrite: true);
            await RetryAsync(() => File.Replace(staged, target, backup, ignoreMetadataErrors: true));
            Log.Info("[update] Ejecutable reemplazado (File.Replace).");

            var started = StartGateway(target);
            if (await WaitHealthyAsync(cfg, started, TimeSpan.FromSeconds(cfg.Git.HealthCheckTimeoutSeconds)))
            {
                Log.Info($"[update] Health-check OK (PID {started.Id}). Actualización completada.");
                TryDelete(PortablePaths.UpdateState);
                return 0;
            }

            Log.Warn("[update] Health-check FALLÓ. Ejecutando rollback...");
            try { if (!started.HasExited) started.Kill(true); await started.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
            await RollbackAsync(target, backup, rollbackCommit);
            return 2;
        }
        catch (Exception ex)
        {
            Log.Error("[update] Error aplicando actualización", ex);
            await RollbackAsync(target, backup, rollbackCommit);
            return 3;
        }
    }

    private static async Task RollbackAsync(string target, string backup, string rollbackCommit)
    {
        try
        {
            if (File.Exists(backup))
            {
                var failed = Path.Combine(PortablePaths.Build, "LocalLLM-Gateway.failed.exe");
                await RetryAsync(() => File.Replace(backup, target, failed, ignoreMetadataErrors: true));
                Log.Info("[update] Ejecutable anterior restaurado.");
            }
            var git = GitAutoUpdater.ResolveGitExe();
            if (git != null && !string.IsNullOrWhiteSpace(rollbackCommit))
            {
                // Equivalente a "git reset --hard ORIG_HEAD", pero con el commit exacto guardado (ORIG_HEAD puede moverse).
                var r = await ProcessRunner.RunAsync(git, new[] { "-C", PortablePaths.Repo, "reset", "--hard", rollbackCommit });
                Log.Info($"[update] git reset --hard {rollbackCommit}: {(r.Success ? "OK" : r.StdErr.Trim())}");
            }
        }
        catch (Exception ex) { Log.Error("[update] Rollback con errores", ex); }
        finally
        {
            TryDelete(PortablePaths.UpdateState);
            if (File.Exists(target)) StartGateway(target);
        }
    }

    private static Process StartGateway(string exe)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        psi.ArgumentList.Add("--post-update");
        psi.Environment.Remove("LOCALLLM_ROOT");
        return Process.Start(psi)!;
    }

    /// <summary>Sano = el proceso nuevo responde /healthz con su propio PID (aunque Ollama aún esté cargando: 503 "starting").</summary>
    private static async Task<bool> WaitHealthyAsync(GatewayConfig cfg, Process p, TimeSpan timeout)
    {
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(2) };
        var url = $"http://127.0.0.1:{cfg.Gateway.Port}/healthz";
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (p.HasExited) return false;
            try
            {
                using var resp = await http.GetAsync(url);
                var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
                if (json.TryGetProperty("pid", out var pid) && pid.GetInt32() == p.Id) return true;
            }
            catch { }
            await Task.Delay(400);
        }
        return false;
    }

    private static async Task RetryAsync(Action a)
    {
        for (int i = 0; ; i++)
        {
            try { a(); return; }
            catch (IOException) when (i < 20) { await Task.Delay(500); } // antivirus/indexador con el archivo abierto
            catch (UnauthorizedAccessException) when (i < 20) { await Task.Delay(500); }
        }
    }

    private static void TryDelete(string f) { try { File.Delete(f); } catch { } }
}
