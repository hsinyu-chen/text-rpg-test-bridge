using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;

string? certDirOverride = null;
int httpPort = 5051;
int wssPort = 5050;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--cert-dir":
            certDirOverride = Path.GetFullPath(args[++i]);
            break;
        case "--http-port":
            httpPort = int.Parse(args[++i]);
            break;
        case "--wss-port":
            wssPort = int.Parse(args[++i]);
            break;
        case "--help":
        case "-h":
            PrintUsage();
            return 0;
    }
}

// Default search order: explicit override → cwd/../TextRPG/.certs → exe/../TextRPG/.certs.
// `dotnet run` puts cwd at the project folder, but a published exe lives several dirs deeper,
// so we try both and pick the first that has both PEM files.
string[] candidates = certDirOverride is not null
    ? new[] { certDirOverride }
    : new[]
    {
        Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "..", "TextRPG", ".certs")),
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "TextRPG", ".certs")),
    };

string? certDir = null;
string? certPath = null;
string? keyPath = null;
foreach (var dir in candidates)
{
    var c = Path.Combine(dir, "dev.pem");
    var k = Path.Combine(dir, "dev-key.pem");
    if (File.Exists(c) && File.Exists(k))
    {
        certDir = dir;
        certPath = c;
        keyPath = k;
        break;
    }
}

if (certDir is null || certPath is null || keyPath is null)
{
    Console.Error.WriteLine("[bridge] cert not found. Searched:");
    foreach (var dir in candidates) Console.Error.WriteLine($"  {dir}");
    Console.Error.WriteLine("[bridge] run TextRPG `npm start` once to generate dev.pem / dev-key.pem, or pass --cert-dir <path>.");
    return 2;
}

X509Certificate2 cert;
try
{
    using var pemCert = X509Certificate2.CreateFromPemFile(certPath, keyPath);
    // PFX round-trip — Kestrel needs the key marshalled into a usable container on Windows.
    cert = X509CertificateLoader.LoadPkcs12(pemCert.Export(X509ContentType.Pfx), password: null);
}
catch (Exception e)
{
    Console.Error.WriteLine($"[bridge] failed to load cert: {e.Message}");
    return 3;
}

var builder = WebApplication.CreateBuilder();
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
builder.WebHost.ConfigureKestrel(options =>
{
    options.Listen(IPAddress.Loopback, httpPort);
    options.Listen(IPAddress.Loopback, wssPort, listen => listen.UseHttps(cert));
});

var state = new BridgeState(wssPort);
builder.Services.AddSingleton(state);

var app = builder.Build();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });

app.MapPost("/send", async (HttpContext ctx, BridgeState s) =>
{
    var body = await ReadJsonObject(ctx);
    return await s.EnqueueAsync("send_action", body, ctx.RequestAborted);
});

app.MapPost("/list", async (HttpContext ctx, BridgeState s) =>
{
    var body = await ReadJsonObject(ctx);
    return await s.EnqueueAsync("list", body, ctx.RequestAborted);
});

app.MapPost("/delete", async (HttpContext ctx, BridgeState s) =>
{
    var body = await ReadJsonObject(ctx);
    return await s.EnqueueAsync("delete", body, ctx.RequestAborted);
});

app.MapPost("/reload", async (HttpContext ctx, BridgeState s) =>
{
    var body = await ReadJsonObject(ctx);
    return await s.EnqueueAsync("reload", body, ctx.RequestAborted);
});

// Profile mgmt — list / inspect-active / switch.
app.MapPost("/profile/list", async (HttpContext ctx, BridgeState s) =>
{
    var body = await ReadJsonObject(ctx);
    return await s.EnqueueAsync("profile_list", body, ctx.RequestAborted);
});

app.MapPost("/profile/active", async (HttpContext ctx, BridgeState s) =>
{
    var body = await ReadJsonObject(ctx);
    return await s.EnqueueAsync("profile_get_active", body, ctx.RequestAborted);
});

app.MapPost("/profile/switch", async (HttpContext ctx, BridgeState s) =>
{
    var body = await ReadJsonObject(ctx);
    return await s.EnqueueAsync("profile_switch", body, ctx.RequestAborted);
});

// LLM profile mgmt — list / active / switch. Distinct from prompt profiles:
// these select which model + API the engine calls (local llama.cpp vs Gemini /
// OpenAI / etc). Switching to a non-local profile via /llm/switch requires
// `confirmPaid: true` in the body — guards against accidentally driving turns
// through a paid model.
app.MapPost("/llm/list", async (HttpContext ctx, BridgeState s) =>
{
    var body = await ReadJsonObject(ctx);
    return await s.EnqueueAsync("llm_list", body, ctx.RequestAborted);
});

app.MapPost("/llm/active", async (HttpContext ctx, BridgeState s) =>
{
    var body = await ReadJsonObject(ctx);
    return await s.EnqueueAsync("llm_get_active", body, ctx.RequestAborted);
});

app.MapPost("/llm/switch", async (HttpContext ctx, BridgeState s) =>
{
    var body = await ReadJsonObject(ctx);
    return await s.EnqueueAsync("llm_switch", body, ctx.RequestAborted);
});

