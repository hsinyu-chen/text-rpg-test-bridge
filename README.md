# BridgeServer — TextRPG dev relay

Local dev tool that lets an external agent drive a running [TextRPG](https://github.com/hsinyu-chen/text-rpg)
build over HTTP, instead of copy/pasting LLM output back and forth.

- Agent ↔ server on `http://127.0.0.1:5051` (plain, curl-friendly)
- App ↔ server on `wss://127.0.0.1:5050/app` (TLS, reuses TextRPG's `mkcert` dev cert)

Single in-flight turn, queue depth 5, 120 s long-poll timeout.

## Run

```pwsh
dotnet run
```

Defaults: `--http-port 5051 --wss-port 5050 --cert-dir ../TextRPG/.certs`. Override any flag if needed.

`dotnet run` looks for `dev.pem` + `dev-key.pem` first under `<cwd>/../TextRPG/.certs`, then under
`<exe-dir>/../TextRPG/.certs`. Run TextRPG's `npm start` once to generate them.

To produce a single-file exe:

```pwsh
dotnet publish -r win-x64 --self-contained -p:PublishSingleFile=true -c Release
```

## HTTP API (agent)

All endpoints are `POST` with a JSON body. Server long-polls until the app responds or 120 s elapses.

| Endpoint | Body | Response |
|----------|------|----------|
| `/send`  | `{ "userInput": "string", "intent": "action|continue|fast_forward|save|null" }` | `{ "messageId", "userMessageId", "pair": { "user": {...}, "model": {...} } }` |
| `/list`  | `{ "limit": 50 }` | `{ "messages": [ { "id", "role", "headPreview", "intent" } ] }` |
| `/delete`| `{ "messageId": "string", "alsoDeletePair": true }` | `{ "deleted": ["id", ...] }` |

Errors come back as `{ "error": "<code>" }`:

| HTTP | code | meaning |
|------|------|---------|
| 503 | `app_not_connected` | no app WebSocket attached, or it dropped mid-flight |
| 429 | `queue_full` | more than 5 requests queued behind the in-flight one |
| 504 | `app_timeout` | app did not produce a result within 120 s |
| 500 | `app_error` | app reported a generation error (see `detail`) |

### UTF-8 gotcha

On Windows, `curl -d` from cmd / Git Bash sends body bytes in the active console
codepage (often CP950 / CP936), and the server's UTF-8 JSON parser will replace any
invalid sequences with `U+FFFD` (`���`). If you need non-ASCII content, either:

- send from PowerShell with `Invoke-RestMethod` after explicit UTF-8 encoding:

  ```pwsh
  $body = @{ userInput = "([curious]glance around)Where am I?"; intent = "action" } | ConvertTo-Json -Compress
  $bytes = [System.Text.Encoding]::UTF8.GetBytes($body)
  Invoke-RestMethod -Uri http://127.0.0.1:5051/send -Method Post `
    -ContentType 'application/json; charset=utf-8' -Body $bytes
  ```

- or write the JSON to a UTF-8 file and use `curl --data-binary @body.json`.

## WS protocol (app)

The app connects to `wss://127.0.0.1:5050/app` and exchanges JSON frames keyed by `type` and
`requestId`. Frames are documented in [`Program.cs`](Program.cs); the app-side client lives in the
TextRPG repo at `src/app/core/services/dev/bridge.service.ts`.

## Scope

Dev-only. Single client, localhost only, no auth, no streaming. Production builds tree-shake the
client side via `isDevMode()`.
