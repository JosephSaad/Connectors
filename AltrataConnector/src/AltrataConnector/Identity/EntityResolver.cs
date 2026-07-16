// Identity/EntityResolver.cs
// --------------------------
// Deterministic entity resolution: link an Altrata person record to an
// internal CRM contact.
//
//   Rule 1 (email):          exact case-insensitive email match.
//   Rule 2 (name+employer):  normalized full name AND normalized employer match.
//
// No fuzzy matching — only deterministic rules, so a link is always
// explainable. Successful links are persisted in the crosswalk store and the
// CRM contact id is emitted as a property on the externalItem.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AltrataConnector.Identity;

public static class EntityNormalizer
{
    /// <summary>Lower-case, strip diacritics, collapse whitespace, drop punctuation.</summary>
    public static string? NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        var folded = StripDiacritics(name.Trim().ToLowerInvariant());
        var sb = new StringBuilder(folded.Length);
        var lastWasSpace = false;
        foreach (var ch in folded)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
                lastWasSpace = false;
            }
            else if (!lastWasSpace && sb.Length > 0)
            {
                sb.Append(' ');
                lastWasSpace = true;
            }
        }
        return sb.ToString().TrimEnd() is { Length: > 0 } result ? result : null;
    }

    /// <summary>Corporate suffixes ignored when comparing employers.</summary>
    private static readonly string[] EmployerSuffixes =
    {
        "incorporated", "inc", "corporation", "corp", "company", "co",
        "limited", "ltd", "llc", "llp", "plc", "gmbh", "sa", "ag", "holdings", "group",
    };

    /// <summary>Name normalization plus removal of trailing corporate suffixes.</summary>
    public static string? NormalizeEmployer(string? employer)
    {
        var normalized = NormalizeName(employer);
        if (normalized == null)
            return null;
        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (tokens.Count > 1 && EmployerSuffixes.Contains(tokens[^1]))
            tokens.RemoveAt(tokens.Count - 1);
        return tokens.Count > 0 ? string.Join(' ', tokens) : normalized;
    }

    private static string StripDiacritics(string text)
    {
        var formD = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(formD.Length);
        foreach (var ch in formD)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}

/// <summary>Match provenance: rule + confidence (1.0 for deterministic rules,
/// the fuzzy score for the fuzzy tier). Both are emitted onto the item.</summary>
public sealed record ResolutionResult(string CrmContactId, string MatchRule, double Confidence = 1.0);

// ---- fuzzy tier ---------------------------------------------------------------

/// <summary>
/// Scored fuzzy matching beneath the deterministic rules. Weights: normalized
/// name token overlap 0.6, employer token overlap 0.3, role-hint overlap 0.1
/// — so a fuzzy link needs a near-exact name plus corroborating employer
/// and/or role evidence to clear a strict threshold.
/// </summary>
public static class FuzzyMatcher
{
    public const double NameWeight = 0.6;
    public const double EmployerWeight = 0.3;
    public const double RoleWeight = 0.1;

    public sealed record MatchScore(double Total, double NameScore, double EmployerScore, double RoleScore);

    /// <summary>Jaccard overlap of whitespace tokens over two NORMALIZED strings;
    /// 0 when either side is null/empty.</summary>
    public static double TokenOverlap(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
            return 0;
        var setA = a.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var setB = b.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        if (setA.Count == 0 || setB.Count == 0)
            return 0;
        var intersection = setA.Intersect(setB).Count();
        var union = setA.Count + setB.Count - intersection;
        return (double)intersection / union;
    }

    /// <summary>Score one candidate (all inputs pre-normalized).</summary>
    public static MatchScore Score(string? nameNorm, string? employerNorm, string? roleNorm,
        CrmContact candidate)
    {
        var nameScore = TokenOverlap(nameNorm, candidate.Name);
        var employerScore = TokenOverlap(employerNorm, candidate.Employer);
        var roleScore = TokenOverlap(roleNorm, candidate.Role);
        return new MatchScore(
            Math.Round(NameWeight * nameScore + EmployerWeight * employerScore + RoleWeight * roleScore, 4),
            nameScore, employerScore, roleScore);
    }
}

/// <summary>Fuzzy tier configuration (all knobs env-driven; see env example).</summary>
public sealed record FuzzyOptions
{
    /// <summary>ENTITY_FUZZY_MATCHING — off by default (deterministic only).</summary>
    public bool Enabled { get; init; }

    /// <summary>ENTITY_MATCH_THRESHOLD — auto-link at/above this score.</summary>
    public double Threshold { get; init; } = 0.85;

    /// <summary>ENTITY_REVIEW_FLOOR — candidates in [floor, threshold) go to the
    /// review queue instead of auto-linking.</summary>
    public double ReviewFloor { get; init; } = 0.6;

    public IMatchReviewSink? Review { get; init; }
}

/// <summary>
/// A below-threshold candidate for a human to review. PII CAUTION: this record
/// is written to a log file (logs/match_review_*.jsonl), which enforces the
/// same ids/counts/hashes-only allowlist as every other connector log — it
/// carries ONLY ids, scores and short hashes of the compared values (for
/// dedup); never the raw name/employer. An adjudicator dereferences both sides
/// via the ids: the Altrata record through the feed/store and the CRM contact
/// through the identity store.
/// </summary>
public sealed record MatchReviewEntry
{
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
    public required string AltrataId { get; init; }
    public required string CandidateContactId { get; init; }
    public required double Score { get; init; }
    public double NameScore { get; init; }
    public double EmployerScore { get; init; }
    public double RoleScore { get; init; }

    /// <summary>Short SHA-256 of the NORMALIZED record name (dedup key, no PII).</summary>
    public string? NameHash { get; init; }

