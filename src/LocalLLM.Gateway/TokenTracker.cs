using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace LocalLLM.Gateway;

public sealed class RequestRecord
{
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public string ClientApp { get; set; } = "unknown";
    public string Endpoint { get; set; } = "";
    public string? Model { get; set; }
    public int Status { get; set; }
    public bool Stream { get; set; }
    public long PromptTokens { get; set; }
    public long CompletionTokens { get; set; }
    public double? TtftMs { get; set; }
    public double TotalMs { get; set; }
    public double? TokensPerSecond { get; set; }
    public double? QueueMs { get; set; }
    public bool Rejected { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Telemetría exacta de tokens: los registros se encolan en un Channel&lt;T&gt; acotado (sin bloquear el
/// hilo de la petición) y un escritor único los inserta por lotes en SQLite en modo WAL.
/// </summary>
public sealed class TokenTracker : IAsyncDisposable
{
    private readonly Channel<RequestRecord> _channel = Channel.CreateBounded<RequestRecord>(
        new BoundedChannelOptions(20_000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly string _cs;
    private readonly Task _writer;

    public TokenTracker()
    {
        Directory.CreateDirectory(PortablePaths.Data);
        _cs = new SqliteConnectionStringBuilder { DataSource = PortablePaths.TelemetryDb, Cache = SqliteCacheMode.Shared, Pooling = true }.ToString();
        InitSchema();
        _writer = Task.Run(WriterLoopAsync);
    }

    public void Record(RequestRecord r) => _channel.Writer.TryWrite(r);

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_cs);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout=5000;";
        cmd.ExecuteNonQuery();
        return c;
    }

    private void InitSchema()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            CREATE TABLE IF NOT EXISTS requests (
                id                INTEGER PRIMARY KEY AUTOINCREMENT,
                ts_utc            TEXT    NOT NULL,
                client_app        TEXT    NOT NULL,
                endpoint          TEXT    NOT NULL,
                model             TEXT,
                status            INTEGER NOT NULL,
                stream            INTEGER NOT NULL,
                prompt_tokens     INTEGER NOT NULL DEFAULT 0,
                completion_tokens INTEGER NOT NULL DEFAULT 0,
                ttft_ms           REAL,
                total_ms          REAL    NOT NULL,
                tokens_per_sec    REAL,
                queue_ms          REAL,
                rejected          INTEGER NOT NULL DEFAULT 0,
                error             TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_requests_ts     ON requests(ts_utc);
            CREATE INDEX IF NOT EXISTS ix_requests_client ON requests(client_app, ts_utc);
            CREATE INDEX IF NOT EXISTS ix_requests_model  ON requests(model, ts_utc);
            """;
        cmd.ExecuteNonQuery();
    }

    private async Task WriterLoopAsync()
    {
        var batch = new List<RequestRecord>(256);
        var reader = _channel.Reader;
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            batch.Clear();
            while (batch.Count < 256 && reader.TryRead(out var r)) batch.Add(r);
            try { InsertBatch(batch); }
            catch (Exception ex) { Log.Error("[telemetry] Error insertando lote", ex); }
        }
    }

    private void InsertBatch(List<RequestRecord> batch)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO requests (ts_utc, client_app, endpoint, model, status, stream, prompt_tokens, completion_tokens,
                                  ttft_ms, total_ms, tokens_per_sec, queue_ms, rejected, error)
            VALUES ($ts, $client, $ep, $model, $status, $stream, $pt, $ct, $ttft, $total, $tps, $queue, $rej, $err);
            """;
        var p = new Dictionary<string, SqliteParameter>();
        foreach (var n in new[] { "$ts", "$client", "$ep", "$model", "$status", "$stream", "$pt", "$ct", "$ttft", "$total", "$tps", "$queue", "$rej", "$err" })
            p[n] = cmd.Parameters.Add(new SqliteParameter { ParameterName = n });

        foreach (var r in batch)
        {
            p["$ts"].Value = r.TimestampUtc.ToString("O");
            p["$client"].Value = r.ClientApp;
            p["$ep"].Value = r.Endpoint;
            p["$model"].Value = (object?)r.Model ?? DBNull.Value;
            p["$status"].Value = r.Status;
            p["$stream"].Value = r.Stream ? 1 : 0;
            p["$pt"].Value = r.PromptTokens;
            p["$ct"].Value = r.CompletionTokens;
            p["$ttft"].Value = (object?)r.TtftMs ?? DBNull.Value;
            p["$total"].Value = r.TotalMs;
            p["$tps"].Value = (object?)r.TokensPerSecond ?? DBNull.Value;
            p["$queue"].Value = (object?)r.QueueMs ?? DBNull.Value;
            p["$rej"].Value = r.Rejected ? 1 : 0;
            p["$err"].Value = (object?)r.Error ?? DBNull.Value;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    // ------------------------------------------------------------------ GET /v1/metrics

    public object QueryMetrics(DateTime sinceUtc, int recent)
    {
        using var c = Open();
        var since = sinceUtc.ToString("O");

        List<Dictionary<string, object?>> Rows(string sql)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$since", since);
            cmd.Parameters.AddWithValue("$recent", recent);
            using var rd = cmd.ExecuteReader();
            var list = new List<Dictionary<string, object?>>();
            while (rd.Read())
            {
                var row = new Dictionary<string, object?>();
                for (int i = 0; i < rd.FieldCount; i++) row[rd.GetName(i)] = rd.IsDBNull(i) ? null : rd.GetValue(i);
                list.Add(row);
            }
            return list;
        }

        const string agg = """
            COUNT(*) AS requests,
            SUM(rejected) AS rejected,
            SUM(CASE WHEN status >= 400 THEN 1 ELSE 0 END) AS errors,
            SUM(prompt_tokens) AS prompt_tokens,
            SUM(completion_tokens) AS completion_tokens,
            ROUND(AVG(ttft_ms), 1) AS avg_ttft_ms,
            ROUND(AVG(tokens_per_sec), 2) AS avg_tokens_per_sec,
            ROUND(AVG(total_ms), 1) AS avg_total_ms
            """;

        return new
        {
            since = sinceUtc,
            totals = Rows($"SELECT {agg} FROM requests WHERE ts_utc >= $since").FirstOrDefault(),
            byClientApp = Rows($"SELECT client_app, {agg} FROM requests WHERE ts_utc >= $since GROUP BY client_app ORDER BY requests DESC"),
            byModel = Rows($"SELECT model, {agg} FROM requests WHERE ts_utc >= $since AND model IS NOT NULL GROUP BY model ORDER BY requests DESC"),
            byEndpoint = Rows($"SELECT endpoint, {agg} FROM requests WHERE ts_utc >= $since GROUP BY endpoint ORDER BY requests DESC"),
            recent = Rows("SELECT * FROM requests ORDER BY id DESC LIMIT $recent")
        };
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        try { await _writer.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
    }
}

/// <summary>
/// Inspector de respuestas en streaming. Recibe los bytes tal cual salen de Ollama (SSE "data: {...}" para
/// /v1/*, NDJSON para /api/*), los divide en líneas sin alterar el flujo y extrae los conteos REALES de tokens
/// del último chunk (usage / prompt_eval_count / eval_count), además del TTFT.
/// </summary>
public sealed class UsageInspector
{
    private readonly List<byte> _pending = new();
    private readonly DateTime _start;
    private readonly bool _bufferWholeBody;
    private readonly MemoryStream? _whole;

    public double? TtftMs { get; private set; }
    public DateTime? FirstTokenAt { get; private set; }
    public long PromptTokens { get; private set; }
    public long CompletionTokens { get; private set; }
    public double? EvalSeconds { get; private set; }
    public string? Model { get; private set; }

    public UsageInspector(DateTime start, string? contentType)
    {
        _start = start;
        // JSON no streaming: puede venir multilínea, se analiza completo al final.
        _bufferWholeBody = contentType?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true;
        if (_bufferWholeBody) _whole = new MemoryStream();
    }

    public void Feed(ReadOnlySpan<byte> chunk)
    {
        if (_bufferWholeBody) { _whole!.Write(chunk); MarkFirstToken(); return; }
        foreach (var b in chunk)
        {
            if (b == (byte)'\n')
            {
                ProcessLine(Encoding.UTF8.GetString(_pending.ToArray()));
                _pending.Clear();
            }
            else _pending.Add(b);
        }
    }

    public void Complete()
    {
        if (_bufferWholeBody) { ProcessJson(Encoding.UTF8.GetString(_whole!.ToArray()), isLine: false); return; }
        if (_pending.Count > 0) ProcessLine(Encoding.UTF8.GetString(_pending.ToArray()));
        _pending.Clear();
    }

    private void ProcessLine(string line)
    {
        line = line.Trim();
        if (line.Length == 0 || line.StartsWith(':')) return;           // comentarios SSE / keep-alive
        if (line.StartsWith("data:")) line = line[5..].Trim();
        if (line == "[DONE]" || !line.StartsWith('{')) return;
        ProcessJson(line, isLine: true);
    }

    private void ProcessJson(string json, bool isLine)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return;

            if (r.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String) Model ??= m.GetString();

            // Primer token con contenido real
            if (isLine && TtftMs is null && HasContent(r)) MarkFirstToken();

            // OpenAI: usage en el último chunk (stream_options.include_usage) o en la respuesta completa
            if (r.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
            {
                if (u.TryGetProperty("prompt_tokens", out var pt)) PromptTokens = pt.GetInt64();
                if (u.TryGetProperty("completion_tokens", out var ct)) CompletionTokens = ct.GetInt64();
            }

            // Ollama nativo: chunk final done=true con conteos y duraciones (ns)
            if (r.TryGetProperty("prompt_eval_count", out var pec)) PromptTokens = pec.GetInt64();
            if (r.TryGetProperty("eval_count", out var ec)) CompletionTokens = ec.GetInt64();
            if (r.TryGetProperty("eval_duration", out var ed) && ed.TryGetInt64(out var ns) && ns > 0) EvalSeconds = ns / 1e9;
        }
        catch (JsonException) { /* chunk parcial o no-JSON: se ignora sin romper el stream */ }
    }

    private static bool HasContent(JsonElement r)
    {
        if (r.TryGetProperty("response", out var resp) && resp.ValueKind == JsonValueKind.String && resp.GetString()!.Length > 0) return true;
        if (r.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.Object &&
            (NonEmpty(msg, "content") || NonEmpty(msg, "thinking") || msg.TryGetProperty("tool_calls", out _))) return true;
        if (r.TryGetProperty("choices", out var ch) && ch.ValueKind == JsonValueKind.Array)
            foreach (var c in ch.EnumerateArray())
            {
                if (c.TryGetProperty("delta", out var d) && (NonEmpty(d, "content") || NonEmpty(d, "reasoning") || d.TryGetProperty("tool_calls", out _))) return true;
                if (c.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String && t.GetString()!.Length > 0) return true;
            }
        if (r.TryGetProperty("embedding", out _) || r.TryGetProperty("embeddings", out _) || r.TryGetProperty("data", out _)) return true;
        return false;
    }

    private static bool NonEmpty(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String && v.GetString()!.Length > 0;

    private void MarkFirstToken()
    {
        if (TtftMs != null) return;
        FirstTokenAt = DateTime.UtcNow;
        TtftMs = (FirstTokenAt.Value - _start).TotalMilliseconds;
    }

    public double? TokensPerSecond(DateTime end)
    {
        if (CompletionTokens <= 0) return null;
        if (EvalSeconds is > 0) return Math.Round(CompletionTokens / EvalSeconds.Value, 2);
        if (FirstTokenAt is { } ft && (end - ft).TotalSeconds > 0.01) return Math.Round(CompletionTokens / (end - ft).TotalSeconds, 2);
        return null;
    }
}
