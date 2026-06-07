using System.ComponentModel;
using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

int httpPort = 5051;
int wssPort = 5050;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
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

var builder = WebApplication.CreateBuilder();
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
builder.WebHost.ConfigureKestrel(options =>
{
    options.Listen(IPAddress.Any, httpPort);
    // Plain ws — nginx terminates TLS upstream; port keeps the 'wss' name for the app contract.
    options.Listen(IPAddress.Any, wssPort);
});

var state = new BridgeState(wssPort);
builder.Services.AddSingleton(state);

// Native MCP-over-HTTP server. Tools live on RelayTools ([McpServerToolType]);
// each maps one former POST route onto a WS frame type via BridgeState.EnqueueAsync.
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithTools<RelayTools>();

var app = builder.Build();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });

// Token is mandatory — the /mcp endpoint refuses to run unauthenticated. Read
// once at startup and pre-encode to UTF-8 so the per-request gate only compares
// bytes. An unset/blank token is fatal: bail before app.Run() so /mcp is never
// served without auth.
var mcpToken = Environment.GetEnvironmentVariable("BRIDGE_MCP_TOKEN");
if (string.IsNullOrWhiteSpace(mcpToken))
{
    Console.Error.WriteLine("[bridge] FATAL: BRIDGE_MCP_TOKEN is required — the /mcp endpoint refuses to run unauthenticated");
    return 1;
}
var mcpTokenBytes = Encoding.UTF8.GetBytes(mcpToken);

// Token gate scoped to the MCP surface ONLY — must run before MapMcp and the
// /app WS map, and must NOT touch /app or anything else. EVERY /mcp request
// (loopback included) must carry `Authorization: Bearer <BRIDGE_MCP_TOKEN>`;
// there is no loopback bypass. The decision reads only the header — never
// X-Forwarded-For or any forwarded header.
app.UseWhen(
    ctx => ctx.Request.Path.StartsWithSegments("/mcp"),
    branch => branch.Use(async (ctx, next) =>
    {
        if (TryGetBearer(ctx, out var presented)
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), mcpTokenBytes))
        {
            await next();
            return;
        }
        ctx.Response.Headers.WWWAuthenticate = "Bearer";
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
    }));

app.MapMcp("/mcp");

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

Console.WriteLine($"[bridge] mcp   listening on http://0.0.0.0:{httpPort}/mcp  (agent → MCP-over-HTTP; tools/list, tools/call; Authorization: Bearer BRIDGE_MCP_TOKEN required)");
Console.WriteLine($"[bridge] ws    listening on ws://0.0.0.0:{wssPort}/app  (app  → WebSocket; multi-client by hello frame's clientId; nginx adds TLS)");

await app.RunAsync();
return 0;

