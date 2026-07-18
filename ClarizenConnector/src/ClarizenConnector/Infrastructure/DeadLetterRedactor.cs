// Infrastructure/DeadLetterRedactor.cs
// ------------------------------------
// Dead-letter payload protection (DEADLETTER_PAYLOAD_MODE=full|redacted,
// default REDACTED — validated in AppConfig.Load).
//
// The shipped default is `redacted` (protective): a dead-letter file/table is
// long-lived source data at rest, so it is stripped unless an operator opts
// into `full` for diagnostic-rich records. Set DEADLETTER_PAYLOAD_MODE=full to
// keep verbatim payloads (do this only where the queue is ACL'd tightly).
//
// Dead-letter records optionally carry the failed externalItem request body
// for diagnosis. That body contains real source data — including financial
// figures under FINANCIAL_DATA_MODE=tag/acl — sitting in a plain JSONL file
// (or dbo.DeadLetter) with a long lifetime. In `redacted` mode the payload is
// stripped at the single choke point every backend shares
// (SyncState.AppendFailedRecords), so file AND SQL records are covered, on
// every dead-letter path including the financial-classification ones:
//
//   kept     item id, object type, error, timestamp, correlation id, attempt
//            info embedded in the error string
//   hashed   one SHA-256 per property VALUE and for the content body, so an
//            operator can still tell "same payload failed again" without the
//            value; property NAMES are kept
//   dropped  property values, content text, the ACL list (count kept), and
//            the response body (Graph validation errors can echo the request)
//
// `retry-failed` never needs the payload: it re-fetches every record from
// Clarizen by id and rebuilds the item, so redacted mode does not reduce
// retryability (tested). Hashes are SHA-256 (FIPS-approved).

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace ClarizenConnector.Infrastructure;

public static class DeadLetterRedactor
{
    public const string ModeEnvVar = "DEADLETTER_PAYLOAD_MODE";

    /// <summary>
    /// True unless DEADLETTER_PAYLOAD_MODE is explicitly <c>full</c> (read at
    /// write time). The default (unset) is redacted — protective by default —
    /// so only a deliberate <c>full</c> keeps verbatim payloads on disk. Any
    /// other value is rejected at config load (AppConfig.Load), so treating an
    /// unrecognised value here as redacted is the fail-safe.
    /// </summary>
    public static bool RedactionEnabled =>
        !string.Equals(
            Environment.GetEnvironmentVariable(ModeEnvVar)?.Trim(), "full",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Redact one request payload (externalItem shape). Non-object payloads
    /// collapse to a marker — nothing unrecognised passes through verbatim.
    /// </summary>
    public static JsonNode? RedactRequestBody(JsonNode? payload)
    {
        if (payload is null)
            return null;
        if (payload is not JsonObject obj)
            return JsonValue.Create("[redacted]");

        var result = new JsonObject { ["redacted"] = true };
        if (obj.TryGetPropertyValue("id", out var id) && id is not null)
            result["id"] = id.DeepClone();

        if (obj.TryGetPropertyValue("properties", out var props) && props is JsonObject properties)
        {
            var hashed = new JsonObject();
            foreach (var (name, value) in properties)
                hashed[name] = value is null ? null : HashNode(value);
            result["properties"] = hashed;
        }

        if (obj.TryGetPropertyValue("content", out var content)
            && content is JsonObject contentObj
            && contentObj.TryGetPropertyValue("value", out var contentValue)
            && contentValue is not null)
        {
            var text = contentValue.GetValueKind() == System.Text.Json.JsonValueKind.String
                ? contentValue.GetValue<string>()
                : contentValue.ToJsonString();
            result["content_sha256"] = Sha256Hex(text);
            result["content_length"] = text.Length;
        }

        if (obj.TryGetPropertyValue("acl", out var acl) && acl is JsonArray aclArray)
            result["acl_count"] = aclArray.Count;

        return result;
    }

    /// <summary>Redact a request-body map in place-shape (null map stays null).</summary>
    public static Dictionary<string, JsonNode?>? RedactRequestBodies(
        Dictionary<string, JsonNode?>? requestBodies)
    {
        if (requestBodies is null)
            return null;
        var redacted = new Dictionary<string, JsonNode?>(requestBodies.Count, StringComparer.Ordinal);
        foreach (var (itemId, payload) in requestBodies)
            redacted[itemId] = RedactRequestBody(payload);
        return redacted;
    }

    /// <summary>"sha256:&lt;hex&gt;" of a JSON value's canonical text — comparable
    /// across failures without exposing the value.</summary>
    internal static string HashNode(JsonNode value)
    {
        var text = value.GetValueKind() == System.Text.Json.JsonValueKind.String
            ? value.GetValue<string>()
            : value.ToJsonString();
        return "sha256:" + Sha256Hex(text);
    }

    internal static string Sha256Hex(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
