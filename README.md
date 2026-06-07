# BridgeServer — TextRPG dev relay

A small .NET 10 web app that lets an external agent drive a running
[TextRPG](https://github.com/hsinyu-chen/text-rpg) build over HTTP, instead of
copy/pasting LLM output back and forth.

- Agent ↔ server: plain **HTTP** on port `5051`
- App ↔ server: plain **WebSocket** on port `5050` (`/app`)

Both ports are plain — no TLS. Terminate TLS upstream (e.g. nginx) if you need it.

## Run / deploy

It's an ordinary ASP.NET Core app — deploy it however you like (`dotnet run`,
`dotnet publish`, or the bundled [`Dockerfile`](Dockerfile)). Optional flags:
`--http-port` (default `5051`), `--wss-port` (default `5050`).

The HTTP routes and the WebSocket frame contract are the source of truth in
[`Program.cs`](Program.cs); the app-side client lives in the TextRPG repo at
`src/app/core/services/dev/bridge.service.ts`.
