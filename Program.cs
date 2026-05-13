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
    ? [certDirOverride]
    :
    [
        Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "..", "TextRPG", ".certs")),
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "TextRPG", ".certs")),
    ];

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

// Multi-client introspection — list currently-connected clientIds. Use to
// disambiguate when the agent is unsure which environment is live.
app.MapGet("/clients", (BridgeState s) =>
    Results.Json(new { clients = s.ListClients() }));

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

// File-agent LLM profile (separate axis from chat-side /llm/active+switch).
// Lets agents A/B-test different models on the file-agent without touching
// the UI's Settings dialog.
app.MapPost("/file-agent/active", async (HttpContext ctx, BridgeState s) =>
{
    var body = await ReadJsonObject(ctx);
    return await s.EnqueueAsync("file_agent_get_profile", body, ctx.RequestAborted);
});

app.MapPost("/file-agent/switch", async (HttpContext ctx, BridgeState s) =>
{
    var body = await ReadJsonObject(ctx);
    return await s.EnqueueAsync("file_agent_set_profile", body, ctx.RequestAborted);
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

// File-agent UI surface controls — open the file-viewer dialog with the
// agent panel pre-opened, or pop the chat-side agent panel. Both are dev-
// only convenience hooks so an outside agent (Claude via /dev-bridge) can
// drive the user to the in-app file-agent surface for handbook validation.
app.MapPost("/agent/open-file-viewer", async (HttpContext ctx, BridgeState s) =>
{
    var body = await ReadJsonObject(ctx);
    return await s.EnqueueAsync("agent_open_file_viewer", body, ctx.RequestAborted);
});

app.MapPost("/agent/open-chat-agent-panel", async (HttpContext ctx, BridgeState s) =>
{
    var body = await ReadJsonObject(ctx);
    return await s.EnqueueAsync("agent_open_chat_agent_panel", body, ctx.RequestAborted);
});

// Push a prompt into the visible chat-side agent panel input (opens panel
// if closed) and optionally auto-send via runAgent. Complements `agent_ask`
// for the "drive the visible UI so a human can watch" path; headless
// `agent_ask` returns the full log to the caller without touching the UI.
app.MapPost("/agent/fill-chat-panel-prompt", async (HttpContext ctx, BridgeState s) =>
{
    var body = await ReadJsonObject(ctx);
    return await s.EnqueueAsync("agent_fill_chat_panel_prompt", body, ctx.RequestAborted);
});

// Snapshot of which AgentHintRegistry paths are physically attached (directive
// mounted) vs. waiting for a parent dialog/panel to open. Use to verify a
// template wiring change — a directive that didn't mount leaves its path in
// `unmounted` even when the UI region is on-screen.
app.MapPost("/agent/get-hints", async (HttpContext ctx, BridgeState s) =>
{
    var body = await ReadJsonObject(ctx);
    return await s.EnqueueAsync("agent_get_hints", body, ctx.RequestAborted);
});

// Headless equivalent of clicking an app://hint/<path> link inside the agent
// console — runs the registry's openTarget(path, action). Response is the
// registry's verdict: ok=true if the element was visible+actioned, or
// ok=false with reason='unknown'|'unreachable' (+ breadcrumb on unreachable).
app.MapPost("/agent/trigger-hint", async (HttpContext ctx, BridgeState s) =>
{
    var body = await ReadJsonObject(ctx);
    return await s.EnqueueAsync("agent_trigger_hint", body, ctx.RequestAborted);
});

// Returns getBoundingClientRect() for a path's mounted element + viewport
// size. Used to mechanically verify that (a) a directive is wired to the
// expected element, and (b) after manual navigation, the element actually
// shifted into the viewport.
app.MapPost("/agent/get-hint-bbox", async (HttpContext ctx, BridgeState s) =>
{
    var body = await ReadJsonObject(ctx);
    return await s.EnqueueAsync("agent_get_hint_bbox", body, ctx.RequestAborted);
});

// Dev-only JS eval inside the running app. The Angular side gates it by
// isDevMode() (same as the rest of the bridge). Pass `expr` as a JS
// expression OR a block ending in `return`; result is JSON-serialized
// with DOM nodes turned into {tag, id, classes} stubs.
app.MapPost("/agent/eval", async (HttpContext ctx, BridgeState s) =>
{
    var body = await ReadJsonObject(ctx);
    return await s.EnqueueAsync("agent_eval", body, ctx.RequestAborted);
});

// Headless file-agent driver — send a prompt, get back the full agent log
// (tool calls, results, thoughts, final submitResponse text). Runs against
// the active book's KB + chat snapshot in a dedicated FileAgentService
// instance; the sidebar / file-viewer instances are not affected.
app.MapPost("/agent/ask", async (HttpContext ctx, BridgeState s) =>
{
    var body = await ReadJsonObject(ctx);
    return await s.EnqueueAsync("agent_ask", body, ctx.RequestAborted);
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

Console.WriteLine($"[bridge] http  listening on http://127.0.0.1:{httpPort}  (agent → /send /list /delete /reload /clients /book/*)");
Console.WriteLine($"[bridge] wss   listening on wss://127.0.0.1:{wssPort}/app  (app  → WebSocket; multi-client by hello frame's clientId)");
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
    private readonly ConcurrentDictionary<string, ClientConn> _clients = new();
    // requestId → (owner clientId, tcs). Owner tagging prevents a different
    // client from accidentally resolving another's pending response after a
    // clientId-collision replace.
    private readonly ConcurrentDictionary<string, PendingReq> _pending = new();
    private const int MaxQueue = 5;
    // Two-call mode (resolver + narrator) on a slow local model can run 3-5
    // minutes; raise the WS round-trip ceiling so the agent doesn't have to
    // poll just to wait out a single turn.
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(600);
    public int WssPort { get; } = wssPort;

    public string[] ListClients() =>
        _clients.Keys.OrderBy(s => s, StringComparer.Ordinal).ToArray();

    public async Task<IResult> EnqueueAsync(string type, JsonObject payload, CancellationToken ct)
    {
        // Resolve target client. Explicit clientId in payload wins; otherwise
        // single-connection convenience routing.
        var requestedId = payload["clientId"]?.GetValue<string>()?.Trim();
        ClientConn? conn;
        string clientId;
        if (!string.IsNullOrEmpty(requestedId))
        {
            if (!_clients.TryGetValue(requestedId, out conn))
                return Results.Json(new { error = "client_not_connected", clientId = requestedId }, statusCode: 503);
            clientId = requestedId;
        }
        else
        {
            var snapshot = _clients.ToArray();
            if (snapshot.Length == 0)
                return Results.Json(new { error = "app_not_connected" }, statusCode: 503);
            if (snapshot.Length > 1)
                return Results.Json(new
                {
                    error = "client_id_required",
                    clients = snapshot.Select(kv => kv.Key).OrderBy(s => s, StringComparer.Ordinal).ToArray()
                }, statusCode: 400);
            clientId = snapshot[0].Key;
            conn = snapshot[0].Value;
        }

        bool counted = false;
        try
        {
            var depth = Interlocked.Increment(ref conn.QueueDepth);
            if (depth > MaxQueue)
            {
                Interlocked.Decrement(ref conn.QueueDepth);
                return Results.Json(new { error = "queue_full", clientId }, statusCode: 429);
            }
            counted = true;

            await conn.Slot.WaitAsync(ct);
            try
            {
                // Re-verify liveness — client could replace/disconnect while we waited.
                if (!_clients.TryGetValue(clientId, out var live) || live != conn || conn.Ws.State != WebSocketState.Open)
                    return Results.Json(new { error = "client_not_connected", clientId }, statusCode: 503);

                var requestId = Guid.NewGuid().ToString("N");
                var msg = new JsonObject { ["type"] = type, ["requestId"] = requestId };
                foreach (var kv in payload)
                {
                    if (kv.Key is "type" or "requestId" or "clientId") continue;
                    msg[kv.Key] = kv.Value?.DeepClone();
                }

                var tcs = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pending[requestId] = new PendingReq(clientId, tcs);
                try
                {
                    var bytes = Encoding.UTF8.GetBytes(msg.ToJsonString());
                    await conn.Ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);

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
                        if (!_clients.TryGetValue(clientId, out var still) || still.Ws.State != WebSocketState.Open)
                            return Results.Json(new { error = "client_not_connected", clientId }, statusCode: 503);
                        return Results.Json(new { error = "app_timeout", clientId }, statusCode: 504);
                    }

                    var responseType = response["type"]?.GetValue<string>();
                    if (responseType == "action_error")
                    {
                        var detail = response["error"]?.GetValue<string>() ?? "unknown";
                        if (detail is "app_disconnected" or "client_replaced")
                            return Results.Json(new { error = "client_not_connected", clientId, reason = detail }, statusCode: 503);
                        return Results.Json(new { error = "app_error", detail, clientId }, statusCode: 500);
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
                conn.Slot.Release();
            }
        }
        finally
        {
            if (counted) Interlocked.Decrement(ref conn.QueueDepth);
        }
    }

    public async Task HandleAppConnectionAsync(WebSocket ws, CancellationToken ct)
    {
        // clientId is learned from the first hello frame. Until then frames
        // are still processed but registration is deferred so legacy apps
        // without a hello collapse to a 'default' bucket on first real frame.
        string clientId = "default";
        ClientConn? conn = null;

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
                catch { Console.WriteLine($"[bridge] invalid json from {clientId}: {sb}"); continue; }
                if (node is null) continue;

                var type = node["type"]?.GetValue<string>();

                if (type == "hello")
                {
                    if (conn != null)
                    {
                        // Re-hello on an active connection: ignored (clientId is sticky for the connection's lifetime).
                        Console.WriteLine($"[bridge] late hello from {clientId} ignored");
                        continue;
                    }
                    var id = node["clientId"]?.GetValue<string>()?.Trim();
                    if (!string.IsNullOrEmpty(id)) clientId = id;
                    conn = await RegisterClientAsync(clientId, ws);
                    Console.WriteLine($"[bridge] app connected: {clientId}");
                    continue;
                }

                // Legacy app — no hello, register on first real frame.
                if (conn == null)
                {
                    conn = await RegisterClientAsync(clientId, ws);
                    Console.WriteLine($"[bridge] app connected (no hello): {clientId}");
                }

                switch (type)
                {
                    case "heartbeat":
                        break;
                    default:
                        var requestId = node["requestId"]?.GetValue<string>();
                        if (requestId is not null && _pending.TryGetValue(requestId, out var pending))
                        {
                            // Only the owner's response is honored — guards against
                            // a replacement client surfacing the prior owner's frames.
                            if (pending.ClientId == clientId)
                                pending.Tcs.TrySetResult(node);
                            else
                                Console.WriteLine($"[bridge] response from {clientId} for {pending.ClientId}'s request, dropped");
                        }
                        else
                        {
                            Console.WriteLine($"[bridge] unmatched frame from {clientId}: {sb}");
                        }
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException e) { Console.WriteLine($"[bridge] ws closed ({clientId}): {e.WebSocketErrorCode}"); }
        catch (Exception e) { Console.WriteLine($"[bridge] ws error ({clientId}): {e.Message}"); }
        finally
        {
            if (conn != null)
            {
                // Only remove if this conn is still the registered one (a same-id
                // replacement already swapped it out and we shouldn't clobber that).
                _clients.TryRemove(new KeyValuePair<string, ClientConn>(conn.ClientId, conn));
                FailPendingForClient(conn.ClientId, "app_disconnected");
                Console.WriteLine($"[bridge] app disconnected: {conn.ClientId}");
            }
        }
    }

    private async Task<ClientConn> RegisterClientAsync(string clientId, WebSocket ws)
    {
        var conn = new ClientConn { Ws = ws, ClientId = clientId };
        ClientConn? old = null;
        _clients.AddOrUpdate(clientId, conn, (_, existing) => { old = existing; return conn; });
        if (old is not null)
        {
            try { await old.Ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "replaced", CancellationToken.None); }
            catch { }
            FailPendingForClient(clientId, "client_replaced");
        }
        return conn;
    }

    private void FailPendingForClient(string clientId, string reason)
    {
        var frame = new JsonObject { ["type"] = "action_error", ["error"] = reason };
        foreach (var kv in _pending.ToArray())
        {
            if (kv.Value.ClientId == clientId)
                kv.Value.Tcs.TrySetResult(frame.DeepClone());
        }
    }
}

sealed class ClientConn
{
    public required WebSocket Ws { get; init; }
    public required string ClientId { get; init; }
    public readonly SemaphoreSlim Slot = new(1, 1);
    public int QueueDepth;
}

sealed record PendingReq(string ClientId, TaskCompletionSource<JsonNode> Tcs);