static bool TryGetBearer(HttpContext ctx, out string token)
{
    token = "";
    var header = ctx.Request.Headers.Authorization.ToString();
    const string scheme = "Bearer ";
    if (header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
    {
        token = header[scheme.Length..].Trim();
        return token.Length > 0;
    }
    return false;
}

static void PrintUsage()
{
    Console.WriteLine("BridgeServer — TextRPG dev relay");
    Console.WriteLine();
    Console.WriteLine("Usage: BridgeServer [--http-port <n>] [--wss-port <n>]");
    Console.WriteLine();
    Console.WriteLine("Defaults:");
    Console.WriteLine("  --http-port  5051  (MCP-over-HTTP at /mcp, agent-facing)");
    Console.WriteLine("  --wss-port   5050  (plain ws, app-facing; nginx adds TLS)");
    Console.WriteLine();
    Console.WriteLine("Env:");
    Console.WriteLine("  BRIDGE_MCP_TOKEN  REQUIRED bearer token for ALL /mcp requests (loopback included).");
    Console.WriteLine("                    Unset/blank → the server refuses to start. Send as");
    Console.WriteLine("                    `Authorization: Bearer <token>`. The /app WebSocket is unaffected.");
}

/// <summary>
/// MCP tool surface. One tool per former agent-facing POST route, plus `clients`
/// for the old GET /clients. Every tool funnels through <see cref="Relay"/> →
/// <see cref="BridgeState.EnqueueAsync"/>, so the WS frame contract is unchanged.
/// Methods are static; the singleton <see cref="BridgeState"/> arrives by DI on
/// the method parameter (it is a registered service — the SDK resolves it from
/// the request scope rather than binding it from the tool's JSON arguments).
/// </summary>
[McpServerToolType]
sealed class RelayTools
{
    // Shared call path: build the WS frame payload, hand it to the relay, and
    // turn the domain result into MCP content (success) or an MCP error result
    // (failure) that preserves the legacy error `code` + extra fields verbatim.
    private static async Task<CallToolResult> Relay(BridgeState state, string type, JsonObject payload, string? clientId, CancellationToken ct)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(clientId)) payload["clientId"] = clientId;
            var result = await state.EnqueueAsync(type, payload, ct);
            return result.ToCallToolResult();
        }
        catch (OperationCanceledException)
        {
            // Cancellation is not an internal fault — let it propagate so the SDK
            // tears the request down rather than reporting it as a tool error.
            throw;
        }
        catch (Exception ex)
        {
            // Surface the real message in the isError payload; otherwise the SDK
            // masks an unexpected throw to a generic string and it's undiagnosable.
            return RelayResult.Error("bridge_internal_error", new JsonObject { ["detail"] = ex.Message }).ToCallToolResult();
        }
    }

    private static JsonObject Obj() => new();

    [McpServerTool(Name = "send"), Description("Drive one real GameEngine turn: send userInput (optionally with an intent) and get back the produced user/model message pair.")]
    public static Task<CallToolResult> Send(
        BridgeState state,
        [Description("The action/line to send. Format ([心境]動作)台詞; empty is valid for continue/fast_forward/system.")] string userInput,
        [Description("Game intent: action (default) / continue / fast_forward / system. Any other value falls back to the engine default.")] string? intent = null,
        [Description("Target app clientId; omit to auto-route when exactly one client is connected.")] string? clientId = null,
        CancellationToken ct = default)
    {
        var p = Obj();
        p["userInput"] = userInput;
        if (intent is not null) p["intent"] = intent;
        return Relay(state, "send_action", p, clientId, ct);
    }

    [McpServerTool(Name = "list"), Description("List the last N chat messages (id/role/preview, or full turn fields with full=true).")]
    public static Task<CallToolResult> List(
        BridgeState state,
        [Description("How many trailing messages to return (default 50, capped at 200 app-side).")] int? limit = null,
        [Description("When true, return full turn fields (analysis/summary/content/*_log) instead of an 80-char preview.")] bool? full = null,
        [Description("Target app clientId; omit to auto-route when exactly one client is connected.")] string? clientId = null,
        CancellationToken ct = default)
    {
        var p = Obj();
        if (limit is not null) p["limit"] = limit;
        if (full is not null) p["full"] = full;
        return Relay(state, "list", p, clientId, ct);
    }

    [McpServerTool(Name = "delete"), Description("Delete a message by id, removing its paired sibling too unless alsoDeletePair=false.")]
    public static Task<CallToolResult> Delete(
        BridgeState state,
        [Description("The message id to delete.")] string messageId,
        [Description("Also delete the adjacent user/model sibling (default true).")] bool? alsoDeletePair = null,
        [Description("Target app clientId; omit to auto-route when exactly one client is connected.")] string? clientId = null,
        CancellationToken ct = default)
    {
        var p = Obj();
        p["messageId"] = messageId;
        if (alsoDeletePair is not null) p["alsoDeletePair"] = alsoDeletePair;
        return Relay(state, "delete", p, clientId, ct);
    }

    [McpServerTool(Name = "reload"), Description("Trigger a hard reload of the running app (window.location.reload), e.g. after editing prompt assets.")]
    public static Task<CallToolResult> Reload(
        BridgeState state,
        [Description("Non-standard Location.reload(force) flag; mostly ignored by browsers, plumbed for explicit intent.")] bool? force = null,
        [Description("Target app clientId; omit to auto-route when exactly one client is connected.")] string? clientId = null,
        CancellationToken ct = default)
    {
        var p = Obj();
        if (force is not null) p["force"] = force;
        return Relay(state, "reload", p, clientId, ct);
    }

    [McpServerTool(Name = "clients"), Description("List the clientIds of currently-connected app instances. Use to disambiguate routing when multiple are live.")]
    public static CallToolResult Clients(BridgeState state)
    {
        var arr = new JsonArray();
        foreach (var id in state.ListClients()) arr.Add(id);
        var payload = new JsonObject { ["clients"] = arr };
        return RelayResult.Success(payload).ToCallToolResult();
    }

    [McpServerTool(Name = "profile_list"), Description("List built-in + user prompt profiles with each profile's system_main compatibility tag.")]
    public static Task<CallToolResult> ProfileList(
        BridgeState state,
        [Description("Target app clientId; omit to auto-route when exactly one client is connected.")] string? clientId = null,
        CancellationToken ct = default)
        => Relay(state, "profile_list", Obj(), clientId, ct);

    [McpServerTool(Name = "profile_active"), Description("Get the active prompt profile (id + displayName + isBuiltIn + baseProfileId + compat).")]
    public static Task<CallToolResult> ProfileActive(
        BridgeState state,
        [Description("Target app clientId; omit to auto-route when exactly one client is connected.")] string? clientId = null,
        CancellationToken ct = default)
        => Relay(state, "profile_get_active", Obj(), clientId, ct);

    [McpServerTool(Name = "profile_switch"), Description("Switch the active prompt profile by id. Refuses mid-turn (busy).")]
    public static Task<CallToolResult> ProfileSwitch(
        BridgeState state,
        [Description("The prompt profile id to make active.")] string id,
        [Description("Target app clientId; omit to auto-route when exactly one client is connected.")] string? clientId = null,
        CancellationToken ct = default)
    {
        var p = Obj();
        p["id"] = id;
        return Relay(state, "profile_switch", p, clientId, ct);
    }

    [McpServerTool(Name = "profile_pull_from_disk"), Description("Pull the active user-defined profile's prompt files from the bound FSA folder into IDB, then forceReload. Built-in/busy rejected.")]
    public static Task<CallToolResult> ProfilePullFromDisk(
        BridgeState state,
        [Description("Target app clientId; omit to auto-route when exactly one client is connected.")] string? clientId = null,
        CancellationToken ct = default)
        => Relay(state, "profile_pull_from_disk", Obj(), clientId, ct);

    [McpServerTool(Name = "profile_push_to_disk"), Description("Write the active user-defined profile's IDB prompt rows out to the bound FSA folder. Built-in/busy rejected.")]
    public static Task<CallToolResult> ProfilePushToDisk(
        BridgeState state,
        [Description("Target app clientId; omit to auto-route when exactly one client is connected.")] string? clientId = null,
        CancellationToken ct = default)
        => Relay(state, "profile_push_to_disk", Obj(), clientId, ct);

    [McpServerTool(Name = "profile_get_prompt"), Description("Read one resolved prompt for a profile (defaults to active). Returns content + hasOverride.")]
    public static Task<CallToolResult> ProfileGetPrompt(
        BridgeState state,
        [Description("The prompt type to read; call profile_get_all_prompts to see the valid types for a profile (its response keys are the authoritative, non-rotting list).")] string promptType,
        [Description("Profile id to read; omit for the active profile.")] string? profileId = null,
        [Description("Target app clientId; omit to auto-route when exactly one client is connected.")] string? clientId = null,
        CancellationToken ct = default)
    {
        // promptType/profileId ride as ordinary payload keys (NOT the reserved
        // `type`) so EnqueueAsync forwards them to the app intact.
        var p = Obj();
        p["promptType"] = promptType;
        if (profileId is not null) p["profileId"] = profileId;
        return Relay(state, "profile_get_prompt", p, clientId, ct);
    }

    [McpServerTool(Name = "profile_get_all_prompts"), Description("Read all resolved prompts for a profile (defaults to active), each with content + hasOverride.")]
    public static Task<CallToolResult> ProfileGetAllPrompts(
        BridgeState state,
        [Description("Profile id to read; omit for the active profile.")] string? profileId = null,
        [Description("Target app clientId; omit to auto-route when exactly one client is connected.")] string? clientId = null,
        CancellationToken ct = default)
    {
        var p = Obj();
        if (profileId is not null) p["profileId"] = profileId;
        return Relay(state, "profile_get_all_prompts", p, clientId, ct);
    }

    [McpServerTool(Name = "profile_set_prompt"), Description("Write one prompt row to the ACTIVE (user-defined) profile's IDB and forceReload. Built-in/busy rejected.")]
    public static Task<CallToolResult> ProfileSetPrompt(
        BridgeState state,
        [Description("The prompt type to write; call profile_get_all_prompts to see the valid types for a profile (its response keys are the authoritative, non-rotting list).")] string promptType,
        [Description("The new prompt text to store.")] string content,
        [Description("Target app clientId; omit to auto-route when exactly one client is connected.")] string? clientId = null,
        CancellationToken ct = default)
    {
        var p = Obj();
        p["promptType"] = promptType;
        p["content"] = content;
        return Relay(state, "profile_set_prompt", p, clientId, ct);
    }

    [McpServerTool(Name = "llm_list"), Description("List every chat-side LLM profile with its isLocal flag (local = free, used by the paid guard).")]
    public static Task<CallToolResult> LlmList(
        BridgeState state,
        [Description("Target app clientId; omit to auto-route when exactly one client is connected.")] string? clientId = null,
        CancellationToken ct = default)
        => Relay(state, "llm_list", Obj(), clientId, ct);

    [McpServerTool(Name = "llm_active"), Description("Get the active chat-side LLM profile (id + name + provider + modelId + isLocal).")]
    public static Task<CallToolResult> LlmActive(
        BridgeState state,
        [Description("Target app clientId; omit to auto-route when exactly one client is connected.")] string? clientId = null,
        CancellationToken ct = default)
        => Relay(state, "llm_get_active", Obj(), clientId, ct);

    [McpServerTool(Name = "llm_switch"), Description("Switch the active chat-side LLM profile by id. Non-local targets require confirmPaid=true (paid-model guard).")]
    public static Task<CallToolResult> LlmSwitch(
        BridgeState state,
        [Description("The LLM profile id to make active.")] string id,
        [Description("Must be true to switch to a non-local (paid) profile; guards against accidental paid turns.")] bool? confirmPaid = null,
        [Description("Target app clientId; omit to auto-route when exactly one client is connected.")] string? clientId = null,
        CancellationToken ct = default)
    {
        var p = Obj();
        p["id"] = id;
        if (confirmPaid is not null) p["confirmPaid"] = confirmPaid;
        return Relay(state, "llm_switch", p, clientId, ct);
    }

    [McpServerTool(Name = "file_agent_active"), Description("Get the active file-agent LLM profile (independent of the chat-side llm_active).")]
    public static Task<CallToolResult> FileAgentActive(
        BridgeState state,
        [Description("Target app clientId; omit to auto-route when exactly one client is connected.")] string? clientId = null,
        CancellationToken ct = default)
        => Relay(state, "file_agent_get_profile", Obj(), clientId, ct);

    [McpServerTool(Name = "file_agent_switch"), Description("Switch the file-agent LLM profile by id. Same paid guard as llm_switch (confirmPaid for non-local).")]
    public static Task<CallToolResult> FileAgentSwitch(
        BridgeState state,
        [Description("The LLM profile id to assign to the file-agent.")] string id,
        [Description("Must be true to switch to a non-local (paid) profile; guards against accidental paid turns.")] bool? confirmPaid = null,
        [Description("Target app clientId; omit to auto-route when exactly one client is connected.")] string? clientId = null,
        CancellationToken ct = default)
    {
        var p = Obj();
        p["id"] = id;
        if (confirmPaid is not null) p["confirmPaid"] = confirmPaid;
        return Relay(state, "file_agent_set_profile", p, clientId, ct);
    }

    [McpServerTool(Name = "book_repair_kb"), Description("Add scenario files missing from the active Book's KB (by scenario id). Existing entries are preserved; busy rejected.")]
    public static Task<CallToolResult> BookRepairKb(
        BridgeState state,
        [Description("The scenario id whose file manifest should backfill the active Book's KB.")] string scenarioId,
        [Description("Target app clientId; omit to auto-route when exactly one client is connected.")] string? clientId = null,
        CancellationToken ct = default)
    {
        var p = Obj();
        p["scenarioId"] = scenarioId;
        return Relay(state, "book_repair_kb", p, clientId, ct);
    }

    [McpServerTool(Name = "config_get"), Description("Read the full AppConfigShape snapshot plus the active modelId (read-only echo).")]
    public static Task<CallToolResult> ConfigGet(
        BridgeState state,
        [Description("Target app clientId; omit to auto-route when exactly one client is connected.")] string? clientId = null,
        CancellationToken ct = default)
        => Relay(state, "config_get", Obj(), clientId, ct);

    [McpServerTool(Name = "config_set"), Description("Partial-patch engine/UI config. Only provided fields are sent; unknown/mistyped keys come back under rejected. Busy rejected.")]
    public static Task<CallToolResult> ConfigSet(
        BridgeState state,
        [Description("Engine dispatch mode: single or two-call.")] string? engineMode = null,
        [Description("Narrative output language code/string.")] string? outputLanguage = null,
        [Description("UI font size in px (> 0).")] double? fontSize = null,
        [Description("UI font family.")] string? fontFamily = null,
        [Description("Screensaver style: invaders or code.")] string? screensaverType = null,
        [Description("Currency code/symbol string.")] string? currency = null,
        [Description("Enable currency conversion.")] bool? enableConversion = null,
        [Description("Pause/idle behavior when the window loses focus.")] bool? idleOnBlur = null,
        [Description("Enable the adult-content declaration gate.")] bool? enableAdultDeclaration = null,
        [Description("Currency exchange rate (> 0).")] double? exchangeRate = null,
        [Description("UI interface language (validated app-side).")] string? interfaceLanguage = null,
        [Description("Smart-context window size in turns (positive integer).")] int? smartContextTurns = null,
        [Description("Target app clientId; omit to auto-route when exactly one client is connected.")] string? clientId = null,
        CancellationToken ct = default)
    {
        var p = Obj();
        if (engineMode is not null) p["engineMode"] = engineMode;
        if (outputLanguage is not null) p["outputLanguage"] = outputLanguage;
        if (fontSize is not null) p["fontSize"] = fontSize;
        if (fontFamily is not null) p["fontFamily"] = fontFamily;
        if (screensaverType is not null) p["screensaverType"] = screensaverType;
        if (currency is not null) p["currency"] = currency;
        if (enableConversion is not null) p["enableConversion"] = enableConversion;
        if (idleOnBlur is not null) p["idleOnBlur"] = idleOnBlur;
        if (enableAdultDeclaration is not null) p["enableAdultDeclaration"] = enableAdultDeclaration;
        if (exchangeRate is not null) p["exchangeRate"] = exchangeRate;
        if (interfaceLanguage is not null) p["interfaceLanguage"] = interfaceLanguage;
        if (smartContextTurns is not null) p["smartContextTurns"] = smartContextTurns;
        return Relay(state, "config_set", p, clientId, ct);
    }

    [McpServerTool(Name = "kb_list"), Description("List the knowledge-base files loaded in the active book (filename + size + tokenCount).")]
    public static Task<CallToolResult> KbList(
        BridgeState state,
        [Description("Target app clientId; omit to auto-route when exactly one client is connected.")] string? clientId = null,
        CancellationToken ct = default)
        => Relay(state, "kb_list", Obj(), clientId, ct);

    [McpServerTool(Name = "kb_read"), Description("Read the full content of one loaded KB file by filename.")]
    public static Task<CallToolResult> KbRead(
        BridgeState state,
        [Description("The KB filename to read (must match a loaded entry; filenames are language-bucketed).")] string filename,
        [Description("Target app clientId; omit to auto-route when exactly one client is connected.")] string? clientId = null,
        CancellationToken ct = default)
    {
        var p = Obj();
        p["filename"] = filename;
        return Relay(state, "kb_read", p, clientId, ct);
    }

    [McpServerTool(Name = "book_list"), Description("List every persisted Book (id/name/messageCount/isActive). Messages are fetched separately via list.")]
    public static Task<CallToolResult> BookList(
        BridgeState state,
        [Description("Target app clientId; omit to auto-route when exactly one client is connected.")] string? clientId = null,
        CancellationToken ct = default)
        => Relay(state, "book_list", Obj(), clientId, ct);

    [McpServerTool(Name = "book_active"), Description("Get the currently active Book (id + name + messageCount).")]
    public static Task<CallToolResult> BookActive(
        BridgeState state,
        [Description("Target app clientId; omit to auto-route when exactly one client is connected.")] string? clientId = null,
        CancellationToken ct = default)
        => Relay(state, "book_get_active", Obj(), clientId, ct);

    [McpServerTool(Name = "book_fork"), Description("Fork the active Book truncated (inclusive) at a message into a new sibling Book and switch to it. Busy rejected.")]
    public static Task<CallToolResult> BookFork(
        BridgeState state,
        [Description("The message id to truncate at (kept in the fork).")] string messageId,
        [Description("Name for the new Book; defaults to '<source> (fork)'.")] string? newName = null,
        [Description("Target app clientId; omit to auto-route when exactly one client is connected.")] string? clientId = null,
        CancellationToken ct = default)
    {
        var p = Obj();
        p["messageId"] = messageId;
        if (newName is not null) p["newName"] = newName;
        return Relay(state, "book_fork", p, clientId, ct);
    }

    [McpServerTool(Name = "book_switch"), Description("Load a different Book as the active session by id. Busy rejected.")]
    public static Task<CallToolResult> BookSwitch(
        BridgeState state,
        [Description("The Book id to load.")] string id,
        [Description("Target app clientId; omit to auto-route when exactly one client is connected.")] string? clientId = null,
        CancellationToken ct = default)
    {
        var p = Obj();
        p["id"] = id;
        return Relay(state, "book_switch", p, clientId, ct);
    }

    [McpServerTool(Name = "agent_open_file_viewer"), Description("Open the in-app File Viewer dialog with the agent panel pre-opened, landing on initialFile (first KB file if omitted).")]
    public static Task<CallToolResult> AgentOpenFileViewer(
        BridgeState state,
        [Description("Filename to land on; omit to open the first loaded KB file.")] string? initialFile = null,
        [Description("Target app clientId; omit to auto-route when exactly one client is connected.")] string? clientId = null,
        CancellationToken ct = default)
    {
        var p = Obj();
        if (initialFile is not null) p["initialFile"] = initialFile;
        return Relay(state, "agent_open_file_viewer", p, clientId, ct);
    }

    [McpServerTool(Name = "agent_open_chat_agent_panel"), Description("Open the chat-side agent panel (read-only sidebar surface).")]
    public static Task<CallToolResult> AgentOpenChatAgentPanel(
        BridgeState state,
        [Description("Target app clientId; omit to auto-route when exactly one client is connected.")] string? clientId = null,
        CancellationToken ct = default)
        => Relay(state, "agent_open_chat_agent_panel", Obj(), clientId, ct);

    [McpServerTool(Name = "agent_fill_chat_panel_prompt"), Description("Open the chat-side agent panel, drop a prompt into its input, and optionally auto-send via runAgent.")]
    public static Task<CallToolResult> AgentFillChatPanelPrompt(
        BridgeState state,
        [Description("The prompt text to place in the panel input.")] string prompt,
        [Description("When true, also fire runAgent so the agent streams live in the visible panel.")] bool? autoSend = null,
        [Description("Target app clientId; omit to auto-route when exactly one client is connected.")] string? clientId = null,
        CancellationToken ct = default)
    {
        var p = Obj();
        p["prompt"] = prompt;
        if (autoSend is not null) p["autoSend"] = autoSend;
        return Relay(state, "agent_fill_chat_panel_prompt", p, clientId, ct);
    }

    [McpServerTool(Name = "agent_get_hints"), Description("Return the AgentHintRegistry mount report (total/mounted/unmounted/activatable) to verify template wiring.")]
    public static Task<CallToolResult> AgentGetHints(
        BridgeState state,
        [Description("Target app clientId; omit to auto-route when exactly one client is connected.")] string? clientId = null,
        CancellationToken ct = default)
        => Relay(state, "agent_get_hints", Obj(), clientId, ct);

    [McpServerTool(Name = "agent_trigger_hint"), Description("Headlessly run an app://hint/<path> link (highlight/focus/activate). Returns ok + reason/breadcrumb.")]
    public static Task<CallToolResult> AgentTriggerHint(
        BridgeState state,
        [Description("The agent-hint manifest path to trigger.")] string path,
        [Description("Action to run: highlight (default) / focus / activate.")] string? action = null,
        [Description("Target app clientId; omit to auto-route when exactly one client is connected.")] string? clientId = null,
        CancellationToken ct = default)
    {
        var p = Obj();
        p["path"] = path;
        if (action is not null) p["action"] = action;
        return Relay(state, "agent_trigger_hint", p, clientId, ct);
    }

    [McpServerTool(Name = "agent_get_hint_bbox"), Description("Return getBoundingClientRect() for a hint path's mounted element plus viewport size.")]
    public static Task<CallToolResult> AgentGetHintBbox(
        BridgeState state,
        [Description("The agent-hint manifest path to measure.")] string path,
        [Description("Target app clientId; omit to auto-route when exactly one client is connected.")] string? clientId = null,
        CancellationToken ct = default)
    {
        var p = Obj();
        p["path"] = path;
        return Relay(state, "agent_get_hint_bbox", p, clientId, ct);
    }

    [McpServerTool(Name = "agent_eval"), Description("Dev-only async JS eval inside the running app (gated by the app's eval toggle). expr is an expression or a block ending in return.")]
    public static Task<CallToolResult> AgentEval(
        BridgeState state,
        [Description("JS expression or statement block; bare expressions auto-wrap as return (expr).")] string expr,
        [Description("Target app clientId; omit to auto-route when exactly one client is connected.")] string? clientId = null,
        CancellationToken ct = default)
    {
        var p = Obj();
        p["expr"] = expr;
        return Relay(state, "agent_eval", p, clientId, ct);
    }

    [McpServerTool(Name = "agent_ask"), Description("Run a headless file-agent turn against the active book's KB + chat, returning the full log (tool calls/results/thoughts/final).")]
    public static Task<CallToolResult> AgentAsk(
        BridgeState state,
        [Description("The question/prompt for the file-agent.")] string prompt,
        [Description("sidebar = readOnly (default); fileViewer = writes allowed against a snapshot Map (engine KB not mutated).")] string? mode = null,
        [Description("Default false (preserve). True wipes prior turn history for a fresh conversation.")] bool? clearHistory = null,
        [Description("Target app clientId; omit to auto-route when exactly one client is connected.")] string? clientId = null,
        CancellationToken ct = default)
    {
        var p = Obj();
        p["prompt"] = prompt;
        if (mode is not null) p["mode"] = mode;
        if (clearHistory is not null) p["clearHistory"] = clearHistory;
        return Relay(state, "agent_ask", p, clientId, ct);
    }
}

