// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Config/DeadLetterRedaction.cs
// -----------------------------
// DEADLETTER_PAYLOAD_MODE=redacted|full (DEFAULT redacted — the safe default,
// matching the Altrata connector; see SECURITY.md / docs/THREAT_MODEL.md §4).
// Set DEADLETTER_PAYLOAD_MODE=full to keep the complete request/response
// payloads for standalone debugging. Any other value fails fast at config load
// (Settings.LoadConfig) naming the setting — it is never silently downgraded to
// full.
//
// Dead-letter records capture the full Graph request/response for debugging,
// which means CRM field values (customer names, emails, case text) can sit in
// logs/failed_records_*.jsonl (or dbo.DeadLetter) indefinitely. In redacted
// mode (the default) the ITEM CONTENT is stripped before the record is written,
// keeping everything retry and triage actually use:
//
//   kept    item_id, object_type, error, timestamp, acl entries, field NAMES
//   hashed  every value under "properties", and "content.value"
//           (sha256:<hex> of the value's JSON — lets you correlate two
//           failures of the same payload without storing the payload)
//
// The trade-off is recorded in the record itself ("@redaction" note).
// retry-failed is unaffected: it re-fetches each item from Salesforce by
// item_id/object_type and never replays the stored request_body.
//
// Applies to BOTH state backends — SyncState.AppendFailedRecords redacts
// before branching to the JSONL file or SqlStateStore.AppendDeadLetter.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace SalesforceCopilotConnector.Config;

public static class DeadLetterRedaction
{
    public const string ModeEnvVar = "DEADLETTER_PAYLOAD_MODE";

    /// <summary>Full-fidelity mode — capture the complete request/response payloads.</summary>
    public const string Full = "full";

    /// <summary>Redacted mode (the shipped default) — property values / content hashed.</summary>
    public const string Redacted = "redacted";

    /// <summary>Note embedded in every redacted payload (spec'd wording — SIEM/triage keys on it).</summary>
    public const string Note =
        "payload redacted per DEADLETTER_PAYLOAD_MODE=redacted — property values and content hashed (sha256); " +
        "retry-failed re-fetches the item from Salesforce";

    /// <summary>Key carrying <see cref="Note"/> inside a redacted body ('@' avoids Graph field collisions).</summary>
    public const string NoteKey = "@redaction";

    /// <summary>
    /// Effective mode (live env read). <b>Default <see cref="Redacted"/></b>: unset/blank ⇒
    /// redacted, so raw CRM values never hit disk unless an operator explicitly opts in to
    /// <see cref="Full"/>. An unrecognized value throws <see cref="ArgumentException"/> so the
    /// misconfiguration fails fast at config load rather than silently defaulting to full.
    /// </summary>
    public static string Mode
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable(ModeEnvVar);
            if (string.IsNullOrWhiteSpace(raw))
                return Redacted;
            var value = raw.Trim().ToLowerInvariant();
            if (value is Full or Redacted)
                return value;
            throw new ArgumentException(
                $"Invalid configuration: {ModeEnvVar} must be '{Full}' or '{Redacted}', got '{raw.Trim()}'");
        }
    }

    /// <summary>True unless <c>DEADLETTER_PAYLOAD_MODE=full</c> (redacted is the default).</summary>
    public static bool RedactedMode => Mode == Redacted;

    /// <summary>Redact every body in a request/response dictionary (null-safe).</summary>
    internal static Dictionary<string, JsonNode?>? RedactBodies(Dictionary<string, JsonNode?>? bodies)
    {
        if (bodies == null)
            return null;
        var redacted = new Dictionary<string, JsonNode?>(bodies.Count);
        foreach (var (itemId, body) in bodies)
            redacted[itemId] = RedactPayload(body);
        return redacted;
    }

    /// <summary>
    /// Redact one captured body. Objects are walked recursively: values under a
    /// <c>properties</c> object and <c>content.value</c> are replaced with
    /// <c>sha256:&lt;hex&gt;</c> of their JSON; every other node (acl, ids, error
    /// details) is copied unchanged. Non-object bodies are replaced wholesale by
    /// a hash envelope. <c>null</c> stays <c>null</c> (captured-but-null).
    /// </summary>
    public static JsonNode? RedactPayload(JsonNode? body)
    {
        switch (body)
        {
            case null:
                return null;
            case JsonObject obj:
            {
                var copy = RedactObject(obj, hashAllValues: false);
                copy[NoteKey] = Note;
                return copy;
            }
            default:
                return new JsonObject
                {
                    [NoteKey] = Note,
                    ["sha256"] = HashOf(body),
                };
        }
    }

    private static JsonObject RedactObject(JsonObject obj, bool hashAllValues)
    {
        var copy = new JsonObject();
        foreach (var (key, value) in obj)
        {
            if (hashAllValues)
            {
                copy[key] = HashOf(value);
            }
            else if (key == "properties" && value is JsonObject properties)
            {
                // Field names survive; field values become hashes.
                copy[key] = RedactObject(properties, hashAllValues: true);
            }
            else if (key == "content" && value is JsonObject content)
            {
                var contentCopy = new JsonObject();
                foreach (var (contentKey, contentValue) in content)
                {
                    contentCopy[contentKey] = contentKey == "value"
                        ? HashOf(contentValue)
                        : contentValue?.DeepClone();
                }
                copy[key] = contentCopy;
            }
            else if (value is JsonObject nested)
            {
                copy[key] = RedactObject(nested, hashAllValues: false);
            }
            else if (value is JsonArray array)
            {
                var arrayCopy = new JsonArray();
                foreach (var element in array)
                    arrayCopy.Add(element is JsonObject elementObj
                        ? RedactObject(elementObj, hashAllValues: false)
                        : element?.DeepClone());
                copy[key] = arrayCopy;
            }
            else
            {
                copy[key] = value?.DeepClone();
            }
        }
        return copy;
    }

    /// <summary><c>sha256:&lt;lowercase hex&gt;</c> of the node's compact JSON (null → "null").</summary>
    internal static string HashOf(JsonNode? value)
    {
        var json = value?.ToJsonString() ?? "null";
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return "sha256:" + Convert.ToHexString(digest).ToLowerInvariant();
    }
}