// Book KB recovery — fill in scenario files the active Book never loaded.
// Only adds missing filenames per the named scenario; existing KB entries
// are preserved. Use when scenarios.json had stale filenames at book-create
// time and the engine silently dropped the 404s.
app.MapPost("/book/repair-kb", async (HttpContext ctx, BridgeState s) =>
{
    var body = await ReadJsonObject(ctx);
    return await s.EnqueueAsync("book_repair_kb", body, ctx.RequestAborted);
});

// Engine config — read or set engineMode + outputLanguage from outside the UI.
app.MapPost("/config/get", async (HttpContext ctx, BridgeState s) =>
{
    var body = await ReadJsonObject(ctx);
    return await s.EnqueueAsync("config_get", body, ctx.RequestAborted);
});

app.MapPost("/config/set", async (HttpContext ctx, BridgeState s) =>
{
    var body = await ReadJsonObject(ctx);
    return await s.EnqueueAsync("config_set", body, ctx.RequestAborted);
});

// Knowledge-base files — list loaded files / read one. The app holds the
// active book's KB in memory (state.loadedFiles); these endpoints surface
// it so an agent can inspect the same content the engine sees, without
// poking IndexedDB or the disk-sync layer.
app.MapPost("/kb/list", async (HttpContext ctx, BridgeState s) =>
{
    var body = await ReadJsonObject(ctx);
    return await s.EnqueueAsync("kb_list", body, ctx.RequestAborted);
});

app.MapPost("/kb/read", async (HttpContext ctx, BridgeState s) =>
{
    var body = await ReadJsonObject(ctx);
    return await s.EnqueueAsync("kb_read", body, ctx.RequestAborted);
});

// Book mgmt — list books, inspect-active, fork active to a new sibling Book
// truncated at a message, switch active. Symmetric with /profile/* — book
// switching is the playthrough-level analogue of profile switching.
app.MapPost("/book/list", async (HttpContext ctx, BridgeState s) =>
{
    var body = await ReadJsonObject(ctx);
    return await s.EnqueueAsync("book_list", body, ctx.RequestAborted);
});

app.MapPost("/book/active", async (HttpContext ctx, BridgeState s) =>
{
    var body = await ReadJsonObject(ctx);
    return await s.EnqueueAsync("book_get_active", body, ctx.RequestAborted);
});

app.MapPost("/book/fork", async (HttpContext ctx, BridgeState s) =>
{
    var body = await ReadJsonObject(ctx);
    return await s.EnqueueAsync("book_fork", body, ctx.RequestAborted);
});

app.MapPost("/book/switch", async (HttpContext ctx, BridgeState s) =>
{
    var body = await ReadJsonObject(ctx);
    return await s.EnqueueAsync("book_switch", body, ctx.RequestAborted);
});

app.Map("/app", async (HttpContext ctx, BridgeState s) =>
{
    if (ctx.Connection.LocalPort != wssPort)
    {
        ctx.Response.StatusCode = 404;
        return;
    }
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsync("expected websocket upgrade");
        return;
    }
    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    await s.HandleAppConnectionAsync(ws, ctx.RequestAborted);
});

Console.WriteLine($"[bridge] http  listening on http://127.0.0.1:{httpPort}  (agent → /send /list /delete /reload /book/*)");
Console.WriteLine($"[bridge] wss   listening on wss://127.0.0.1:{wssPort}/app  (app  → WebSocket)");
Console.WriteLine($"[bridge] cert  {certPath}");

await app.RunAsync();
return 0;

static async Task<JsonObject> ReadJsonObject(HttpContext ctx)
{
    if (ctx.Request.ContentLength == 0) return new JsonObject();
    try
    {
        var node = await JsonNode.ParseAsync(ctx.Request.Body);
        return (node as JsonObject) ?? new JsonObject();
    }
    catch
    {
        return new JsonObject();
    }
}

static void PrintUsage()
{
    Console.WriteLine("BridgeServer — TextRPG dev relay");
    Console.WriteLine();
    Console.WriteLine("Usage: BridgeServer [--cert-dir <path>] [--http-port <n>] [--wss-port <n>]");
    Console.WriteLine();
    Console.WriteLine("Defaults:");
    Console.WriteLine("  --cert-dir   ../TextRPG/.certs  (relative to executable)");
    Console.WriteLine("  --http-port  5051               (plain http, agent-facing)");
    Console.WriteLine("  --wss-port   5050               (https/wss, app-facing)");
}

sealed class BridgeState(int wssPort)
{
    private readonly object _lock = new();
    private WebSocket? _activeWs;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode>> _pending = new();
    private readonly SemaphoreSlim _slot = new(1, 1);
    private int _queueDepth;
    private const int MaxQueue = 5;
    // Two-call mode (resolver + narrator) on a slow local model can run 3-5
    // minutes; raise the WS round-trip ceiling so the agent doesn't have to
    // poll just to wait out a single turn.
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(600);
    public int WssPort { get; } = wssPort;

