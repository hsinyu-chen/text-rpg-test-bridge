# BridgeServer — TextRPG dev relay

A small .NET 10 web app that lets an external agent drive a running
[TextRPG](https://github.com/hsinyu-chen/text-rpg) build, instead of
copy/pasting LLM output back and forth.

Both surfaces share a **single port** `5050`, path-routed:

- Agent ↔ server: native **MCP-over-HTTP** at `/mcp`
  (Streamable HTTP; one MCP tool per former HTTP route)
- App ↔ server: plain **WebSocket** at `/app`

The port is plain — no TLS. Terminate TLS upstream (e.g. a reverse proxy) if you need it.

## Run / deploy

It's an ordinary ASP.NET Core app — deploy it however you like (`dotnet run`,
`dotnet publish`, or the bundled [`Dockerfile`](Dockerfile)). Optional flag:
`--port` (default `5050`; serves both `/mcp` and `/app`).

### Reverse proxy: raise the read timeout

Slow tools hold the `/mcp` request open while they run — `agent_ask` drives a
full in-app agent turn and routinely takes 2–3 minutes, sending no bytes until
it finishes. A proxy with a short idle/read timeout cuts the connection mid-call
and the client sees `transport dropped mid-call`, even though the app completed
and sent its response (the relay just has no connection left to write to). Give
the `/mcp` location a read timeout comfortably above the slowest tool, e.g. 1800s.

On Synology specifically: WebStation's reverse proxy has a ~60s read timeout that
isn't configurable — front the container with the DSM **Login Portal** reverse
proxy instead (Control Panel → Login Portal → Advanced → Reverse Proxy), which
exposes a Proxy Timeout, and add the WebSocket `Upgrade` / `Connection` custom
headers so `/app` can connect.

## MCP access + token auth

The `/mcp` endpoint speaks the standard Model Context Protocol over Streamable
HTTP — point any MCP client at `http://<host>:5050/mcp`. Each former agent-facing
HTTP route is now an MCP tool (`send`, `list`, `delete`, `reload`, `clients`,
`config_get`/`config_set`, the `profile_*` / `llm_*` / `book_*` / `agent_*`
families, …). Every tool takes an optional `clientId` for multi-client routing.

Tool calls are gated by a **required** bearer token in the `BRIDGE_MCP_TOKEN`
environment variable. Auth is enforced **per tool call, not at the HTTP layer** —
the MCP handshake (`initialize` / `tools/list`) stays open and never returns
`401`/`403`. That's deliberate: an auth challenge on the handshake makes some MCP
clients (e.g. VS Code) launch an OAuth / Dynamic-Client-Registration flow instead
of sending the static token, so the protocol surface must answer unauthenticated.

- **The token is mandatory.** If `BRIDGE_MCP_TOKEN` is unset or blank the server
  refuses to start — it writes a fatal message to stderr and exits non-zero.
- **Every tool call must carry** `Authorization: Bearer <BRIDGE_MCP_TOKEN>`,
  compared in fixed time. **There is no loopback bypass** — calls from
  `127.0.0.1` / `::1` need the token too. The check reads only the header, never
  `X-Forwarded-For` or any forwarded header.
- A missing or wrong token does **not** produce an HTTP `401`; the tool returns an
  `unauthorized` error result (an `isError` tool result), leaving the transport
  intact.

The app-facing WebSocket at `/app` (same port) is unaffected — it carries no token.

The MCP tool surface and the WebSocket frame contract are the source of truth in
[`Program.cs`](Program.cs); the app-side client lives in the TextRPG repo at
`src/app/core/services/dev/bridge.service.ts`.
