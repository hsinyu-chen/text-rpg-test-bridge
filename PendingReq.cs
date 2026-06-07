using System.Text.Json.Nodes;

sealed record PendingReq(string ClientId, TaskCompletionSource<JsonNode> Tcs);
