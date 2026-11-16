using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace LocalLLM.Gateway;

public sealed record GpuInfo(
    string Name,
    GpuVendorMatch Vendor,
    long DedicatedVramMb,
    long SharedMemoryMb,
    uint VendorId,
    uint DeviceId,
    long LuidLow,
    int LuidHigh,
    bool IsSoftware,
    string Source);

public sealed record HardwareInfo(
    string CpuName,
    int PhysicalCores,
    int LogicalProcessors,
    long TotalRamMb,
    long AvailableRamMb,
    List<GpuInfo> Gpus,
    GpuInfo? PrimaryGpu,
    string? NvidiaSmiPath,
    string Fingerprint);

/// <summary>Perfil resuelto tras aplicar la matriz de decisión y el cálculo de encaje en memoria.</summary>
public sealed record ResolvedProfile(
    string ProfileId,
    string Description,
    string Model,
    int NumCtx,
    int NumParallel,
    string KvCacheType,
    bool FlashAttention,
    int? NumGpu,
    int? NumThread,
    double EstimatedMemoryGb,
    string Target,
    List<string> Decisions);

/// <summary>
/// Perfilado de hardware en modo usuario estándar: DXGI (VRAM dedicada exacta, 64 bits), WMI de solo lectura
/// (Win32_Processor / Win32_PhysicalMemory / Win32_VideoController), GlobalMemoryStatusEx y nvidia-smi.
/// Se ejecuta en CADA arranque: si la carpeta se mueve en un USB a otra PC, se re-perfila y se re-selecciona modelo.
/// </summary>
public static class HardwareProfiler
{
    private const uint VendorNvidia = 0x10DE, VendorAmd = 0x1002, VendorIntel = 0x8086, VendorMicrosoft = 0x1414;

    public static async Task<HardwareInfo> DetectAsync(CancellationToken ct = default)
    {
        var (cpuName, physical) = QueryCpu();
        var mem = NativeMethods.GetMemoryStatus();
        long totalRamMb = (long)(mem.ullTotalPhys / 1048576);
        long wmiRam = QueryInstalledRamMb();
        if (wmiRam > 0) totalRamMb = Math.Max(totalRamMb, wmiRam - 64); // RAM instalada vs. visible para el SO

        var gpus = EnumerateDxgiAdapters();
        if (gpus.Count == 0) gpus = QueryWmiVideoControllers();

        var smi = LocateNvidiaSmi();
        if (smi != null)
        {
            // nvidia-smi da la VRAM real (DXGI a veces resta memoria reservada); sobreescribe por orden.
            var nv = await QueryNvidiaSmiAsync(smi, ct);
            var nvGpus = gpus.Where(g => g.Vendor == GpuVendorMatch.Nvidia).ToList();
            for (int i = 0; i < Math.Min(nv.Count, nvGpus.Count); i++)
            {
                var idx = gpus.IndexOf(nvGpus[i]);
                gpus[idx] = nvGpus[i] with { DedicatedVramMb = Math.Max(nvGpus[i].DedicatedVramMb, nv[i].totalMb), Source = "dxgi+nvidia-smi" };
            }
            if (nvGpus.Count == 0)
                gpus.AddRange(nv.Select(n => new GpuInfo(n.name, GpuVendorMatch.Nvidia, n.totalMb, 0, VendorNvidia, 0, 0, 0, false, "nvidia-smi")));
        }

        // GPU principal: la de mayor VRAM dedicada (no software, no iGPU con <1 GB "dedicada" de BIOS).
        var primary = gpus.Where(g => !g.IsSoftware && g.DedicatedVramMb >= 1024)
                          .OrderByDescending(g => g.Vendor == GpuVendorMatch.Nvidia)
                          .ThenByDescending(g => g.DedicatedVramMb)
                          .FirstOrDefault();

        var fingerprint = $"{cpuName}|{physical}|{totalRamMb / 1024}GB|" +
                          string.Join(";", gpus.Where(g => !g.IsSoftware).Select(g => $"{g.VendorId:X4}:{g.DeviceId:X4}:{g.DedicatedVramMb / 512}"));

        return new HardwareInfo(cpuName, physical, Environment.ProcessorCount, totalRamMb,
            (long)(mem.ullAvailPhys / 1048576), gpus, primary, smi, fingerprint);
    }

