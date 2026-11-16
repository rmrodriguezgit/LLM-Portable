using System.Diagnostics;

namespace LocalLLM.Gateway;

public enum EngineState { Starting, Provisioning, PullingModel, Ready, Degraded, Updating, Stopped }

/// <summary>Raíz de composición y orquestación del ciclo de vida (arranque, hot-reload, auto-actualización, apagado).</summary>
public sealed class AppServices : IAsyncDisposable
{
    public GatewayConfig Config { get; private set; }
    public HardwareInfo Hardware { get; private set; } = null!;
    public PortableBootstrapper Bootstrapper { get; private set; } = null!;
    public OllamaProcessHost Ollama { get; private set; } = null!;
    public MemoryGuard Guard { get; private set; } = null!;
    public TokenTracker Tokens { get; private set; } = null!;
    public InferenceGateway Gateway { get; private set; } = null!;
    public GitAutoUpdater Updater { get; private set; } = null!;

    public EngineState State { get; private set; } = EngineState.Starting;
    public string StatusText { get; private set; } = "Iniciando...";
    public double? ProgressPercent { get; private set; }

    /// <summary>Se dispara en cualquier cambio de estado visible (para el System Tray).</summary>
    public event Action? StatusChanged;
    /// <summary>Solicita a la UI cerrar la aplicación (p. ej. tras lanzar la auto-actualización).</summary>
    public event Action? ExitRequested;

    private readonly CancellationTokenSource _cts = new();
    private FileSystemWatcher? _profilesWatcher;
    private readonly SemaphoreSlim _reloadLock = new(1, 1);

    public AppServices()
    {
        PortablePaths.EnsureDirectories();
        Config = GatewayConfig.Load();
    }

