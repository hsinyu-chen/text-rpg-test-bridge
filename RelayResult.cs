using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
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
    // UnsafeRelaxedJsonEscaping so non-ASCII game text (CJK) serializes verbatim instead of \uXXXX.
    private static readonly JsonSerializerOptions JsonOpts =
        new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static RelayResult Success(JsonNode? value) => new(false, value, null, null);

    public static RelayResult Error(string code, JsonObject? fields = null) => new(true, null, code, fields);

    public CallToolResult ToCallToolResult()
    {
        if (!IsError)
        {
            var json = Value?.ToJsonString(JsonOpts) ?? "null";
            return new CallToolResult
            {
                IsError = false,
                Content = [new TextContentBlock { Text = json }],
            };
        }

        var body = ErrorFields is null ? [] : (JsonObject)ErrorFields.DeepClone();
        body["error"] = Code;
        return new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = body.ToJsonString(JsonOpts) }],
        };
    }
}