    /// <summary>
    /// Compara con el último snapshot (data\hardware.json). Devuelve true si el hardware cambió
    /// (p.ej. el USB se conectó a otra PC) para forzar reselección de modelo y reinicio de Ollama.
    /// </summary>
    public static bool UpdateSnapshot(HardwareInfo hw, ResolvedProfile profile)
    {
        string? previous = null;
        try
        {
            if (File.Exists(PortablePaths.HardwareSnapshot))
                previous = JsonDocument.Parse(File.ReadAllText(PortablePaths.HardwareSnapshot))
                    .RootElement.GetProperty("hardware").GetProperty("fingerprint").GetString();
        }
        catch { /* snapshot corrupto: se regenera */ }

        var json = JsonSerializer.Serialize(new { savedAt = DateTime.UtcNow, machine = Environment.MachineName, hardware = hw, profile },
            GatewayConfig.JsonOptions);
        var tmp = PortablePaths.HardwareSnapshot + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, PortablePaths.HardwareSnapshot, overwrite: true);

        var changed = previous != null && previous != hw.Fingerprint;
        if (changed) Log.Warn($"[hardware] Hardware distinto al último arranque ({previous} → {hw.Fingerprint}). Re-perfilando.");
        return changed;
    }

    // ------------------------------------------------------------------ Matriz de decisión

    public static ResolvedProfile SelectProfile(HardwareInfo hw, GatewayConfig cfg)
    {
        var decisions = new List<string>();
        var gpu = hw.PrimaryGpu;
        var vendor = gpu?.Vendor ?? GpuVendorMatch.Cpu;
        var vramMb = gpu?.DedicatedVramMb ?? 0;

        // Intel Arc/integradas: Ollama oficial no acelera Intel en Windows → se trata como CPU.
        if (vendor == GpuVendorMatch.Intel)
        {
            decisions.Add($"GPU Intel '{gpu!.Name}' detectada; Ollama no la acelera de forma oficial → perfil CPU.");
            vendor = GpuVendorMatch.Cpu; vramMb = 0;
        }

        foreach (var p in cfg.Profiles)
        {
            if (!Matches(p.Match, vendor, vramMb, hw.TotalRamMb)) continue;
            var candidates = new[] { p.Model }.Concat(p.Fallbacks);
            foreach (var model in candidates)
            {
                var fit = TryFit(p, model, vendor, vramMb, hw, cfg.MemoryGuard, decisions);
                if (fit != null) return fit with { Decisions = decisions };
            }
            decisions.Add($"Perfil '{p.Id}' coincide pero ningún modelo cabe; probando el siguiente.");
        }

        // Último recurso: el perfil CPU más pequeño.
        var last = cfg.Profiles.Last(p => p.Match.GpuVendor is GpuVendorMatch.Cpu or GpuVendorMatch.Any);
        var m = last.Fallbacks.LastOrDefault() ?? last.Model;
        decisions.Add($"Sin encaje holgado: se fuerza '{m.Name}' de '{last.Id}'.");
        return new ResolvedProfile(last.Id, last.Description, m.Name, last.MinNumCtx, 1, last.KvCacheType, last.FlashAttention,
            0, hw.PhysicalCores, m.WeightsGb, "cpu", decisions);
    }

    private static bool Matches(ProfileMatch m, GpuVendorMatch vendor, long vramMb, long ramMb)
    {
        var vendorOk = m.GpuVendor switch
        {
            GpuVendorMatch.Any => true,
            GpuVendorMatch.Cpu => true, // un perfil CPU sirve para cualquier equipo
            _ => m.GpuVendor == vendor
        };
        if (!vendorOk) return false;
        if (m.GpuVendor is GpuVendorMatch.Nvidia or GpuVendorMatch.Amd && (vramMb < m.MinVramMb || vramMb > m.MaxVramMb)) return false;
        return ramMb >= m.MinRamMb && ramMb <= m.MaxRamMb;
    }

    /// <summary>
    /// Cálculo de encaje: pesos + KV cache (num_ctx × num_parallel, cuantizado) + margen. Reduce primero
    /// num_parallel, luego num_ctx (a la mitad, hasta MinNumCtx). Evita el desbordamiento a Shared GPU Memory.
    /// </summary>
    private static ResolvedProfile? TryFit(HardwareProfile p, ModelSpec model, GpuVendorMatch vendor, long vramMb,
        HardwareInfo hw, MemoryGuardSettings guard, List<string> decisions)
    {
        bool gpuTarget = p.Match.GpuVendor is GpuVendorMatch.Nvidia or GpuVendorMatch.Amd;
        double kvFactor = (p.FlashAttention ? p.KvCacheType : "f16") switch { "q8_0" => 0.5, "q4_0" => 0.25, _ => 1.0 };
        double budgetGb = gpuTarget
            ? (vramMb - guard.VramReserveMb - guard.MinFreeVramMb) / 1024.0
            : (hw.TotalRamMb - guard.RamReserveMb - guard.MinFreeRamMb) / 1024.0;

        double Estimate(int ctx, int par) => model.WeightsGb + model.KvMbPer1kF16 * kvFactor * (ctx / 1024.0) * par / 1024.0 + 0.4;

        int ctx = p.NumCtx, par = p.NumParallel;
        while (Estimate(ctx, par) > budgetGb && par > 1) par--;
        while (Estimate(ctx, par) > budgetGb && ctx / 2 >= p.MinNumCtx) ctx /= 2;

        int? numGpu = null;
        var est = Estimate(ctx, par);
        if (est > budgetGb)
        {
            if (!(p.DynamicNumGpu && gpuTarget && model.Layers > 0))
            {
                decisions.Add($"'{model.Name}' necesita ~{est:F1} GB y el presupuesto es {budgetGb:F1} GB → no cabe.");
                return null;
            }
            // Offload parcial (AMD): capas que caben en VRAM tras reservar KV; el resto en RAM del sistema.
            double kvGb = est - model.WeightsGb - 0.4;
            double perLayer = model.WeightsGb / model.Layers;
            numGpu = (int)Math.Floor((budgetGb - kvGb - 0.4) / perLayer);
            if (numGpu < model.Layers / 3)
            {
                decisions.Add($"'{model.Name}': solo {numGpu}/{model.Layers} capas cabrían en VRAM → descartado.");
                return null;
            }
            decisions.Add($"'{model.Name}': offload parcial num_gpu={numGpu}/{model.Layers} capas.");
        }
        else if (p.DynamicNumGpu && model.Layers > 0)
        {
            numGpu = model.Layers + 1; // todas las capas + capa de salida
        }

        if (par != p.NumParallel || ctx != p.NumCtx)
            decisions.Add($"Ajuste de encaje: num_parallel {p.NumParallel}→{par}, num_ctx {p.NumCtx}→{ctx}.");
        decisions.Add($"Seleccionado '{model.Name}' (perfil {p.Id}): ~{est:F1} GB de {budgetGb:F1} GB disponibles en {(gpuTarget ? "VRAM" : "RAM")}.");

        return new ResolvedProfile(p.Id, p.Description, model.Name, ctx, par, p.KvCacheType, p.FlashAttention,
            gpuTarget ? numGpu : 0,
            gpuTarget ? null : Math.Max(1, hw.PhysicalCores), // CPU: núcleos físicos, sin hyperthreading
            Math.Round(est, 2), gpuTarget ? vendor.ToString().ToLowerInvariant() : "cpu", decisions);
    }

    // ------------------------------------------------------------------ CPU / RAM (WMI lectura)

    private static (string name, int physicalCores) QueryCpu()
    {
        string name = "CPU desconocida";
        int cores = 0;
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name, NumberOfCores FROM Win32_Processor");
            foreach (ManagementObject o in s.Get())
            {
                name = (o["Name"] as string)?.Trim() ?? name;
                cores += Convert.ToInt32(o["NumberOfCores"] ?? 0, CultureInfo.InvariantCulture);
            }
        }
        catch (Exception ex) { Log.Warn($"[hardware] WMI Win32_Processor no disponible: {ex.Message}"); }
        if (cores <= 0) cores = Math.Max(1, Environment.ProcessorCount / 2);
        return (name, cores);
    }

    private static long QueryInstalledRamMb()
    {
        try
        {
            long total = 0;
            using var s = new ManagementObjectSearcher("SELECT Capacity FROM Win32_PhysicalMemory");
            foreach (ManagementObject o in s.Get())
                total += Convert.ToInt64(o["Capacity"] ?? 0L, CultureInfo.InvariantCulture);
            return total / 1048576;
        }
        catch { return 0; }
    }

    // ------------------------------------------------------------------ DXGI

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId, DeviceId, SubSysId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint AdapterLuidLow;
        public int AdapterLuidHigh;
        public uint Flags;
    }

    private const uint DXGI_ADAPTER_FLAG_SOFTWARE = 2;
    private const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);

    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        // IDXGIObject
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
        // IDXGIFactory
        void EnumAdapters(); void MakeWindowAssociation(); void GetWindowAssociation(); void CreateSwapChain(); void CreateSoftwareAdapter();
        // IDXGIFactory1
        [PreserveSig] int EnumAdapters1(uint index, out IDXGIAdapter1 adapter);
        [PreserveSig] bool IsCurrent();
    }

    [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
        void EnumOutputs(); void GetDesc(); void CheckInterfaceSupport();
        [PreserveSig] int GetDesc1(out DXGI_ADAPTER_DESC1 desc);
    }

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IDXGIFactory1 factory);

    private static List<GpuInfo> EnumerateDxgiAdapters()
    {
        var list = new List<GpuInfo>();
        try
        {
            var iid = typeof(IDXGIFactory1).GUID;
            if (CreateDXGIFactory1(ref iid, out var factory) != 0) return list;
            try
            {
                for (uint i = 0; ; i++)
                {
                    var hr = factory.EnumAdapters1(i, out var adapter);
                    if (hr == DXGI_ERROR_NOT_FOUND || hr != 0) break;
                    try
                    {
                        if (adapter.GetDesc1(out var d) != 0) continue;
                        bool software = (d.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0 || d.VendorId == VendorMicrosoft;
                        list.Add(new GpuInfo(d.Description.Trim(), ToVendor(d.VendorId),
                            (long)(d.DedicatedVideoMemory / 1048576), (long)(d.SharedSystemMemory / 1048576),
                            d.VendorId, d.DeviceId, d.AdapterLuidLow, d.AdapterLuidHigh, software, "dxgi"));
                    }
                    finally { Marshal.ReleaseComObject(adapter); }
                }
            }
            finally { Marshal.ReleaseComObject(factory); }
        }
        catch (Exception ex) { Log.Warn($"[hardware] DXGI no disponible: {ex.Message}"); }
        return list;
    }

    /// <summary>Fallback WMI (AdapterRAM es uint32: se satura en 4 GB, por eso DXGI es la fuente primaria).</summary>
    private static List<GpuInfo> QueryWmiVideoControllers()
    {
        var list = new List<GpuInfo>();
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name, AdapterRAM, PNPDeviceID FROM Win32_VideoController");
            foreach (ManagementObject o in s.Get())
            {
                var name = (o["Name"] as string) ?? "GPU";
                var pnp = (o["PNPDeviceID"] as string) ?? "";
                uint vid = pnp.Contains("VEN_10DE") ? VendorNvidia : pnp.Contains("VEN_1002") ? VendorAmd : pnp.Contains("VEN_8086") ? VendorIntel : 0;
                long ram = Convert.ToInt64(o["AdapterRAM"] ?? 0L, CultureInfo.InvariantCulture) / 1048576;
                list.Add(new GpuInfo(name, ToVendor(vid), ram, 0, vid, 0, 0, 0, vid == 0, "wmi"));
            }
        }
        catch (Exception ex) { Log.Warn($"[hardware] WMI Win32_VideoController no disponible: {ex.Message}"); }
        return list;
    }

    private static GpuVendorMatch ToVendor(uint vid) => vid switch
    {
        VendorNvidia => GpuVendorMatch.Nvidia,
        VendorAmd => GpuVendorMatch.Amd,
        VendorIntel => GpuVendorMatch.Intel,
        _ => GpuVendorMatch.Cpu
    };

    // ------------------------------------------------------------------ nvidia-smi

    /// <summary>Busca nvidia-smi en System32 (drivers DCH), NVSMI clásico o el DriverStore. Solo lectura/ejecución.</summary>
    public static string? LocateNvidiaSmi()
    {
        var sys32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var candidates = new List<string>
        {
            Path.Combine(sys32, "nvidia-smi.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe")
        };
        foreach (var c in candidates) if (File.Exists(c)) return c;
        try
        {
            var repo = Path.Combine(sys32, "DriverStore", "FileRepository");
            if (Directory.Exists(repo))
                foreach (var dir in Directory.EnumerateDirectories(repo, "nv*"))
                {
                    var f = Path.Combine(dir, "nvidia-smi.exe");
                    if (File.Exists(f)) return f;
                }
        }
        catch { /* sin permiso de listado: ignorar */ }
        return null;
    }

    private static async Task<List<(string name, long totalMb, long freeMb)>> QueryNvidiaSmiAsync(string smi, CancellationToken ct)
    {
        var list = new List<(string, long, long)>();
        try
        {
            var r = await ProcessRunner.RunAsync(smi,
                new[] { "--query-gpu=name,memory.total,memory.free", "--format=csv,noheader,nounits" },
                timeout: TimeSpan.FromSeconds(15), ct: ct);
            if (!r.Success) return list;
            foreach (var line in r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = line.Split(',', StringSplitOptions.TrimEntries);
                if (parts.Length >= 3 && long.TryParse(parts[1], out var tot) && long.TryParse(parts[2], out var free))
                    list.Add((parts[0], tot, free));
            }
        }
        catch (Exception ex) { Log.Warn($"[hardware] nvidia-smi falló: {ex.Message}"); }
        return list;
    }
}