    public async Task<IResult> EnqueueAsync(string type, JsonObject payload, CancellationToken ct)
    {
        bool counted = false;
        try
        {
            lock (_lock)
            {
                if (_activeWs is null || _activeWs.State != WebSocketState.Open)
                    return Results.Json(new { error = "app_not_connected" }, statusCode: 503);
                if (_queueDepth >= MaxQueue)
                    return Results.Json(new { error = "queue_full" }, statusCode: 429);
                _queueDepth++;
                counted = true;
            }

            await _slot.WaitAsync(ct);
            try
            {
                WebSocket? ws;
                lock (_lock) ws = _activeWs;
                if (ws is null || ws.State != WebSocketState.Open)
                    return Results.Json(new { error = "app_not_connected" }, statusCode: 503);

                var requestId = Guid.NewGuid().ToString("N");
                var msg = new JsonObject { ["type"] = type, ["requestId"] = requestId };
                foreach (var kv in payload)
                {
                    if (kv.Key is "type" or "requestId") continue;
                    msg[kv.Key] = kv.Value?.DeepClone();
                }

                var tcs = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pending[requestId] = tcs;
                try
                {
                    var bytes = Encoding.UTF8.GetBytes(msg.ToJsonString());
                    await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);

                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(RequestTimeout);
                    using var reg = cts.Token.Register(() => tcs.TrySetCanceled());

                    JsonNode response;
                    try
                    {
                        response = await tcs.Task;
                    }
                    catch (OperationCanceledException)
                    {
                        if (ct.IsCancellationRequested) throw;
                        // App could have disconnected mid-flight; surface a clearer code.
                        WebSocket? still;
                        lock (_lock) still = _activeWs;
                        if (still is null || still.State != WebSocketState.Open)
                            return Results.Json(new { error = "app_not_connected" }, statusCode: 503);
                        return Results.Json(new { error = "app_timeout" }, statusCode: 504);
                    }

                    var responseType = response["type"]?.GetValue<string>();
                    if (responseType == "action_error")
                    {
                        var detail = response["error"]?.GetValue<string>() ?? "unknown";
                        if (detail == "app_disconnected")
                            return Results.Json(new { error = "app_not_connected" }, statusCode: 503);
                        return Results.Json(new { error = "app_error", detail }, statusCode: 500);
                    }

                    if (response is JsonObject obj)
                    {
                        obj.Remove("type");
                        obj.Remove("requestId");
                        return Results.Json(obj);
                    }
                    return Results.Json(response);
                }
                finally
                {
                    _pending.TryRemove(requestId, out _);
                }
            }
            finally
            {
                _slot.Release();
            }
        }
        finally
        {
            if (counted) Interlocked.Decrement(ref _queueDepth);
        }
    }

    public async Task HandleAppConnectionAsync(WebSocket ws, CancellationToken ct)
    {
        WebSocket? old;
        lock (_lock)
        {
            old = _activeWs;
            _activeWs = ws;
        }
        if (old is not null)
        {
            try { await old.CloseAsync(WebSocketCloseStatus.NormalClosure, "replaced", CancellationToken.None); }
            catch { }
        }
        Console.WriteLine("[bridge] app connected");

        var buffer = new byte[64 * 1024];
        var sb = new StringBuilder();
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                sb.Clear();
                WebSocketReceiveResult res;
                do
                {
                    res = await ws.ReceiveAsync(buffer, ct);
                    if (res.MessageType == WebSocketMessageType.Close)
                    {
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                        return;
                    }
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, res.Count));
                }
                while (!res.EndOfMessage);

                if (sb.Length == 0) continue;
                JsonNode? node;
                try { node = JsonNode.Parse(sb.ToString()); }
                catch { Console.WriteLine($"[bridge] invalid json from app: {sb}"); continue; }
                if (node is null) continue;

                var type = node["type"]?.GetValue<string>();
                switch (type)
                {
                    case "heartbeat":
                        break;
                    case "hello":
                        Console.WriteLine($"[bridge] hello: {node.ToJsonString()}");
                        break;
                    default:
                        var requestId = node["requestId"]?.GetValue<string>();
                        if (requestId is not null && _pending.TryGetValue(requestId, out var tcs))
                        {
                            tcs.TrySetResult(node);
                        }
                        else
                        {
                            Console.WriteLine($"[bridge] unmatched frame: {sb}");
                        }
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException e) { Console.WriteLine($"[bridge] ws closed: {e.WebSocketErrorCode}"); }
        catch (Exception e) { Console.WriteLine($"[bridge] ws error: {e.Message}"); }
        finally
        {
            lock (_lock) { if (_activeWs == ws) _activeWs = null; }
            // Surface disconnect to any in-flight request as a synthetic error frame.
            var disconnectFrame = new JsonObject { ["type"] = "action_error", ["error"] = "app_disconnected" };
            foreach (var kv in _pending)
                kv.Value.TrySetResult(disconnectFrame.DeepClone());
            Console.WriteLine("[bridge] app disconnected");
        }
    }
}
