using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LocalLLM.Gateway;

/// <summary>
/// Compuerta de admisión: limita la concurrencia hacia Ollama (num_parallel), mantiene una cola acotada
/// (429 si se llena) y permite "graceful drain" (pausa nuevas peticiones y espera a que terminen las activas)
/// para reinicios de Ollama o reemplazo del ejecutable sin cortar respuestas en curso.
/// </summary>
public sealed class RequestGate
{
    private SemaphoreSlim _slots;
    private int _capacity;
    private int _queued, _active;
    private volatile TaskCompletionSource _resume = CompletedTcs();
    private volatile bool _paused;

    public RequestGate(int parallel) { _capacity = Math.Max(1, parallel); _slots = new SemaphoreSlim(_capacity, _capacity); }

    public int Active => Volatile.Read(ref _active);
    public int Queued => Volatile.Read(ref _queued);
    public bool Paused => _paused;
    public int Capacity => _capacity;

    public sealed class Lease : IDisposable
    {
        private RequestGate? _g; private readonly SemaphoreSlim _s;
        internal Lease(RequestGate g, SemaphoreSlim s) { _g = g; _s = s; }
        public void Dispose()
        {
            var g = Interlocked.Exchange(ref _g, null);
            if (g is null) return;
            Interlocked.Decrement(ref g._active);
            _s.Release();
        }
    }

    /// <summary>Devuelve null si la cola está llena o se agotó la espera.</summary>
    public async Task<Lease?> EnterAsync(int maxQueue, TimeSpan timeout, CancellationToken ct)
    {
        if (Interlocked.Increment(ref _queued) > maxQueue) { Interlocked.Decrement(ref _queued); return null; }
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            while (true)
            {
                await _resume.Task.WaitAsync(cts.Token);              // espera si hay drain en curso
                var slots = _slots;
                if (!await slots.WaitAsync(Timeout.Infinite, cts.Token).ContinueWith(t => !t.IsCanceled && t.Result))
                    return null;
                if (_paused || !ReferenceEquals(slots, _slots)) { slots.Release(); continue; }
                Interlocked.Increment(ref _active);
                return new Lease(this, slots);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        finally { Interlocked.Decrement(ref _queued); }
    }

    /// <summary>Pausa la admisión y espera a que las peticiones activas terminen (o venza el timeout).</summary>
    public async Task<bool> PauseAndDrainAsync(TimeSpan timeout)
    {
        _resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _paused = true;
        var sw = Stopwatch.StartNew();
        while (Active > 0 && sw.Elapsed < timeout) await Task.Delay(100);
        Log.Info($"[gate] Drain {(Active == 0 ? "completo" : $"con {Active} activas tras timeout")} en {sw.ElapsedMilliseconds} ms.");
        return Active == 0;
    }

    public void Resume(int? newParallel = null)
    {
        if (newParallel is { } n && n != _capacity)
        {
            _capacity = Math.Max(1, n);
            _slots = new SemaphoreSlim(_capacity, _capacity); // las leases antiguas liberan el semáforo antiguo
        }
        _paused = false;
        _resume.TrySetResult();
    }

    private static TaskCompletionSource CompletedTcs() { var t = new TaskCompletionSource(); t.SetResult(); return t; }
}

/// <summary>
/// Gateway de inferencia: Kestrel enlazado SOLO a 127.0.0.1 (sin Admin, sin alerta de Firewall).
///  - :11434 HTTP/1.1 (+HTTP/2 vía ALPN no aplica sin TLS; compatible con todos los clientes OpenAI/Ollama)
///  - :11436 HTTP/2 h2c "prior knowledge" opcional para clientes gRPC/HTTP2 nativos
/// Proxy con streaming hacia ollama.exe (:11435), Memory Guard (429), cola de admisión y telemetría.
/// </summary>
public sealed class InferenceGateway : IAsyncDisposable
{
    private static readonly HashSet<string> TrackedPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/v1/chat/completions", "/v1/completions", "/v1/embeddings",
        "/api/chat", "/api/generate", "/api/embed", "/api/embeddings"
    };

