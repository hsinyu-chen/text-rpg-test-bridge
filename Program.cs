using System.Net;
using System.Security.Cryptography;
using System.Text;
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
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsync("expected websocket upgrade");
        return;
    }
    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    await s.HandleAppConnectionAsync(ws, ctx.RequestAborted);
});

Console.WriteLine($"[bridge] listening on 0.0.0.0:{port}  — /mcp (agent, MCP-over-HTTP, Bearer required) + /app (app WebSocket). TLS terminated upstream.");

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
    Console.WriteLine("Usage: BridgeServer [--port <n>]");
    Console.WriteLine();
    Console.WriteLine("Defaults:");
    Console.WriteLine("  --port  5050  (default 5050; serves /mcp + /app)");
    Console.WriteLine();
    Console.WriteLine("Env:");
    Console.WriteLine("  BRIDGE_MCP_TOKEN  REQUIRED bearer token for ALL /mcp requests (loopback included).");
    Console.WriteLine("                    Unset/blank → the server refuses to start. Send as");
    Console.WriteLine("                    `Authorization: Bearer <token>`. The /app WebSocket is unaffected.");
}
