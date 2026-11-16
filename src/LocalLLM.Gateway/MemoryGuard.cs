using System.Diagnostics;
using System.Globalization;
using Microsoft.AspNetCore.Http;

namespace LocalLLM.Gateway;

public sealed record MemorySnapshot(
    DateTime TimestampUtc,
    long TotalRamMb,
    long FreeRamMb,
    long? TotalVramMb,
    long? FreeVramMb,
    string VramSource,
    bool CircuitOpen,
    string? Reason,
    long RejectedSinceStart);

/// <summary>
/// Vigila RAM física (GlobalMemoryStatusEx) y VRAM libre (nvidia-smi en modo bucle, o contadores PDH
/// "GPU Adapter Memory" para AMD). Si la VRAM libre &lt; 800 MB o la RAM libre &lt; 2 GB abre el circuito:
/// las peticiones de inferencia reciben HTTP 429 + Retry-After ANTES de que Windows desborde tensores a
/// Shared GPU Memory o pagine a disco. Cierre con histéresis para evitar oscilaciones.
/// </summary>
public sealed class MemoryGuard : IAsyncDisposable
{
    private MemoryGuardSettings _settings;
    private readonly HardwareInfo _hw;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private Process? _smiLoop;
    private PerformanceCounter? _gpuDedicatedUsage;

    private volatile bool _open;
    private volatile string? _reason;
    private long _freeVramMb = -1, _totalVramMb = -1;
    private long _rejected;
    private MemorySnapshot _last;

    public event Action<bool, string?>? CircuitChanged;

    public MemoryGuard(MemoryGuardSettings settings, HardwareInfo hw)
    {
        _settings = settings;
        _hw = hw;
        _last = new MemorySnapshot(DateTime.UtcNow, hw.TotalRamMb, hw.AvailableRamMb, null, null, "none", false, null, 0);
    }

    public bool IsOpen => _settings.Enabled && _open;
    public MemorySnapshot Current => _last;

    public void UpdateSettings(MemoryGuardSettings s) => _settings = s;

    public void Start()
    {
        StartVramSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    // ------------------------------------------------------------------ fuentes de VRAM

    private void StartVramSource()
    {
        var gpu = _hw.PrimaryGpu;
        if (gpu is null) return;

        if (gpu.Vendor == GpuVendorMatch.Nvidia && _hw.NvidiaSmiPath != null)
        {
            // Un único proceso nvidia-smi en bucle (-lms) en lugar de lanzar uno por muestra.
            var psi = new ProcessStartInfo(_hw.NvidiaSmiPath)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var a in new[] { "--query-gpu=memory.total,memory.free", "--format=csv,noheader,nounits", "-i", "0",
                         "-lms", Math.Max(250, _settings.PollIntervalMs).ToString(CultureInfo.InvariantCulture) })
                psi.ArgumentList.Add(a);
            try
            {
                _smiLoop = Process.Start(psi);
                if (_smiLoop != null)
                {
                    ChildProcessJob.Instance.Assign(_smiLoop);
                    _smiLoop.OutputDataReceived += (_, e) => ParseSmiLine(e.Data);
                    _smiLoop.BeginOutputReadLine();
                    _smiLoop.BeginErrorReadLine();
                    Log.Info("[memguard] Monitor VRAM: nvidia-smi -lms");
                    return;
                }
            }
            catch (Exception ex) { Log.Warn($"[memguard] No se pudo iniciar nvidia-smi en bucle: {ex.Message}"); }
        }

        // AMD (u NVIDIA sin nvidia-smi): contador PDH por LUID del adaptador; legible por usuarios estándar.
        try
        {
            var instance = $"luid_0x{gpu.LuidHigh:X8}_0x{gpu.LuidLow:X8}_phys_0";
            var cat = new PerformanceCounterCategory("GPU Adapter Memory");
            var name = cat.GetInstanceNames().FirstOrDefault(n => n.Equals(instance, StringComparison.OrdinalIgnoreCase))
                       ?? cat.GetInstanceNames().FirstOrDefault(n => n.StartsWith($"luid_0x{gpu.LuidHigh:X8}_0x{gpu.LuidLow:X8}", StringComparison.OrdinalIgnoreCase));
            if (name != null)
            {
                _gpuDedicatedUsage = new PerformanceCounter("GPU Adapter Memory", "Dedicated Usage", name, readOnly: true);
                Interlocked.Exchange(ref _totalVramMb, gpu.DedicatedVramMb);
                Log.Info($"[memguard] Monitor VRAM: PDH 'GPU Adapter Memory({name})'");
            }
        }
        catch (Exception ex) { Log.Warn($"[memguard] Contadores PDH de GPU no disponibles: {ex.Message}"); }
    }