    private static readonly HashSet<string> HopByHop = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Connection", "Transfer-Encoding", "Upgrade", "TE", "Trailer", "Host",
        "Content-Length", "Proxy-Authenticate", "Proxy-Authorization"
    };

    private readonly AppServices _s;
    private WebApplication? _app;

    public RequestGate Gate { get; }
    public string AdminToken { get; }

    public InferenceGateway(AppServices services)
    {
        _s = services;
        Gate = new RequestGate(services.Ollama.Profile.NumParallel);
        AdminToken = LoadOrCreateAdminToken();
    }

    public async Task StartAsync(CancellationToken ct)
    {
        var cfg = _s.Config.Gateway;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = PortablePaths.Root,
            EnvironmentName = Environments.Production
        });
        builder.Logging.ClearProviders();
        builder.Services.AddHostFiltering(o =>
        {
            // Defensa contra DNS rebinding: solo Host loopback.
            o.AllowedHosts = new[] { "127.0.0.1", "localhost", "[::1]" };
            o.AllowEmptyHosts = false;
        });
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Limits.MaxRequestBodySize = 256L * 1024 * 1024;    // prompts con imágenes base64
            k.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(5);
            k.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(30);
            k.Limits.Http2.MaxStreamsPerConnection = 256;
            var ip = IPAddress.Parse(cfg.ListenAddress);
            if (!IPAddress.IsLoopback(ip)) throw new InvalidOperationException("El Gateway solo puede escuchar en loopback (Zero-Admin / sin Firewall).");
            k.Listen(ip, cfg.Port, l => l.Protocols = HttpProtocols.Http1AndHttp2);
            if (cfg.H2cPort > 0) k.Listen(ip, cfg.H2cPort, l => l.Protocols = HttpProtocols.Http2);
        });

        var app = builder.Build();
        app.UseHostFiltering();
        MapEndpoints(app);
        await app.StartAsync(ct);
        _app = app;
        Log.Info($"[gateway] Escuchando en http://{cfg.ListenAddress}:{cfg.Port}" + (cfg.H2cPort > 0 ? $" y h2c://{cfg.ListenAddress}:{cfg.H2cPort}" : ""));
    }

    private void MapEndpoints(WebApplication app)
    {
        app.MapGet("/healthz", () => Results.Json(new
        {
            status = _s.Ollama.IsReady ? "ok" : "starting",
            version = typeof(InferenceGateway).Assembly.GetName().Version?.ToString(),
            pid = Environment.ProcessId,
            ollama = _s.Ollama.Version,
            model = _s.Ollama.Profile.Model
        }, statusCode: _s.Ollama.IsReady ? 200 : 503));

        // Ollama devuelve "Ollama is running" en "/": algunos clientes lo usan como sonda.
        app.MapGet("/", () => Results.Text("Ollama is running"));

        app.MapGet("/v1/metrics", (HttpContext ctx) =>
        {
            var since = DateTime.TryParse(ctx.Request.Query["since"], null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var s)
                ? s : DateTime.UtcNow.AddHours(-24);
            var recent = int.TryParse(ctx.Request.Query["recent"], out var r) ? Math.Clamp(r, 0, 1000) : 50;
            return Results.Json(new
            {
                telemetry = _s.Tokens.QueryMetrics(since, recent),
                live = new
                {
                    activeRequests = Gate.Active,
                    queuedRequests = Gate.Queued,
                    parallelSlots = Gate.Capacity,
                    draining = Gate.Paused,
                    memory = _s.Guard.Current
                }
            }, GatewayConfig.JsonOptions);
        });

        app.MapGet("/v1/hardware-profile", () => Results.Json(new
        {
            hardware = _s.Hardware,
            profile = _s.Ollama.Profile,
            memory = _s.Guard.Current,
            profilesFile = PortablePaths.ActiveProfilesFile,
            root = PortablePaths.Root
        }, GatewayConfig.JsonOptions));

        app.MapPost("/v1/system/git-sync", async (HttpContext ctx) =>
        {
            if (!IsAdmin(ctx)) return Results.Json(new { error = "Falta cabecera X-LocalLLM-Admin con el token de data\\admin-token.txt" }, statusCode: 401);
            var result = await _s.Updater.SyncAsync("api", ctx.RequestAborted);
            return Results.Json(result, GatewayConfig.JsonOptions);
        });

        // Todo lo demás (/api/tags, /api/show, /api/ps, /api/pull, /v1/models, ...) pasa tal cual a Ollama.
        app.Map("/{**path}", (RequestDelegate)ProxyAsync);
    }

    private bool IsAdmin(HttpContext ctx)
    {
        // Cabecera personalizada: obliga a preflight CORS en navegadores (que no respondemos) → anti-CSRF.
        var given = ctx.Request.Headers["X-LocalLLM-Admin"].ToString();
        return given.Length > 0 &&
               CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(AdminToken));
    }

    // ------------------------------------------------------------------ proxy + telemetría

    private async Task ProxyAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "/";
        bool tracked = TrackedPaths.Contains(path) && HttpMethods.IsPost(ctx.Request.Method);
        var started = DateTime.UtcNow;
        var record = tracked ? new RequestRecord { Endpoint = path, ClientApp = ResolveClientApp(ctx), TimestampUtc = started } : null;

        if (!_s.Ollama.IsReady && !Gate.Paused)
        {
            await WriteError(ctx, 503, "LocalLLM aún está iniciando el motor (descargando herramientas o modelo).", "engine_starting", retryAfter: 10);
            Finish(record, 503, started, error: "engine_starting");
            return;
        }

        RequestGate.Lease? lease = null;
        try
        {
            if (tracked)
            {
                if (await _s.Guard.TryRejectAsync(ctx))
                {
                    if (record != null) { record.Rejected = true; Finish(record, 429, started, error: "memory_guard"); }
                    return;
                }
                var cfg = _s.Config.Gateway;
                lease = await Gate.EnterAsync(cfg.MaxQueuedRequests, TimeSpan.FromSeconds(cfg.QueueTimeoutSeconds), ctx.RequestAborted);
                if (lease is null)
                {
                    await WriteError(ctx, 429, "Cola de inferencia llena o tiempo de espera agotado.", "queue_full", retryAfter: 2);
                    if (record != null) { record.Rejected = true; Finish(record, 429, started, error: "queue_full"); }
                    return;
                }
                record!.QueueMs = (DateTime.UtcNow - started).TotalMilliseconds;
            }

            using var upstreamReq = await BuildUpstreamRequestAsync(ctx, path, tracked, record);
            using var upstream = await _s.Ollama.Http.SendAsync(upstreamReq, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);

            ctx.Response.StatusCode = (int)upstream.StatusCode;
            foreach (var h in upstream.Headers.Concat(upstream.Content.Headers))
                if (!HopByHop.Contains(h.Key)) ctx.Response.Headers[h.Key] = h.Value.ToArray();
            ctx.Response.Headers["X-LocalLLM-Model"] = _s.Ollama.Profile.Model;

            var contentType = upstream.Content.Headers.ContentType?.MediaType;
            bool streaming = contentType is "text/event-stream" or "application/x-ndjson";
            if (streaming) ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();

            var inspector = tracked ? new UsageInspector(started, contentType) : null;
            await using var body = await upstream.Content.ReadAsStreamAsync(ctx.RequestAborted);
            var buffer = new byte[16 * 1024];
            int n;
            while ((n = await body.ReadAsync(buffer, ctx.RequestAborted)) > 0)
            {
                inspector?.Feed(buffer.AsSpan(0, n));
                await ctx.Response.Body.WriteAsync(buffer.AsMemory(0, n), ctx.RequestAborted);
                if (streaming) await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
            }

            if (record != null && inspector != null)
            {
                inspector.Complete();
                var end = DateTime.UtcNow;
                record.Stream = streaming;
                record.Model = inspector.Model ?? record.Model;
                record.PromptTokens = inspector.PromptTokens;
                record.CompletionTokens = inspector.CompletionTokens;
                record.TtftMs = inspector.TtftMs is { } t ? Math.Round(t - (record.QueueMs ?? 0), 1) : null;
                record.TokensPerSecond = inspector.TokensPerSecond(end);
                Finish(record, (int)upstream.StatusCode, started);
            }
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            Finish(record, 499, started, error: "client_disconnected");
        }
        catch (HttpRequestException ex)
        {
            Log.Warn($"[gateway] Upstream no disponible: {ex.Message}");
            if (!ctx.Response.HasStarted) await WriteError(ctx, 502, "Motor Ollama no disponible.", "upstream_unavailable", retryAfter: 5);
            Finish(record, 502, started, error: ex.Message);
        }
        catch (Exception ex)
        {
            Log.Error($"[gateway] Error en {path}", ex);
            if (!ctx.Response.HasStarted) await WriteError(ctx, 500, "Error interno del Gateway.", "gateway_error");
            Finish(record, 500, started, error: ex.Message);
        }
        finally
        {
            lease?.Dispose();
        }
    }

    private async Task<HttpRequestMessage> BuildUpstreamRequestAsync(HttpContext ctx, string path, bool tracked, RequestRecord? record)
    {
        var target = path.TrimStart('/') + ctx.Request.QueryString.Value;
        var req = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), target);

        foreach (var h in ctx.Request.Headers)
        {
            if (HopByHop.Contains(h.Key) || h.Key.StartsWith(':') || h.Key.Equals("X-LocalLLM-Admin", StringComparison.OrdinalIgnoreCase)) continue;
            req.Headers.TryAddWithoutValidation(h.Key, h.Value.ToArray());
        }

        if (HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method)) return req;

        if (tracked)
        {
            // Se lee el JSON para aplicar defaults del perfil y conocer modelo/stream.
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted);
            var bytes = ms.ToArray();
            try
            {
                if (JsonNode.Parse(bytes) is JsonObject json)
                {
                    ApplyProfileDefaults(json, path);
                    record!.Model = json["model"]?.GetValue<string>();
                    bytes = JsonSerializer.SerializeToUtf8Bytes(json);
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
            {
                /* cuerpo no-JSON o tipos inesperados: se reenvía intacto y Ollama responderá el error */
            }
            req.Content = new ByteArrayContent(bytes);
        }
        else
        {
            req.Content = new StreamContent(ctx.Request.Body);
        }

        if (ctx.Request.ContentType is { } ctype) req.Content.Headers.TryAddWithoutValidation("Content-Type", ctype);
        return req;
    }

    /// <summary>
    /// Aplica el perfil de hardware sin pisar lo que el cliente envió explícitamente:
    /// modelo por defecto, keep_alive, options.num_ctx / num_thread / num_gpu (API nativa) e
    /// stream_options.include_usage (API OpenAI con stream) para contar tokens exactos.
    /// </summary>
    private void ApplyProfileDefaults(JsonObject json, string path)
    {
        var p = _s.Ollama.Profile;
        if (json["model"] is null || string.IsNullOrWhiteSpace(json["model"]?.GetValue<string>()))
            json["model"] = p.Model;

        if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
        {
            json["keep_alive"] ??= _s.Config.Gateway.KeepAlive;
            if (json["options"] is not JsonObject opts) json["options"] = opts = new JsonObject();
            opts["num_ctx"] ??= p.NumCtx;
            if (p.NumThread is { } t) opts["num_thread"] ??= t;
            if (p.NumGpu is { } g) opts["num_gpu"] ??= g;
        }
        else if (_s.Config.Gateway.InjectStreamUsage && json["stream"]?.GetValue<bool>() == true &&
                 !path.EndsWith("/embeddings", StringComparison.OrdinalIgnoreCase))
        {
            if (json["stream_options"] is not JsonObject so) json["stream_options"] = so = new JsonObject();
            so["include_usage"] ??= true;
        }
    }

    private static string ResolveClientApp(HttpContext ctx)
    {
        var app = ctx.Request.Headers["X-Client-App"].ToString();
        if (string.IsNullOrWhiteSpace(app)) app = ctx.Request.Headers.UserAgent.ToString();
        if (string.IsNullOrWhiteSpace(app)) app = "unknown";
        return app.Length > 200 ? app[..200] : app;
    }

    private void Finish(RequestRecord? r, int status, DateTime started, string? error = null)
    {
        if (r is null) return;
        r.Status = status;
        r.Error ??= error;
        r.TotalMs = Math.Round((DateTime.UtcNow - started).TotalMilliseconds, 1);
        _s.Tokens.Record(r);
    }

    private static async Task WriteError(HttpContext ctx, int status, string message, string code, int? retryAfter = null)
    {
        ctx.Response.StatusCode = status;
        if (retryAfter is { } ra) ctx.Response.Headers.RetryAfter = ra.ToString();
        await ctx.Response.WriteAsJsonAsync(new { error = new { message, type = "localllm_gateway", code } });
    }

    private static string LoadOrCreateAdminToken()
    {
        try
        {
            if (File.Exists(PortablePaths.AdminTokenFile))
            {
                var t = File.ReadAllText(PortablePaths.AdminTokenFile).Trim();
                if (t.Length >= 32) return t;
            }
        }
        catch { }
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        File.WriteAllText(PortablePaths.AdminTokenFile, token);
        return token;
    }

    public async ValueTask DisposeAsync()
    {
        if (_app != null)
        {
            await _app.StopAsync(TimeSpan.FromSeconds(10)).ContinueWith(_ => { });
            await _app.DisposeAsync();
        }
    }
}
