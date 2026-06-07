using System.Net;
using ModelContextProtocol;
using ModelContextProtocol.Server;

int port = 5050;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--port":
            port = int.Parse(args[++i]);
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
    // Plain HTTP+WS on one port; TLS terminated upstream (WebStation / reverse proxy).
    options.Listen(IPAddress.Any, port);
});

var state = new BridgeState();
builder.Services.AddSingleton(state);
builder.Services.AddHttpContextAccessor();

// Native MCP-over-HTTP server. Tools live on RelayTools ([McpServerToolType]);
// each maps one former POST route onto a WS frame type via BridgeState.EnqueueAsync.
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithTools<RelayTools>();

var app = builder.Build();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });

// BRIDGE_MCP_TOKEN is mandatory — no tool call can be authorized without it, so
// refuse to start if it's unset/blank. Auth is enforced per TOOL CALL inside
// RelayTools, NOT at the HTTP layer: a 401/403 on the MCP handshake makes clients
// (e.g. VS Code) attempt OAuth or fail their initial probe, so the protocol
// surface (initialize / tools.list) must stay open. See RelayTools for the gate.
var mcpToken = Environment.GetEnvironmentVariable("BRIDGE_MCP_TOKEN");
if (string.IsNullOrWhiteSpace(mcpToken))
{
    Console.Error.WriteLine("[bridge] FATAL: BRIDGE_MCP_TOKEN is required — tool calls cannot be authorized without it");
    return 1;
}
RelayTools.Configure(app.Services.GetRequiredService<IHttpContextAccessor>(), mcpToken);

app.MapMcp("/mcp");

app.Map("/app", async (HttpContext ctx, BridgeState s) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsync("expected websocket upgrade");
        return;
    }
    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    await s.HandleAppConnectionAsync(ws, ctx.RequestAborted);
});

Console.WriteLine($"[bridge] listening on 0.0.0.0:{port}  — /mcp (agent, MCP-over-HTTP; Bearer required per tool call) + /app (app WebSocket). TLS terminated upstream.");

await app.RunAsync();
return 0;

static void PrintUsage()
{
    Console.WriteLine("BridgeServer — TextRPG dev relay");
    Console.WriteLine();
    Console.WriteLine("Usage: BridgeServer [--port <n>]");
    Console.WriteLine();
    Console.WriteLine("Defaults:");
    Console.WriteLine("  --port  5050  (default 5050; serves /mcp + /app)");
    Console.WriteLine();
    Console.WriteLine("Env:");
    Console.WriteLine("  BRIDGE_MCP_TOKEN  REQUIRED — authorizes MCP tool calls (the handshake stays open so");
    Console.WriteLine("                    clients connect without OAuth). Unset/blank → the server refuses to");
    Console.WriteLine("                    start. Clients send `Authorization: Bearer <token>`; /app is unaffected.");
}
