// Config/DeadLetterRedaction.cs
// -----------------------------
// Dead-letter payload protection (DEADLETTER_PAYLOAD_MODE=full|redacted).
//
// A dead-letter entry normally embeds the full failed externalItem request
// body — Salesforce field VALUES included — which turns the queue file / SQL
// table into a second copy of business data with its own access-control story
// (docs/THREAT_MODEL.md). In `redacted` mode the record values are stripped
// BEFORE the entry is written to either backend:
//
//   kept    : item id, object type, error, timestamp, correlation id,
//             property NAMES, sizes, and SHA-256 hashes (payload + content)
//   stripped: every property value, the content text, the ACL principal list,
//             the raw Graph response body (hashed instead)
//
// retry-failed is unaffected by design: it re-locates the record in BDH by
// item_id + object_type (fresh fields + fresh ACL) and never replays the
// stored payload. Default is `full` (unchanged behaviour); an UNRECOGNIZED
// mode redacts and warns — at this data's sensitivity a typo must fail toward
// less exposure, mirroring the chassis' fail-closed filter philosophy.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using HadoopConnector.Infrastructure;

namespace HadoopConnector.Config;

public static class DeadLetterRedaction
{
    public const string ModeEnvVar = "DEADLETTER_PAYLOAD_MODE";

    private static readonly IAppLogger Logger = Logging.GetLogger("hadoop_connector");
    private static int _warnedUnknownMode;

    /// <summary>True when DEADLETTER_PAYLOAD_MODE requires payload redaction.</summary>
    public static bool RedactionEnabled
    {
        get
        {
            var raw = EnvFlags.GetString(ModeEnvVar, "full").Trim().ToLowerInvariant();
            switch (raw)
            {
                case "full":
                    return false;
                case "redacted":
                    return true;
                default:
                    // Fail toward LESS exposure: an unknown value redacts.
                    if (Interlocked.Exchange(ref _warnedUnknownMode, 1) == 0)
                    {
                        Logger.Warning(
                            $"{ModeEnvVar}='{raw}' is not recognized (expected full|redacted); "
                            + "treating it as 'redacted' so payloads are not stored by accident.");
                    }
                    return true;
            }
        }
    }

    /// <summary>Test seam: re-arm the one-time unknown-mode warning.</summary>
    internal static void ResetForTests() => Interlocked.Exchange(ref _warnedUnknownMode, 0);

    /// <summary>
    /// Redact one externalItem request body: keep the id, the property NAMES,
    /// sizes and hashes; drop every value, the content text and the ACL list.
    /// Null stays null (the entry simply has no request_body).
    /// </summary>
    public static JsonNode? RedactRequestBody(JsonNode? body)
    {
        if (body is null)
            return null;
        var redacted = new JsonObject { ["redacted"] = true };
        if (body is JsonObject obj)
        {
            if (obj.TryGetPropertyValue("id", out var id) && id is not null)
                redacted["id"] = id.DeepClone();
            if (obj.TryGetPropertyValue("properties", out var props) && props is JsonObject p)
            {
                var names = new JsonArray();
                foreach (var kv in p)
                    names.Add(JsonValue.Create(kv.Key));
                redacted["property_names"] = names;
            }
            if (obj.TryGetPropertyValue("content", out var content)
                && content is JsonObject c
                && c.TryGetPropertyValue("value", out var value)
                && value is JsonValue v
                && v.TryGetValue<string>(out var text))
            {
                redacted["content_length"] = text.Length;
                redacted["content_sha256"] = Sha256Hex(text);
            }
            if (obj.TryGetPropertyValue("acl", out var acl) && acl is JsonArray a)
                redacted["acl_entries"] = a.Count;
        }
        redacted["payload_sha256"] = Sha256Hex(body.ToJsonString());
        return redacted;
    }

    /// <summary>Redact one Graph response body: length + hash only (error text
    /// can echo submitted values back).</summary>
    public static JsonNode? RedactResponseBody(JsonNode? body)
    {
        if (body is null)
            return null;
        var raw = body.ToJsonString();
        return new JsonObject
        {
            ["redacted"] = true,
            ["body_length"] = raw.Length,
            ["body_sha256"] = Sha256Hex(raw),
        };
    }

    /// <summary>Apply the configured mode to a request/response body map (null-safe).</summary>
    internal static Dictionary<string, JsonNode?>? Apply(
        Dictionary<string, JsonNode?>? bodies, Func<JsonNode?, JsonNode?> redactOne)
    {
        if (bodies is null || !RedactionEnabled)
            return bodies;
        var redacted = new Dictionary<string, JsonNode?>(bodies.Count, StringComparer.Ordinal);
        foreach (var (itemId, body) in bodies)
            redacted[itemId] = redactOne(body);
        return redacted;
    }

    /// <summary>Lowercase hex SHA-256 (FIPS-approved primitive) of the UTF-8 text.</summary>
    internal static string Sha256Hex(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
