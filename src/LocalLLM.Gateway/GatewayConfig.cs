using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalLLM.Gateway;

/// <summary>Modelo de profiles.json (configuración remota sincronizada vía Git).</summary>
public sealed class GatewayConfig
{
    public int SchemaVersion { get; set; } = 1;
    public GatewaySettings Gateway { get; set; } = new();
    public MemoryGuardSettings MemoryGuard { get; set; } = new();
    public GitSettings Git { get; set; } = new();
    public ToolsSettings Tools { get; set; } = new();
    public List<HardwareProfile> Profiles { get; set; } = new();

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static GatewayConfig Load(string? path = null)
    {
        path ??= PortablePaths.ActiveProfilesFile;
        if (!File.Exists(path))
            throw new FileNotFoundException($"No se encontró profiles.json en '{path}'.");
        var cfg = JsonSerializer.Deserialize<GatewayConfig>(File.ReadAllText(path), JsonOptions)
                  ?? throw new InvalidDataException("profiles.json vacío o inválido.");
        cfg.Validate();
        return cfg;
    }

    public void Validate()
    {
        if (Gateway.Port == Gateway.OllamaPort)
            throw new InvalidDataException("gateway.port y gateway.ollamaPort no pueden ser iguales (el Gateway ocupa 11434 y Ollama corre detrás).");
        if (Profiles.Count == 0)
            throw new InvalidDataException("profiles.json debe contener al menos un perfil.");
        if (!Profiles.Any(p => p.Match.GpuVendor == GpuVendorMatch.Any || p.Match.GpuVendor == GpuVendorMatch.Cpu))
            throw new InvalidDataException("profiles.json debe contener al menos un perfil CPU/any como último recurso.");
    }
}

