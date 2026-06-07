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

## MCP access + token auth

The `/mcp` endpoint speaks the standard Model Context Protocol over Streamable
HTTP — point any MCP client at `http://<host>:5050/mcp`. Each former agent-facing
HTTP route is now an MCP tool (`send`, `list`, `delete`, `reload`, `clients`,
`config_get`/`config_set`, the `profile_*` / `llm_*` / `book_*` / `agent_*`
families, …). Every tool takes an optional `clientId` for multi-client routing.

Access to `/mcp` is gated by a **required** bearer token in the `BRIDGE_MCP_TOKEN`
environment variable:

- **The token is mandatory.** If `BRIDGE_MCP_TOKEN` is unset or blank the server
  refuses to start — it writes a fatal message to stderr and exits non-zero, so
  `/mcp` is never served unauthenticated.
- **Every `/mcp` request must carry** `Authorization: Bearer <BRIDGE_MCP_TOKEN>`,
  compared in fixed time. **There is no loopback bypass** — connections from
  `127.0.0.1` / `::1` need the token too. The decision reads only the header,
  never `X-Forwarded-For` or any forwarded header.
- A missing or wrong token gets a `401` with a `WWW-Authenticate: Bearer`
  response header and an empty body.

The token gates `/mcp` only — the app-facing WebSocket at `/app` (same port) is
unaffected (it carries no token).

The MCP tool surface and the WebSocket frame contract are the source of truth in
[`Program.cs`](Program.cs); the app-side client lives in the TextRPG repo at
`src/app/core/services/dev/bridge.service.ts`.