    private void ParseSmiLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        var p = line.Split(',', StringSplitOptions.TrimEntries);
        if (p.Length >= 2 && long.TryParse(p[0], out var total) && long.TryParse(p[1], out var free))
        {
            Interlocked.Exchange(ref _totalVramMb, total);
            Interlocked.Exchange(ref _freeVramMb, free);
        }
    }

    // ------------------------------------------------------------------ bucle

    private async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Max(250, _settings.PollIntervalMs)));
        do
        {
            try { Evaluate(); }
            catch (Exception ex) { Log.Warn($"[memguard] Error de muestreo: {ex.Message}"); }
        } while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
    }

    private void Evaluate()
    {
        var mem = NativeMethods.GetMemoryStatus();
        long freeRam = (long)(mem.ullAvailPhys / 1048576);
        long totalRam = (long)(mem.ullTotalPhys / 1048576);

        long? freeVram = null, totalVram = null;
        string source = "none";
        if (_smiLoop is { HasExited: false } && Interlocked.Read(ref _freeVramMb) >= 0)
        {
            freeVram = Interlocked.Read(ref _freeVramMb);
            totalVram = Interlocked.Read(ref _totalVramMb);
            source = "nvidia-smi";
        }
        else if (_gpuDedicatedUsage != null)
        {
            var usedMb = (long)(_gpuDedicatedUsage.NextValue() / 1048576);
            totalVram = Interlocked.Read(ref _totalVramMb);
            freeVram = Math.Max(0, totalVram.Value - usedMb);
            source = "pdh";
        }

        bool ramLow = freeRam < _settings.MinFreeRamMb;
        bool vramLow = freeVram.HasValue && freeVram.Value < _settings.MinFreeVramMb;
        bool ramOk = freeRam > _settings.MinFreeRamMb * _settings.RecoveryFactor;
        bool vramOk = !freeVram.HasValue || freeVram.Value > _settings.MinFreeVramMb * _settings.RecoveryFactor;

        if (!_open && (ramLow || vramLow))
        {
            _reason = ramLow
                ? $"RAM física libre {freeRam} MB < {_settings.MinFreeRamMb} MB"
                : $"VRAM libre {freeVram} MB < {_settings.MinFreeVramMb} MB";
            _open = true;
            Log.Warn($"[memguard] CIRCUITO ABIERTO: {_reason}");
            CircuitChanged?.Invoke(true, _reason);
        }
        else if (_open && ramOk && vramOk)
        {
            _open = false;
            Log.Info($"[memguard] Circuito cerrado (RAM libre {freeRam} MB, VRAM libre {freeVram?.ToString() ?? "n/d"} MB).");
            _reason = null;
            CircuitChanged?.Invoke(false, null);
        }

        _last = new MemorySnapshot(DateTime.UtcNow, totalRam, freeRam, totalVram, freeVram, source, _open, _reason,
            Interlocked.Read(ref _rejected));
    }

    // ------------------------------------------------------------------ Circuit Breaker HTTP 429

    /// <summary>Devuelve true si la petición fue rechazada (respuesta 429 ya escrita).</summary>
    public async Task<bool> TryRejectAsync(HttpContext ctx)
    {
        if (!IsOpen) return false;
        Interlocked.Increment(ref _rejected);
        ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        ctx.Response.Headers.RetryAfter = _settings.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        await ctx.Response.WriteAsJsonAsync(new
        {
            error = new
            {
                message = $"LocalLLM Memory Guard: {_reason}. Reintenta en {_settings.RetryAfterSeconds}s.",
                type = "memory_pressure",
                code = "circuit_open"
            }
        });
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_loop != null) { try { await _loop; } catch { } }
        try { if (_smiLoop is { HasExited: false }) _smiLoop.Kill(); } catch { }
        _smiLoop?.Dispose();
        _gpuDedicatedUsage?.Dispose();
    }
}