/// <summary>
/// Outcome of a relay round-trip. Carries EITHER a success <see cref="JsonNode"/>
/// (the app's response payload, with type/requestId already stripped) OR an
/// error <c>code</c> plus the extra fields legacy callers depend on (clientId /
/// clients / detail / reason / target / active / rejected …). The MCP tool layer
/// maps success → tool content and error → an isError tool result whose JSON
/// preserves the same code strings + fields the old HTTP body carried.
/// </summary>
sealed record RelayResult(bool IsError, JsonNode? Value, string? Code, JsonObject? ErrorFields)
{
    public static RelayResult Success(JsonNode? value) => new(false, value, null, null);

    public static RelayResult Error(string code, JsonObject? fields = null) => new(true, null, code, fields);

    public CallToolResult ToCallToolResult()
    {
        if (!IsError)
        {
            var json = Value?.ToJsonString() ?? "null";
            return new CallToolResult
            {
                IsError = false,
                Content = [new TextContentBlock { Text = json }],
            };
        }

        var body = ErrorFields is null ? new JsonObject() : (JsonObject)ErrorFields.DeepClone();
        body["error"] = Code;
        return new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = body.ToJsonString() }],
        };
    }
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

    public async Task<RelayResult> EnqueueAsync(string type, JsonObject payload, CancellationToken ct)
    {
        // Resolve target client. Explicit clientId in payload wins; otherwise
        // single-connection convenience routing.
        var requestedId = payload["clientId"]?.GetValue<string>()?.Trim();
        ClientConn? conn;
        string clientId;
        if (!string.IsNullOrEmpty(requestedId))
        {
            if (!_clients.TryGetValue(requestedId, out conn))
                return RelayResult.Error("client_not_connected", new JsonObject { ["clientId"] = requestedId });
            clientId = requestedId;
        }
        else
        {
            var snapshot = _clients.ToArray();
            if (snapshot.Length == 0)
                return RelayResult.Error("app_not_connected");
            if (snapshot.Length > 1)
            {
                var clients = new JsonArray();
                foreach (var key in snapshot.Select(kv => kv.Key).OrderBy(s => s, StringComparer.Ordinal))
                    clients.Add(key);
                return RelayResult.Error("client_id_required", new JsonObject { ["clients"] = clients });
            }
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
                return RelayResult.Error("queue_full", new JsonObject { ["clientId"] = clientId });
            }
            counted = true;

            await conn.Slot.WaitAsync(ct);
            try
            {
                // Re-verify liveness — client could replace/disconnect while we waited.
                if (!_clients.TryGetValue(clientId, out var live) || live != conn || conn.Ws.State != WebSocketState.Open)
                    return RelayResult.Error("client_not_connected", new JsonObject { ["clientId"] = clientId });

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
                            return RelayResult.Error("client_not_connected", new JsonObject { ["clientId"] = clientId });
                        return RelayResult.Error("app_timeout", new JsonObject { ["clientId"] = clientId });
                    }

                    var responseType = response["type"]?.GetValue<string>();
                    if (responseType == "action_error")
                    {
                        var detail = response["error"]?.GetValue<string>() ?? "unknown";
                        if (detail is "app_disconnected" or "client_replaced")
                            return RelayResult.Error("client_not_connected", new JsonObject { ["clientId"] = clientId, ["reason"] = detail });
                        return RelayResult.Error("app_error", new JsonObject { ["detail"] = detail, ["clientId"] = clientId });
                    }

                    if (response is JsonObject obj)
                    {
                        obj.Remove("type");
                        obj.Remove("requestId");
                        return RelayResult.Success(obj);
                    }
                    return RelayResult.Success(response);
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
