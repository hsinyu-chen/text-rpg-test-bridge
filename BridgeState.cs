using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

sealed class BridgeState
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