    /// <summary>Short SHA-256 of the NORMALIZED record employer (dedup key, no PII).</summary>
    public string? EmployerHash { get; init; }

    /// <summary>Stable short hash of a normalized value for review-queue dedup.</summary>
    public static string? HashValue(string? normalized) =>
        string.IsNullOrEmpty(normalized)
            ? null
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))
                .ToLowerInvariant()[..16];
}

public interface IMatchReviewSink
{
    void Add(MatchReviewEntry entry);
}

/// <summary>Append-only JSONL review queue at logs/match_review_{CONNECTOR_ID}.jsonl.</summary>
public sealed class MatchReviewQueue : IMatchReviewSink
{
    private readonly object _sync = new();

    public string Path { get; }

    public MatchReviewQueue(string connectorId, string? logsDir = null)
    {
        var dir = logsDir
            ?? Environment.GetEnvironmentVariable("LOGS_DIR")
            ?? System.IO.Path.Combine(Directory.GetCurrentDirectory(), "logs");
        Path = System.IO.Path.Combine(dir, $"match_review_{connectorId}.jsonl");
    }

    public void Add(MatchReviewEntry entry)
    {
        lock (_sync)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            using var stream = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.WriteLine(System.Text.Json.JsonSerializer.Serialize(entry));
        }
    }

    public IReadOnlyList<MatchReviewEntry> ReadAll()
    {
        lock (_sync)
        {
            if (!File.Exists(Path))
                return Array.Empty<MatchReviewEntry>();
            var entries = new List<MatchReviewEntry>();
            foreach (var line in File.ReadLines(Path))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                try
                {
                    var entry = System.Text.Json.JsonSerializer.Deserialize<MatchReviewEntry>(line);
                    if (entry != null)
                        entries.Add(entry);
                }
                catch
                {
                    // tolerate manual edits
                }
            }
            return entries;
        }
    }
}

// ---- resolver ------------------------------------------------------------------

public sealed class EntityResolver
{
    public const string RuleEmail = "email";
    public const string RuleNameEmployer = "name+employer";
    public const string RuleFuzzy = "fuzzy";

    private readonly IIdentityStore _store;
    private readonly FuzzyOptions _fuzzy;
    private IReadOnlyList<CrmContact>? _candidatePool;  // loaded lazily, once

    public EntityResolver(IIdentityStore store, FuzzyOptions? fuzzy = null)
    {
        _store = store;
        _fuzzy = fuzzy ?? new FuzzyOptions();
    }

    /// <summary>
    /// Resolve a person to a CRM contact. Tier 1: deterministic email, then
    /// normalized name+employer. Tier 2 (opt-in): scored fuzzy match — auto-
    /// link at/above the threshold, review-queue in [floor, threshold).
    /// A successful match is persisted to the crosswalk with its provenance.
    /// </summary>
    public ResolutionResult? Resolve(string altrataId, string? email, string? name, string? employer,
        DateTime? utcNow = null, string? role = null)
    {
        var match = Match(email, name, employer, role, altrataId);
        if (match == null)
            return null;
        _store.UpsertCrosswalk(new CrosswalkEntry(
            altrataId, match.CrmContactId, match.MatchRule, utcNow ?? DateTime.UtcNow));
        return match;
    }

    /// <summary>Pure matching (no crosswalk write) — deterministic rules, then fuzzy.</summary>
    public ResolutionResult? Match(string? email, string? name, string? employer,
        string? role = null, string? altrataId = null)
    {
        if (!string.IsNullOrWhiteSpace(email))
        {
            var contact = _store.FindContactByEmail(email.Trim().ToLowerInvariant());
            if (contact != null)
                return new ResolutionResult(contact.Id, RuleEmail);
        }

        var normalizedName = EntityNormalizer.NormalizeName(name);
        var normalizedEmployer = EntityNormalizer.NormalizeEmployer(employer);
        if (normalizedName != null && normalizedEmployer != null)
        {
            var contact = _store.FindContactByNameEmployer(normalizedName, normalizedEmployer);
            if (contact != null)
                return new ResolutionResult(contact.Id, RuleNameEmployer);
        }

        if (!_fuzzy.Enabled || normalizedName == null)
            return null;

        // Tier 2: scored fuzzy over the candidate pool (loaded once per resolver).
        var normalizedRole = EntityNormalizer.NormalizeName(role);
        _candidatePool ??= _store.ListCrmContacts();

        CrmContact? best = null;
        FuzzyMatcher.MatchScore? bestScore = null;
        foreach (var candidate in _candidatePool)
        {
            var score = FuzzyMatcher.Score(normalizedName, normalizedEmployer, normalizedRole, candidate);
            if (score.NameScore <= 0)
                continue;  // a fuzzy link without any name evidence is never acceptable
            if (bestScore == null || score.Total > bestScore.Total)
            {
                best = candidate;
                bestScore = score;
            }
        }

        if (best == null || bestScore == null)
            return null;

        if (bestScore.Total >= _fuzzy.Threshold)
        {
            return new ResolutionResult(best.Id, RuleFuzzy, Math.Round(bestScore.Total, 2));
        }

        if (bestScore.Total >= _fuzzy.ReviewFloor)
        {
            // Ids + scores + hashes ONLY — raw name/employer are PII and must
            // never reach the review-queue log file.
            _fuzzy.Review?.Add(new MatchReviewEntry
            {
                AltrataId = altrataId ?? "(unknown)",
                CandidateContactId = best.Id,
                Score = bestScore.Total,
                NameScore = bestScore.NameScore,
                EmployerScore = bestScore.EmployerScore,
                RoleScore = bestScore.RoleScore,
                NameHash = MatchReviewEntry.HashValue(normalizedName),
                EmployerHash = MatchReviewEntry.HashValue(normalizedEmployer),
            });
        }
        return null;
    }
}