public sealed class GatewaySettings
{
    /// <summary>Siempre loopback: Windows no pide Admin ni dispara el Firewall.</summary>
    public string ListenAddress { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 11434;
    /// <summary>Puerto opcional HTTP/2 sin TLS (h2c, "prior knowledge"). 0 = deshabilitado.</summary>
    public int H2cPort { get; set; } = 11436;
    /// <summary>Puerto interno donde escucha ollama.exe (solo loopback).</summary>
    public int OllamaPort { get; set; } = 11435;
    /// <summary>Peticiones de inferencia en cola máximo antes de devolver 429.</summary>
    public int MaxQueuedRequests { get; set; } = 64;
    /// <summary>Tiempo máximo esperando turno en la cola.</summary>
    public int QueueTimeoutSeconds { get; set; } = 120;
    public string KeepAlive { get; set; } = "30m";
    /// <summary>Inyecta stream_options.include_usage=true en /v1/* con stream para contar tokens exactos.</summary>
    public bool InjectStreamUsage { get; set; } = true;
    public int DrainTimeoutSeconds { get; set; } = 60;
}

public sealed class MemoryGuardSettings
{
    public bool Enabled { get; set; } = true;
    public long MinFreeVramMb { get; set; } = 800;
    public long MinFreeRamMb { get; set; } = 2048;
    /// <summary>Histéresis: el circuito se cierra cuando lo libre supera umbral * factor.</summary>
    public double RecoveryFactor { get; set; } = 1.25;
    public int PollIntervalMs { get; set; } = 1000;
    public int RetryAfterSeconds { get; set; } = 5;
    /// <summary>Margen de VRAM reservado al seleccionar perfil (escritorio de Windows, navegador...).</summary>
    public long VramReserveMb { get; set; } = 1024;
    /// <summary>Margen de RAM reservado para el sistema al seleccionar perfil CPU.</summary>
    public long RamReserveMb { get; set; } = 6144;
}

public sealed class GitSettings
{
    public bool Enabled { get; set; } = true;
    public string Remote { get; set; } = "https://github.com/rmrodriguezgit/LLM-Portable.git";
    public string Branch { get; set; } = "main";
    public int IntervalMinutes { get; set; } = 15;
    /// <summary>Permite el Nivel 2 (recompilar el Gateway). Requiere SDK .NET 8 portable (se descarga en bin\dotnet).</summary>
    public bool AllowSelfRebuild { get; set; } = true;
    public int HealthCheckTimeoutSeconds { get; set; } = 10;
}

public sealed class ToolsSettings
{
    public ToolSource Uv { get; set; } = new()
    {
        GithubRepo = "astral-sh/uv",
        AssetPattern = @"^uv-x86_64-pc-windows-msvc\.zip$",
        ProbeRelativePath = "uv.exe"
    };
    public ToolSource Ollama { get; set; } = new()
    {
        GithubRepo = "ollama/ollama",
        AssetPattern = @"^ollama-windows-amd64\.zip$",
        ProbeRelativePath = "ollama.exe"
    };
    public ToolSource Git { get; set; } = new()
    {
        GithubRepo = "git-for-windows/git",
        AssetPattern = @"^MinGit-[0-9.]+(\.windows\.[0-9]+)?-64-bit\.zip$",
        ProbeRelativePath = @"cmd\git.exe",
        PreferSystemInstall = true
    };
    /// <summary>Canal del SDK .NET para auto-recompilación (Nivel 2).</summary>
    public string DotnetChannel { get; set; } = "8.0";
}

public sealed class ToolSource
{
    public string GithubRepo { get; set; } = "";
    /// <summary>"latest" o un tag concreto (ej. "v0.12.6"). Fijar un tag da builds reproducibles.</summary>
    public string Version { get; set; } = "latest";
    /// <summary>Regex del nombre del asset del release.</summary>
    public string AssetPattern { get; set; } = "";
    /// <summary>SHA256 fijado (hex). Si está presente tiene prioridad sobre el digest publicado.</summary>
    public string? Sha256 { get; set; }
    /// <summary>Archivo que debe existir tras extraer (relativo a la carpeta de la herramienta).</summary>
    public string ProbeRelativePath { get; set; } = "";
    /// <summary>Solo Git: usar git.exe del sistema si ya existe en el PATH del usuario.</summary>
    public bool PreferSystemInstall { get; set; }
}

public enum GpuVendorMatch { Any, Nvidia, Amd, Intel, Cpu }

public sealed class HardwareProfile
{
    public string Id { get; set; } = "";
    public string Description { get; set; } = "";
    public ProfileMatch Match { get; set; } = new();
    public ModelSpec Model { get; set; } = new();
    /// <summary>Modelos alternativos (orden de preferencia) si el principal no cabe tras ajustar contexto/paralelismo.</summary>
    public List<ModelSpec> Fallbacks { get; set; } = new();
    public int NumCtx { get; set; } = 8192;
    public int MinNumCtx { get; set; } = 4096;
    public int NumParallel { get; set; } = 1;
    /// <summary>f16 | q8_0 | q4_0 (requiere flash attention).</summary>
    public string KvCacheType { get; set; } = "q8_0";
    public bool FlashAttention { get; set; } = true;
    /// <summary>Para GPUs AMD/Intel: calcular num_gpu (capas offload) según VRAM dedicada.</summary>
    public bool DynamicNumGpu { get; set; }
}

public sealed class ProfileMatch
{
    public GpuVendorMatch GpuVendor { get; set; } = GpuVendorMatch.Any;
    public long MinVramMb { get; set; }
    public long MaxVramMb { get; set; } = long.MaxValue;
    public long MinRamMb { get; set; }
    public long MaxRamMb { get; set; } = long.MaxValue;
}

public sealed class ModelSpec
{
    /// <summary>Tag de Ollama, ej. "qwen3:14b".</summary>
    public string Name { get; set; } = "";
    /// <summary>Tamaño aproximado de los pesos cuantizados en GB (para el cálculo de encaje).</summary>
    public double WeightsGb { get; set; }
    /// <summary>MB de KV cache por cada 1024 tokens de contexto en f16 (q8_0 = /2, q4_0 = /4).</summary>
    public double KvMbPer1kF16 { get; set; }
    /// <summary>Número de capas (para calcular num_gpu parcial en AMD/Intel).</summary>
    public int Layers { get; set; }
}