    public async Task StartAsync()
    {
        var ct = _cts.Token;
        try
        {
            // 1) Perfilado de hardware (rápido) → perfil inicial. Se repite en cada arranque (portabilidad USB).
            SetStatus(EngineState.Starting, "Perfilando hardware...");
            Hardware = await HardwareProfiler.DetectAsync(ct);
            var profile = HardwareProfiler.SelectProfile(Hardware, Config);
            var changed = HardwareProfiler.UpdateSnapshot(Hardware, profile);
            Log.Info($"[app] Hardware: {Hardware.CpuName}, {Hardware.PhysicalCores} núcleos físicos, {Hardware.TotalRamMb} MB RAM, " +
                     $"GPU: {Hardware.PrimaryGpu?.Name ?? "ninguna"} ({Hardware.PrimaryGpu?.DedicatedVramMb ?? 0} MB){(changed ? " [CAMBIO DE EQUIPO]" : "")}");
            foreach (var d in profile.Decisions) Log.Info($"[profile] {d}");

            // 2) Servicios. El Gateway escucha YA (responde 503 "starting" mientras se aprovisiona).
            Tokens = new TokenTracker();
            Guard = new MemoryGuard(Config.MemoryGuard, Hardware);
            Guard.CircuitChanged += (open, reason) => SetStatus(open ? EngineState.Degraded : EngineState.Ready,
                open ? $"Memory Guard activo: {reason}" : $"Listo · {Ollama?.Profile.Model}");
            Guard.Start();
            Ollama = new OllamaProcessHost(Config, profile);
            Ollama.PullProgress += (msg, pct) => SetStatus(EngineState.PullingModel, msg, pct);
            Bootstrapper = new PortableBootstrapper(Config);
            Bootstrapper.Progress += p => SetStatus(EngineState.Provisioning, p.Message, p.Percent);
            Updater = new GitAutoUpdater(this);
            Gateway = new InferenceGateway(this);
            await Gateway.StartAsync(ct);

            // 3) Aprovisionamiento portable (uv, ollama, git) con verificación SHA256.
            SetStatus(EngineState.Provisioning, "Verificando herramientas portables...");
            await Bootstrapper.RunAsync(ct);

            // 4) Repo Git (perfiles remotos). Si se acaba de clonar, el profiles.json del repo pasa a ser el activo.
            if (await Updater.InitializeAsync(ct))
            {
                Config = GatewayConfig.Load();
                profile = HardwareProfiler.SelectProfile(Hardware, Config);
                foreach (var d in profile.Decisions) Log.Info($"[profile:repo] {d}");
                Ollama.UpdateProfile(profile); // aún no arrancado: el entorno se construye con este perfil
                Guard.UpdateSettings(Config.MemoryGuard);
                HardwareProfiler.UpdateSnapshot(Hardware, profile);
                Gateway.Gate.Resume(profile.NumParallel);
            }

            // 5) Motor + modelo óptimo.
            SetStatus(EngineState.Starting, "Iniciando motor Ollama...");
            await Ollama.StartAsync(ct);
            SetStatus(EngineState.PullingModel, $"Verificando modelo {profile.Model}...");
            await Ollama.EnsureModelAsync(profile.Model, ct);
            await Ollama.WarmUpAsync(ct);

            Updater.StartTimer();
            WatchProfilesFile();
            SetStatus(EngineState.Ready, $"Listo · {profile.Model}");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error("[app] Error de arranque", ex);
            SetStatus(EngineState.Degraded, $"Error: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ Nivel 1: hot-reload

    public async Task ReloadProfilesAsync(CancellationToken ct)
    {
        await _reloadLock.WaitAsync(ct);
        try
        {
            var newCfg = GatewayConfig.Load(); // lanza si es inválido → el llamador revierte
            if (newCfg.Gateway.Port != Config.Gateway.Port || newCfg.Gateway.OllamaPort != Config.Gateway.OllamaPort)
                Log.Warn("[app] Cambios de puerto requieren reiniciar la aplicación; se aplicarán en el próximo arranque.");

            var newProfile = HardwareProfiler.SelectProfile(Hardware, newCfg);
            var old = Ollama.Profile;
            Config = newCfg;
            Guard.UpdateSettings(newCfg.MemoryGuard);

            bool modelChanged = !string.Equals(newProfile.Model, old.Model, StringComparison.OrdinalIgnoreCase);
            if (modelChanged)
            {
                // Descargar el nuevo modelo ANTES de drenar: los clientes siguen atendidos con el modelo actual.
                await Ollama.EnsureModelAsync(newProfile.Model, ct);
            }

            if (Ollama.RequiresRestart(newProfile))
            {
                SetStatus(EngineState.Updating, "Aplicando nuevo perfil (drenando peticiones)...");
                await Gateway.Gate.PauseAndDrainAsync(TimeSpan.FromSeconds(Config.Gateway.DrainTimeoutSeconds));
                try { await Ollama.RestartAsync(newProfile, ct); }
                finally { Gateway.Gate.Resume(newProfile.NumParallel); }
            }
            else
            {
                // Solo cambia modelo/opciones por petición: Ollama (MAX_LOADED_MODELS=1) descarga el anterior al pedir el nuevo.
                Ollama.UpdateProfile(newProfile);
            }

            if (modelChanged) await Ollama.WarmUpAsync(ct);
            HardwareProfiler.UpdateSnapshot(Hardware, newProfile);
            foreach (var d in newProfile.Decisions) Log.Info($"[profile] {d}");
            SetStatus(EngineState.Ready, $"Listo · {newProfile.Model}");
        }
        finally { _reloadLock.Release(); }
    }

    private void WatchProfilesFile()
    {
        var file = PortablePaths.ActiveProfilesFile;
        _profilesWatcher = new FileSystemWatcher(Path.GetDirectoryName(file)!, Path.GetFileName(file))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true
        };
        var debounce = new System.Timers.Timer(1500) { AutoReset = false };
        debounce.Elapsed += async (_, _) =>
        {
            if (Updater.LastResult is { } lr && DateTime.UtcNow - lr.FinishedUtc < TimeSpan.FromSeconds(5)) return; // ya recargado por git-sync
            try { await ReloadProfilesAsync(_cts.Token); Log.Info("[app] profiles.json editado localmente: recargado."); }
            catch (Exception ex) { Log.Warn($"[app] profiles.json inválido, se mantiene la configuración anterior: {ex.Message}"); }
        };
        _profilesWatcher.Changed += (_, _) => { debounce.Stop(); debounce.Start(); };
        _profilesWatcher.Created += (_, _) => { debounce.Stop(); debounce.Start(); };
    }

    // ------------------------------------------------------------------ Nivel 2: reemplazo del ejecutable

    public async Task ApplySelfUpdateAsync(string newExe, string rollbackCommit)
    {
        try
        {
            SetStatus(EngineState.Updating, "Actualizando Gateway (drenando peticiones activas)...");
            await Gateway.Gate.PauseAndDrainAsync(TimeSpan.FromSeconds(Config.Gateway.DrainTimeoutSeconds));

            var psi = new ProcessStartInfo(newExe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(newExe)! };
            foreach (var a in new[] { "--apply-update", "--parent-pid", Environment.ProcessId.ToString(), "--rollback-commit", rollbackCommit })
                psi.ArgumentList.Add(a);
            psi.Environment["LOCALLLM_ROOT"] = PortablePaths.Root;
            // El aplicador NO va en el Job Object: debe sobrevivir a la salida de este proceso.
            Process.Start(psi);
            Log.Info("[app] Aplicador de actualización lanzado; cerrando instancia actual.");
            ExitRequested?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error("[app] No se pudo lanzar la actualización", ex);
            Gateway.Gate.Resume();
            SetStatus(EngineState.Ready, $"Listo · {Ollama.Profile.Model} (actualización cancelada)");
        }
    }

    // ------------------------------------------------------------------ estado

    private void SetStatus(EngineState state, string text, double? pct = null)
    {
        State = state;
        StatusText = text;
        ProgressPercent = pct;
        StatusChanged?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        SetStatus(EngineState.Stopped, "Deteniendo...");
        _cts.Cancel();
        _profilesWatcher?.Dispose();
        if (Updater != null) await Updater.DisposeAsync();
        if (Gateway != null) await Gateway.DisposeAsync();
        if (Ollama != null) await Ollama.DisposeAsync();
        if (Guard != null) await Guard.DisposeAsync();
        if (Tokens != null) await Tokens.DisposeAsync();
        ChildProcessJob.Instance.Dispose(); // KILL_ON_JOB_CLOSE: cualquier hijo restante muere aquí
    }
}
