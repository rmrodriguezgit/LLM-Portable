using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LocalLLM.Gateway;

public sealed record BootstrapProgress(string Stage, double? Percent, string Message);

/// <summary>
/// Auto-aprovisionamiento Zero-Admin: verifica uv, ollama y git en .\bin\; si faltan, los descarga por HTTPS
/// a data\cache\downloads, valida SHA256 y extrae a una carpeta temporal que luego se renombra (movimiento
/// atómico dentro del mismo volumen). Nunca invoca UAC ni escribe fuera de la carpeta portable.
/// </summary>
public sealed class PortableBootstrapper
{
    private readonly GatewayConfig _config;
    private readonly HttpClient _http;

    public event Action<BootstrapProgress>? Progress;

    /// <summary>Ruta del git.exe a usar (portable o del sistema).</summary>
    public string? GitExe { get; private set; }

    public PortableBootstrapper(GatewayConfig config)
    {
        _config = config;
        _http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        })
        { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("LocalLLM-Portable", "1.0"));
    }

    public async Task RunAsync(CancellationToken ct)
    {
        PortablePaths.EnsureDirectories();
        CleanupStaleTemp();

        await EnsureToolAsync("uv", _config.Tools.Uv, PortablePaths.UvDir, ct);
        await EnsureToolAsync("ollama", _config.Tools.Ollama, PortablePaths.OllamaDir, ct);
        await EnsureGitAsync(ct);
        await EnsurePythonEnvAsync(ct);
        Report("ready", 100, "Herramientas portables listas.");
    }

    // ------------------------------------------------------------------ herramientas

    private async Task EnsureToolAsync(string name, ToolSource src, string targetDir, CancellationToken ct)
    {
        var probe = Path.Combine(targetDir, src.ProbeRelativePath);
        if (File.Exists(probe))
        {
            Log.Info($"[bootstrap] {name} presente: {probe}");
            return;
        }

        Report(name, 0, $"Resolviendo release de {name} ({src.GithubRepo}@{src.Version})...");
        var asset = await ResolveGithubAssetAsync(src, ct);
        Log.Info($"[bootstrap] {name}: {asset.Name} ({asset.Size / 1048576.0:F1} MB) sha256={asset.Sha256}");

        var zipPath = Path.Combine(PortablePaths.Downloads, asset.Name);
        await DownloadVerifiedAsync(asset.Url, zipPath, asset.Sha256, HashAlgorithmName.SHA256, name, ct);

        Report(name, null, $"Extrayendo {asset.Name}...");
        ExtractAtomically(zipPath, targetDir);

        if (!File.Exists(probe))
            throw new InvalidOperationException($"Tras extraer {asset.Name} no se encontró '{src.ProbeRelativePath}'.");
        TryDelete(zipPath);
        Log.Info($"[bootstrap] {name} instalado en {targetDir}");
    }

    private async Task EnsureGitAsync(CancellationToken ct)
    {
        if (File.Exists(PortablePaths.PortableGitExe))
        {
            GitExe = PortablePaths.PortableGitExe;
            return;
        }
        if (_config.Tools.Git.PreferSystemInstall && FindOnPath("git.exe") is { } systemGit)
        {
            Log.Info($"[bootstrap] Usando git del sistema: {systemGit}");
            GitExe = systemGit;
            return;
        }
        await EnsureToolAsync("git", _config.Tools.Git, PortablePaths.GitDir, ct);
        GitExe = PortablePaths.PortableGitExe;
    }

    /// <summary>Crea/sincroniza env\.venv con uv si el repo trae un proyecto Python (repo\python\pyproject.toml).</summary>
    private async Task EnsurePythonEnvAsync(CancellationToken ct)
    {
        var project = Path.Combine(PortablePaths.Repo, "python");
        if (!File.Exists(Path.Combine(project, "pyproject.toml"))) return;
        Report("python", null, "Sincronizando entorno Python aislado (uv sync)...");
        var r = await UvRunner.SyncAsync(ct);
        if (!r.Success) Log.Warn($"[bootstrap] uv sync falló: {r.StdErr}");
    }

    // ------------------------------------------------------------------ SDK .NET (solo Nivel 2 de actualización)

    /// <summary>
    /// Devuelve un dotnet.exe con SDK 8+. Si no existe, descarga el ZIP oficial del SDK en bin\dotnet
    /// (metadatos de releases.json de Microsoft, verificación SHA512). Sin instalador, sin Admin.
    /// </summary>
    public async Task<string> EnsureDotnetSdkAsync(CancellationToken ct)
    {
        if (File.Exists(PortablePaths.PortableDotnetExe)) return PortablePaths.PortableDotnetExe;

        if (FindOnPath("dotnet.exe") is { } sys)
        {
            var r = await ProcessRunner.RunAsync(sys, new[] { "--list-sdks" }, timeout: TimeSpan.FromSeconds(30), ct: ct);
            if (r.Success && Regex.IsMatch(r.StdOut, @"^(8|9|1\d)\.", RegexOptions.Multiline)) return sys;
        }

        var channel = _config.Tools.DotnetChannel;
        Report("dotnet", 0, $"Resolviendo SDK .NET {channel} portable...");
        var metaUrl = $"https://builds.dotnet.microsoft.com/dotnet/release-metadata/{channel}/releases.json";
        using var meta = JsonDocument.Parse(await _http.GetStringAsync(metaUrl, ct));
        var latestSdk = meta.RootElement.GetProperty("latest-sdk").GetString();
        JsonElement? file = null;
        foreach (var rel in meta.RootElement.GetProperty("releases").EnumerateArray())
        {
            if (!rel.TryGetProperty("sdks", out var sdks)) continue;
            foreach (var sdk in sdks.EnumerateArray())
            {
                if (sdk.GetProperty("version").GetString() != latestSdk) continue;
                foreach (var f in sdk.GetProperty("files").EnumerateArray())
                    if (f.GetProperty("name").GetString() == "dotnet-sdk-win-x64.zip") { file = f; break; }
            }
            if (file != null) break;
        }
        if (file is null) throw new InvalidOperationException($"No se encontró dotnet-sdk-win-x64.zip para SDK {latestSdk}.");

        var url = file.Value.GetProperty("url").GetString()!;
        var sha512 = file.Value.GetProperty("hash").GetString()!;
        var zip = Path.Combine(PortablePaths.Downloads, $"dotnet-sdk-{latestSdk}-win-x64.zip");
        await DownloadVerifiedAsync(url, zip, sha512, HashAlgorithmName.SHA512, "dotnet", ct);
        ExtractAtomically(zip, PortablePaths.DotnetDir);
        TryDelete(zip);
        return PortablePaths.PortableDotnetExe;
    }

    // ------------------------------------------------------------------ GitHub releases + SHA256

    private sealed record ResolvedAsset(string Name, string Url, long Size, string Sha256);

    private async Task<ResolvedAsset> ResolveGithubAssetAsync(ToolSource src, CancellationToken ct)
    {
        var api = src.Version.Equals("latest", StringComparison.OrdinalIgnoreCase)
            ? $"https://api.github.com/repos/{src.GithubRepo}/releases/latest"
            : $"https://api.github.com/repos/{src.GithubRepo}/releases/tags/{src.Version}";

        using var req = new HttpRequestMessage(HttpMethod.Get, api);
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;

        var pattern = new Regex(src.AssetPattern, RegexOptions.IgnoreCase);
        var assets = root.GetProperty("assets").EnumerateArray().ToList();
        var asset = assets.FirstOrDefault(a => pattern.IsMatch(a.GetProperty("name").GetString() ?? ""));
        if (asset.ValueKind == JsonValueKind.Undefined)
            throw new InvalidOperationException($"Ningún asset de {src.GithubRepo} coincide con '{src.AssetPattern}'.");

        var name = asset.GetProperty("name").GetString()!;
        var url = asset.GetProperty("browser_download_url").GetString()!;
        var size = asset.GetProperty("size").GetInt64();

        // Orden de confianza del hash: 1) fijado en profiles.json, 2) digest de GitHub,
        // 3) asset <name>.sha256, 4) sha256sum.txt, 5) tabla de hashes en las notas del release.
        string? sha = src.Sha256;
        if (string.IsNullOrWhiteSpace(sha) && asset.TryGetProperty("digest", out var dg) && dg.GetString() is { } digest
            && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            sha = digest["sha256:".Length..];

        if (string.IsNullOrWhiteSpace(sha))
        {
            var side = assets.FirstOrDefault(a => a.GetProperty("name").GetString() == name + ".sha256");
            if (side.ValueKind != JsonValueKind.Undefined)
                sha = ExtractHash(await _http.GetStringAsync(side.GetProperty("browser_download_url").GetString(), ct), name);
        }
        if (string.IsNullOrWhiteSpace(sha))
        {
            var sums = assets.FirstOrDefault(a => (a.GetProperty("name").GetString() ?? "").Equals("sha256sum.txt", StringComparison.OrdinalIgnoreCase));
            if (sums.ValueKind != JsonValueKind.Undefined)
                sha = ExtractHash(await _http.GetStringAsync(sums.GetProperty("browser_download_url").GetString(), ct), name);
        }
        if (string.IsNullOrWhiteSpace(sha) && root.TryGetProperty("body", out var body))
            sha = ExtractHash(body.GetString() ?? "", name);

        if (string.IsNullOrWhiteSpace(sha) || !Regex.IsMatch(sha, "^[0-9a-fA-F]{64}$"))
            throw new InvalidOperationException(
                $"No se pudo obtener un SHA256 confiable para {name}. Fíjalo en profiles.json → tools.*.sha256.");

        return new ResolvedAsset(name, url, size, sha.ToLowerInvariant());
    }

    /// <summary>Busca un hash hex de 64 caracteres en la misma línea que el nombre del archivo (o el único hash del texto).</summary>
    private static string? ExtractHash(string text, string fileName)
    {
        foreach (var line in text.Split('\n'))
            if (line.Contains(fileName, StringComparison.OrdinalIgnoreCase))
            {
                var m = Regex.Match(line, @"\b[0-9a-fA-F]{64}\b");
                if (m.Success) return m.Value;
            }
        var all = Regex.Matches(text, @"\b[0-9a-fA-F]{64}\b");
        return all.Count == 1 ? all[0].Value : null;
    }

    private async Task DownloadVerifiedAsync(string url, string dest, string expectedHex, HashAlgorithmName algo, string label, CancellationToken ct)
    {
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Se rechaza descarga no-HTTPS: {url}");

        var part = dest + ".part";
        using (var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength;
            using var hasher = IncrementalHash.CreateHash(algo);
            await using (var input = await resp.Content.ReadAsStreamAsync(ct))
            await using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
            {
                var buffer = new byte[1 << 20];
                long read = 0;
                var lastReport = DateTime.MinValue;
                int n;
                while ((n = await input.ReadAsync(buffer, ct)) > 0)
                {
                    hasher.AppendData(buffer, 0, n);
                    await output.WriteAsync(buffer.AsMemory(0, n), ct);
                    read += n;
                    if (DateTime.UtcNow - lastReport > TimeSpan.FromMilliseconds(500))
                    {
                        lastReport = DateTime.UtcNow;
                        double? pct = total > 0 ? read * 100.0 / total.Value : null;
                        Report(label, pct, $"Descargando {label}: {read / 1048576.0:F0} MB{(total > 0 ? $" / {total.Value / 1048576.0:F0} MB" : "")}");
                    }
                }
            }

            var actual = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
            if (!actual.Equals(expectedHex.Trim().ToLowerInvariant(), StringComparison.Ordinal))
            {
                TryDelete(part);
                throw new CryptographicException($"Integridad inválida para {Path.GetFileName(dest)}: esperado {expectedHex}, obtenido {actual}.");
            }
        }
        File.Move(part, dest, overwrite: true);
        Report(label, 100, $"{label}: integridad {algo.Name} verificada.");
    }

    /// <summary>Extrae a "{target}.extracting" y renombra; ZipFile ya bloquea rutas fuera del destino (zip-slip).</summary>
    private static void ExtractAtomically(string zipPath, string targetDir)
    {
        var staging = targetDir + ".extracting";
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        ZipFile.ExtractToDirectory(zipPath, staging);

        // Si el ZIP trae una única carpeta raíz (p.ej. uv-x86_64-pc-windows-msvc\uv.exe), aplanarla.
        var entries = Directory.GetFileSystemEntries(staging);
        var contentRoot = entries.Length == 1 && Directory.Exists(entries[0]) ? entries[0] : staging;

        if (Directory.Exists(targetDir))
        {
            var old = targetDir + ".old";
            if (Directory.Exists(old)) Directory.Delete(old, true);
            Directory.Move(targetDir, old);
            TryDeleteDir(old);
        }
        Directory.Move(contentRoot, targetDir);
        if (contentRoot != staging) TryDeleteDir(staging);
    }

    private static void CleanupStaleTemp()
    {
        foreach (var d in Directory.GetDirectories(PortablePaths.Bin).Where(d => d.EndsWith(".extracting") || d.EndsWith(".old")))
            TryDeleteDir(d);
        foreach (var f in Directory.GetFiles(PortablePaths.Downloads, "*.part"))
            TryDelete(f);
    }

    public static string? FindOnPath(string exe)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* entradas de PATH malformadas */ }
        }
        return null;
    }

    private void Report(string stage, double? pct, string msg)
    {
        Log.Info($"[bootstrap] {msg}");
        Progress?.Invoke(new BootstrapProgress(stage, pct, msg));
    }

    private static void TryDelete(string f) { try { File.Delete(f); } catch { } }
    private static void TryDeleteDir(string d) { try { Directory.Delete(d, true); } catch { } }
}

/// <summary>Invocación de uv.exe portable sobre repo\python con el venv en env\.venv.</summary>
public static class UvRunner
{
    private static string Project => Path.Combine(PortablePaths.Repo, "python");

    public static Task<ProcessResult> SyncAsync(CancellationToken ct) =>
        ProcessRunner.RunAsync(PortablePaths.UvExe, new[] { "sync", "--project", Project }, Project,
            timeout: TimeSpan.FromMinutes(15), ct: ct);

    /// <summary>Ejecuta un script Python del repo (hooks de post-sincronización, utilidades).</summary>
    public static Task<ProcessResult> RunScriptAsync(string scriptRelative, IEnumerable<string> args, CancellationToken ct)
    {
        var script = Path.GetFullPath(Path.Combine(Project, scriptRelative));
        if (!script.StartsWith(Project, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("El script debe estar dentro de repo\\python.");
        return ProcessRunner.RunAsync(PortablePaths.UvExe,
            new[] { "run", "--project", Project, "python", script }.Concat(args), Project,
            timeout: TimeSpan.FromMinutes(5), ct: ct);
    }
}
