using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace LocalLLM.Gateway;

/// <summary>
/// Supervisa ollama.exe portable: lo lanza oculto con variables de entorno solo de proceso, lo asigna al
/// Job Object (KILL_ON_JOB_CLOSE), espera readiness, lo reinicia con backoff si cae y descarga el modelo
/// seleccionado (POST /api/pull) informando el progreso.
/// </summary>
public sealed class OllamaProcessHost : IAsyncDisposable
{
    private readonly GatewayConfig _config;
    private ResolvedProfile _profile;
    private Process? _process;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private Task? _supervisor;
    private volatile bool _stopping;
    private StreamWriter? _logWriter;

    public HttpClient Http { get; }
    public Uri BaseUri { get; }
    public bool IsReady { get; private set; }
    public string? Version { get; private set; }
    public ResolvedProfile Profile => _profile;

    public event Action<string, double?>? PullProgress;
    public event Action<bool>? ReadyChanged;

    public OllamaProcessHost(GatewayConfig config, ResolvedProfile profile)
    {
        _config = config;
        _profile = profile;
        BaseUri = new Uri($"http://127.0.0.1:{config.Gateway.OllamaPort}/");
        Http = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 256,
            EnableMultipleHttp2Connections = true,
            UseProxy = false // loopback directo, nunca por proxy corporativo
        })
        { BaseAddress = BaseUri, Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task StartAsync(CancellationToken ct)
    {
        await _lifecycle.WaitAsync(ct);
        try { await LaunchAsync(ct); }
        finally { _lifecycle.Release(); }
        _supervisor ??= Task.Run(() => SuperviseAsync(_cts.Token));
    }

    /// <summary>Reinicio controlado (cambio de perfil por hot-reload o cambio de hardware). El llamador drena antes.</summary>
    public async Task RestartAsync(ResolvedProfile newProfile, CancellationToken ct)
    {
        await _lifecycle.WaitAsync(ct);
        try
        {
            _profile = newProfile;
            await StopProcessAsync();
            await LaunchAsync(ct);
        }
        finally { _lifecycle.Release(); }
    }

    /// <summary>Actualiza el perfil sin reiniciar (modelo por defecto, num_gpu/num_thread por petición).</summary>
    public void UpdateProfile(ResolvedProfile p) => _profile = p;

    /// <summary>¿El cambio de perfil requiere reiniciar el proceso? (variables de entorno distintas).</summary>
    public bool RequiresRestart(ResolvedProfile p) =>
        p.NumParallel != _profile.NumParallel || p.KvCacheType != _profile.KvCacheType ||
        p.FlashAttention != _profile.FlashAttention || p.NumCtx != _profile.NumCtx;

    private Dictionary<string, string> BuildEnvironment()
    {
        var p = _profile;
        var env = new Dictionary<string, string>
        {
            ["OLLAMA_HOST"] = $"127.0.0.1:{_config.Gateway.OllamaPort}",
            ["OLLAMA_MODELS"] = PortablePaths.Models,
            ["OLLAMA_TMPDIR"] = PortablePaths.Tmp,
            ["OLLAMA_NUM_PARALLEL"] = p.NumParallel.ToString(CultureInfo.InvariantCulture),
            ["OLLAMA_MAX_LOADED_MODELS"] = "1",          // un solo modelo residente: evita desbordar VRAM
            ["OLLAMA_MAX_QUEUE"] = _config.Gateway.MaxQueuedRequests.ToString(CultureInfo.InvariantCulture),
            ["OLLAMA_CONTEXT_LENGTH"] = p.NumCtx.ToString(CultureInfo.InvariantCulture),
            ["OLLAMA_FLASH_ATTENTION"] = p.FlashAttention ? "1" : "0",
            ["OLLAMA_KV_CACHE_TYPE"] = p.FlashAttention ? p.KvCacheType : "f16",
            ["OLLAMA_KEEP_ALIVE"] = _config.Gateway.KeepAlive,
            ["OLLAMA_NOHISTORY"] = "1",
            ["OLLAMA_ORIGINS"] = "http://127.0.0.1:*,http://localhost:*"
        };
        if (p.NumThread is { } t) env["OLLAMA_NUM_THREAD"] = t.ToString(CultureInfo.InvariantCulture); // además se inyecta options.num_thread
        if (p.Target == "cpu") env["CUDA_VISIBLE_DEVICES"] = "-1";
        return env;
    }

    private async Task LaunchAsync(CancellationToken ct)
    {
        if (!File.Exists(PortablePaths.OllamaExe))
            throw new FileNotFoundException("ollama.exe no encontrado en bin\\ollama. Ejecuta el bootstrapper.", PortablePaths.OllamaExe);

        var psi = new ProcessStartInfo(PortablePaths.OllamaExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = PortablePaths.OllamaDir
        };
        psi.ArgumentList.Add("serve");
        PortableEnvironment.Apply(psi.Environment);
        foreach (var kv in BuildEnvironment()) psi.Environment[kv.Key] = kv.Value;

        _logWriter ??= new StreamWriter(new FileStream(Path.Combine(PortablePaths.Logs, "ollama.log"),
            FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) lock (_logWriter) _logWriter.WriteLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (_logWriter) _logWriter.WriteLine(e.Data); };
        proc.Start();
        ChildProcessJob.Instance.Assign(proc);
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        _process = proc;
        Log.Info($"[ollama] PID {proc.Id} iniciado en 127.0.0.1:{_config.Gateway.OllamaPort} " +
                 $"(parallel={_profile.NumParallel}, ctx={_profile.NumCtx}, kv={_profile.KvCacheType}, fa={_profile.FlashAttention})");

        await WaitReadyAsync(TimeSpan.FromSeconds(60), ct);
    }

    private async Task WaitReadyAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (_process is { HasExited: true })
                throw new InvalidOperationException($"ollama.exe terminó durante el arranque (código {_process.ExitCode}). Revisa data\\logs\\ollama.log.");
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(2));
                var v = await Http.GetFromJsonAsync<JsonElement>("api/version", cts.Token);
                Version = v.TryGetProperty("version", out var ver) ? ver.GetString() : null;
                SetReady(true);
                Log.Info($"[ollama] Listo (versión {Version}).");
                return;
            }
            catch (Exception) when (!ct.IsCancellationRequested) { await Task.Delay(300, ct); }
        }
        throw new TimeoutException("ollama.exe no respondió en 60 s.");
    }

    private async Task SuperviseAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var p = _process;
                if (p is null) { await Task.Delay(500, ct); continue; }
                await p.WaitForExitAsync(ct);
                if (_stopping || ct.IsCancellationRequested) return;
                // Reinicio controlado en curso o ya reemplazado por otro proceso: no es un crash.
                if (_lifecycle.CurrentCount == 0 || !ReferenceEquals(p, _process)) { await Task.Delay(500, ct); continue; }

                SetReady(false);
                Log.Warn($"[ollama] El proceso terminó inesperadamente (código {p?.ExitCode}). Reiniciando en {backoff.TotalSeconds}s...");
                await Task.Delay(backoff, ct);
                backoff = TimeSpan.FromSeconds(Math.Min(60, backoff.TotalSeconds * 2));
                await _lifecycle.WaitAsync(ct);
                try { if (_process is null || _process.HasExited) await LaunchAsync(ct); }
                finally { _lifecycle.Release(); }
                backoff = TimeSpan.FromSeconds(1);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Log.Error("[ollama] Fallo en supervisor", ex); await Task.Delay(backoff, ct).ContinueWith(_ => { }); }
        }
    }

    // ------------------------------------------------------------------ modelos

    public async Task<bool> HasModelAsync(string model, CancellationToken ct)
    {
        var tags = await Http.GetFromJsonAsync<JsonElement>("api/tags", ct);
        var wanted = model.Contains(':') ? model : model + ":latest";
        return tags.GetProperty("models").EnumerateArray()
            .Any(m => string.Equals(m.GetProperty("name").GetString(), wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>POST /api/pull en streaming; reporta % al System Tray. Los blobs quedan en .\models\.</summary>
    public async Task EnsureModelAsync(string model, CancellationToken ct)
    {
        if (await HasModelAsync(model, ct)) { Log.Info($"[ollama] Modelo '{model}' ya presente en models\\."); return; }

        Log.Info($"[ollama] Descargando modelo '{model}' a models\\ ...");
        using var req = new HttpRequestMessage(HttpMethod.Post, "api/pull")
        {
            Content = JsonContent.Create(new { model, stream = true })
        };
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync(ct));
        string? line;
        var last = DateTime.MinValue;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            if (r.TryGetProperty("error", out var err)) throw new InvalidOperationException($"ollama pull '{model}': {err.GetString()}");
            var status = r.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";
            double? pct = null;
            if (r.TryGetProperty("total", out var t) && r.TryGetProperty("completed", out var c) && t.GetInt64() > 0)
                pct = c.GetInt64() * 100.0 / t.GetInt64();
            if (DateTime.UtcNow - last > TimeSpan.FromMilliseconds(400) || status == "success")
            {
                last = DateTime.UtcNow;
                PullProgress?.Invoke($"{model}: {status}", pct);
            }
        }
        if (!await HasModelAsync(model, ct)) throw new InvalidOperationException($"El modelo '{model}' no aparece tras el pull.");
        PullProgress?.Invoke($"{model}: listo", 100);
        Log.Info($"[ollama] Modelo '{model}' descargado.");
    }

    /// <summary>Precarga el modelo en VRAM/RAM (petición vacía) para que la primera respuesta real no pague la carga.</summary>
    public async Task WarmUpAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await Http.PostAsJsonAsync("api/generate", new { model = _profile.Model, keep_alive = _config.Gateway.KeepAlive }, ct);
            Log.Info($"[ollama] Warm-up de '{_profile.Model}': HTTP {(int)resp.StatusCode}");
        }
        catch (Exception ex) { Log.Warn($"[ollama] Warm-up falló: {ex.Message}"); }
    }

    private void SetReady(bool ready)
    {
        if (IsReady == ready) return;
        IsReady = ready;
        ReadyChanged?.Invoke(ready);
    }

    private async Task StopProcessAsync()
    {
        var p = _process;
        _process = null;
        SetReady(false);
        if (p is null || p.HasExited) { p?.Dispose(); return; }
        try
        {
            p.Kill(entireProcessTree: true); // ollama serve no tiene apagado por señal en Windows
            await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception ex) { Log.Warn($"[ollama] Error al detener: {ex.Message}"); }
        finally { p.Dispose(); }
    }

    public async ValueTask DisposeAsync()
    {
        _stopping = true;
        _cts.Cancel();
        await StopProcessAsync();
        if (_supervisor != null) { try { await _supervisor; } catch { } }
        _logWriter?.Dispose();
        Http.Dispose();
    }
}
