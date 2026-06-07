using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
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
    // Auth is enforced per TOOL CALL (here), NOT at the HTTP layer. An HTTP 401/403 on
    // the MCP handshake makes clients (e.g. VS Code) launch the OAuth/Dynamic-Client-
    // Registration flow or fail their initial header-less probe, so the protocol surface
    // (initialize / tools.list) must stay open — only an actual tool invocation requires
    // the Bearer token. IHttpContextAccessor is a singleton backed by AsyncLocal, so a
    // static reference safely yields the *current* request's context. Set once at startup.
    private static IHttpContextAccessor? _http;
    private static byte[]? _tokenBytes;

    public static void Configure(IHttpContextAccessor http, string token)
    {
        _http = http;
        _tokenBytes = Encoding.UTF8.GetBytes(token);
    }

    private static bool IsAuthorized()
    {
        var ctx = _http?.HttpContext;
        if (ctx is null || _tokenBytes is null) return false;
        var header = ctx.Request.Headers.Authorization.ToString();
        const string scheme = "Bearer ";
        if (!header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return false;
        var presented = header[scheme.Length..].Trim();
        return presented.Length > 0
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), _tokenBytes);
    }

    // Shared call path: build the WS frame payload, hand it to the relay, and
    // turn the domain result into MCP content (success) or an MCP error result
    // (failure) that preserves the legacy error `code` + extra fields verbatim.
    private static async Task<CallToolResult> Relay(BridgeState state, string type, JsonObject payload, string? clientId, CancellationToken ct)
    {
        if (!IsAuthorized()) return RelayResult.Error("unauthorized").ToCallToolResult();
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

    private static JsonObject Obj() => [];

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
        if (!IsAuthorized()) return RelayResult.Error("unauthorized").ToCallToolResult();
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
