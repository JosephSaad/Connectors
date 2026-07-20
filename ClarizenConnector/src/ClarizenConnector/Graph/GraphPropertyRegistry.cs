// Graph/GraphPropertyRegistry.cs
// ------------------------------
// THE SINGLE SOURCE OF TRUTH for "which Graph property names may this connector
// publish", and the mechanism that makes publishing an undeclared one
// UNREPRESENTABLE rather than merely discouraged.
//
// WHY THIS EXISTS
// Microsoft Graph rejects externalItem properties that are not in the registered
// connection schema. The connector therefore ships undeployable the moment the
// code stamps a name that config/graph-schema.json does not declare — and that
// failure is invisible locally, because nothing in a unit test talks to Graph.
//
// THE PREVIOUS GUARD HAD A HOLE OF EXACTLY THE SHAPE IT WAS BUILT TO CATCH.
// It compared graph-schema.json against two hand-curated symbol lists
// (SchemaConfig.ReservedPropertyNames and ItemConverter.StandardPropertyNames).
// A property stamped with a BARE STRING LITERAL —
//     item.Properties["SomethingNew"] = value;
// which is precisely the coding style that caused the original ContentGateStatus
// defect — appeared in neither list, so the guard stayed green while an
// undeclared property sailed through to the Graph PUT. A guard that depends on a
// developer REMEMBERING to register a name cannot catch the mistake of not
// registering a name.
//
// HOW IT IS CLOSED NOW
//   1. WRITE TIME — THIS IS THE GUARANTEE.  ExternalItem.Properties is a
//      GraphPropertyBag, not a Dictionary. Its setter asks this registry. An
//      undeclared name throws at the exact line that stamped it, with the
//      offending name in the message — literal, const, or computed at runtime,
//      it makes no difference, because the check is on the VALUE of the name and
//      not on where the name came from. Nothing has to be registered anywhere
//      for a new stamp to be covered.
//      A BLANK OR WHITESPACE-ONLY NAME IS THE SAME FAULT and raises the same
//      type. It used to raise ArgumentException, which the crawl's per-record
//      catch(Exception) demoted to a dead-lettered bad row — so the crawl closed
//      normally and THE SYNC CURSOR ADVANCED past records that never reached the
//      index. That is the precise failure this layer exists to prevent.
//      During a crawl, IngestPipeline deliberately does NOT let its per-record /
//      per-object isolation catches demote GraphSchemaConfigurationException to a
//      dead-lettered "bad row": it is a defect that hits every record, so it
//      aborts the run instead of closing a crawl with an empty index. The base
//      type, not the leaf type, is what those catches key on — so any fault of
//      the "the configuration cannot produce a deployable item" class escalates,
//      including a declaration file that cannot be loaded at all
//      (GraphSchemaUnavailableException).
//   2. SERIALIZE TIME.  ExternalItem.ToJson() re-checks the whole bag, so a bag
//      populated by any future route still cannot produce a Graph body carrying
//      an undeclared property. This re-check IGNORES SuspendEnforcement: the
//      scope below is for observing what the code stamps, and must not widen the
//      last checkpoint before the wire. (It used to honour it, which made the
//      guarantee documented on ToJson false inside any suspension scope.)
//   3. PREFLIGHT — BEST EFFORT, NOT A GUARANTEE.  Commands/ValidateConfig runs
//      StampedPropertyInventory over the real schema.json and diffs the names it
//      observes against the declaration, so `validate-config` on a deployment
//      host usually reports drift in a hand-edited schema before a crawl starts.
//      It invokes a FIXED SET of stamper call sites, so a stamp written anywhere
//      it does not invoke is invisible to it. Do not treat a green preflight as
//      proof of no drift; layer 1 is what actually holds.
//
// The declaration is read from config/graph-schema.json — the same file that is
// PATCHed to Graph — so the registry cannot drift from what Graph will accept.

using System.Text.Json;

namespace ClarizenConnector.Graph;

/// <summary>
/// Base for the failures the stamp/serialize path raises when the CONNECTOR'S
/// CONFIGURATION cannot produce a deployable item — as opposed to a single
/// poisoned source record.
/// <para>
/// The distinction is the whole point of the type. IngestPipeline's per-record
/// and per-object isolation catches dead-letter an exception and carry on; that
/// is right for a bad row and catastrophically wrong for a configuration defect,
/// which hits EVERY record. Demoting one produces a crawl that closes
/// "successfully" having indexed nothing, ADVANCES THE SYNC CURSOR past the
/// records it never indexed, and leaves the gap invisible until someone reads
/// the dead-letter file. Anything thrown on the stamp/serialize path that is a
/// property of the configuration rather than of the record must derive from this
/// so those catches re-throw it.
/// </para>
/// </summary>
public abstract class GraphSchemaConfigurationException : InvalidOperationException
{
    protected GraphSchemaConfigurationException(string message)
        : base(message)
    {
    }

    protected GraphSchemaConfigurationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown when the connector tries to publish a Graph property name that
/// config/graph-schema.json does not declare — or a name that is blank, which no
/// schema can ever declare. Graph would reject the item; this turns a silent
/// deployment-time rejection into a loud local failure.
/// </summary>
public sealed class UndeclaredGraphPropertyException : GraphSchemaConfigurationException
{
    public UndeclaredGraphPropertyException(string propertyName, string schemaPath)
        : base($"Graph property '{propertyName}' is not declared in the connection schema "
            + $"('{schemaPath}'). Microsoft Graph rejects undeclared properties, so publishing it "
            + "would make the connector undeployable. Add it to config/graph-schema.json (name, "
            + "type and the is* flags) — or stop stamping it.")
        => PropertyName = propertyName;

    /// <summary>The blank / whitespace-only name case. Same type, because it is
    /// the same fault with the same blast radius: a name no connection schema can
    /// declare, produced by configuration rather than by the record in hand. It
    /// used to raise <see cref="ArgumentException"/>, which the crawl's per-record
    /// catch demoted to a dead-lettered bad row — the crawl then closed normally
    /// and advanced the sync cursor past every record.</summary>
    public UndeclaredGraphPropertyException(string? propertyName)
        : base($"Graph property name '{propertyName}' is blank or whitespace-only. No connection "
            + "schema can declare it and Microsoft Graph would reject every item carrying it, so "
            + "this is a configuration defect that hits every record — not a bad source row. It "
            + "usually means a selectedFields entry in config/schema.json maps a field onto an "
            + "empty Graph property name.")
        => PropertyName = propertyName ?? string.Empty;

    public string PropertyName { get; }
}

/// <summary>
/// Thrown when the connection-schema declaration itself cannot be loaded at the
/// moment a property is stamped — the file is missing, is not a JSON array, or
/// declares no usable names. Same class of fault as an undeclared name (the
/// configuration, not the record) and therefore the same fatal handling: without
/// a declaration the connector cannot know whether ANY item is deployable, so
/// dead-lettering record after record while the cursor advances is the worst
/// possible response.
/// </summary>
public sealed class GraphSchemaUnavailableException : GraphSchemaConfigurationException
{
    public GraphSchemaUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The set of Graph property names config/graph-schema.json declares. Loaded
/// once and cached; <see cref="Reset"/> and <see cref="OverridePath"/> exist for
/// tests and for commands that validate a schema file other than the deployed one.
/// </summary>
public static class GraphPropertyRegistry
{
    private static readonly object Lock = new();
    private static HashSet<string>? _declared;
    private static string? _resolvedPath;
    private static string? _overridePath;

    /// <summary>File name of the connection-schema document, relative to a
    /// <c>config</c> directory.</summary>
    public const string FileName = "graph-schema.json";

    /// <summary>
    /// Point the registry at a specific graph-schema.json. Setting it clears the
    /// cache. Null restores the default probe order. Test/CLI hook only —
    /// production leaves this alone and gets the deployed config.
    /// </summary>
    public static string? OverridePath
    {
        get { lock (Lock) return _overridePath; }
        set
        {
            lock (Lock)
            {
                _overridePath = value;
                _declared = null;
                _resolvedPath = null;
            }
        }
    }

    /// <summary>Drop the cache (re-reads the file on next use).</summary>
    public static void Reset()
    {
        lock (Lock)
        {
            _declared = null;
            _resolvedPath = null;
        }
    }

    /// <summary>Path the registry actually read (for error messages).</summary>
    public static string ResolvedPath
    {
        get
        {
            EnsureLoaded();
            lock (Lock) return _resolvedPath!;
        }
    }

    /// <summary>Every property name declared in config/graph-schema.json.</summary>
    public static IReadOnlySet<string> Declared
    {
        get
        {
            EnsureLoaded();
            lock (Lock) return _declared!;
        }
    }

    /// <summary>True when the connection schema declares <paramref name="name"/>.</summary>
    public static bool IsDeclared(string name) => Declared.Contains(name);

    /// <summary>
    /// The checked stamp. Throws <see cref="UndeclaredGraphPropertyException"/>
    /// naming the property when the connection schema does not declare it.
    /// </summary>
    public static void AssertDeclared(string name)
    {
        // The blank check is deliberately AHEAD of the suspension check: a blank
        // name is unusable no matter who is asking, and letting the inventory
        // collect one would turn a fatal config error into a reported "drift"
        // warning. Keeping it here is also what preserves the validate-config
        // mitigation, which surfaces it via StampedPropertyInventory.Collect.
        if (string.IsNullOrWhiteSpace(name))
            throw new UndeclaredGraphPropertyException(name);
        if (_suspendDepth > 0)
            return;
        if (!IsDeclared(name))
            throw new UndeclaredGraphPropertyException(name, ResolvedPath);
    }

    /// <summary>
    /// The same check with suspension IGNORED. Used by
    /// <see cref="ExternalItem.ToJson"/>, whose documented guarantee is about the
    /// bytes that reach a Graph PUT: an inventory scope widening the WRITE path
    /// must not also widen the last checkpoint before the wire. Safe because the
    /// only production caller of <see cref="SuspendEnforcement"/> —
    /// <see cref="StampedPropertyInventory"/> — reads property NAMES off the item
    /// and never serializes or sends it.
    /// </summary>
    public static void AssertDeclaredIgnoringSuspension(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new UndeclaredGraphPropertyException(name);
        if (!IsDeclared(name))
            throw new UndeclaredGraphPropertyException(name, ResolvedPath);
    }

    // ── Enforcement suspension (inventory collection only) ──────────────────
    //
    // StampedPropertyInventory has to observe what the code stamps even when
    // that name is NOT declared — otherwise it could never report the drift it
    // exists to report. Deliberately narrow:
    //   • [ThreadStatic], so it can never widen a write on another thread (the
    //     crawl is parallel; a process-global flag would be a real bypass),
    //   • depth-counted and restored in a finally, so an exception inside the
    //     scope cannot leave enforcement off,
    //   • it does NOT relax ExternalItem.ToJson's check AT ALL — ToJson calls
    //     AssertDeclaredIgnoringSuspension, so the serialize-time layer holds
    //     inside a suspension scope too. Safe because the only production caller
    //     below (StampedPropertyInventory.Capture) reads property NAMES off the
    //     item and never serializes or sends it.
    //   • it does NOT relax the blank-name check, which runs ahead of it.
    // Nothing else in the connector may call this.

    [ThreadStatic]
    private static int _suspendDepth;

    /// <summary>True while this thread is collecting the stamped-property
    /// inventory. Diagnostic only.</summary>
    internal static bool EnforcementSuspended => _suspendDepth > 0;

    internal static IDisposable SuspendEnforcement() => new Suspension();

    private sealed class Suspension : IDisposable
    {
        private bool _disposed;

        public Suspension() => _suspendDepth++;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _suspendDepth--;
        }
    }

    /// <summary>Parse the declared property names out of a graph-schema.json
    /// document. Used by the registry and by preflight validation of a file
    /// other than the deployed one.</summary>
    public static HashSet<string> ReadDeclaredNames(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException(
                $"'{path}' must be a JSON array of property definitions.");

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateArray())
        {
            if (property.ValueKind == JsonValueKind.Object
                && property.TryGetProperty("name", out var name)
                && name.ValueKind == JsonValueKind.String
                && name.GetString() is { Length: > 0 } value)
            {
                names.Add(value);
            }
        }
        if (names.Count == 0)
            throw new InvalidDataException(
                $"'{path}' declares no property names — refusing to treat an empty declaration as "
                + "permission to publish nothing, because that silently disables the guard.");
        return names;
    }

    private static void EnsureLoaded()
    {
        lock (Lock)
        {
            if (_declared is not null)
                return;

            // Both failures below are raised from inside AssertDeclared, i.e. on
            // the per-record stamp path, where catch(Exception) would dead-letter
            // them one record at a time. They are configuration faults, so they
            // are wrapped in a GraphSchemaConfigurationException that the crawl's
            // isolation catches re-throw. The inner exception is preserved.
            var path = ResolvePath();
            if (path is null)
            {
                throw new GraphSchemaUnavailableException(
                    $"Could not locate config/{FileName}. The Graph connection schema is the "
                    + "authority on which properties may be published; without it the connector "
                    + "cannot know whether an item is deployable. Run from the connector root, or "
                    + $"set {nameof(GraphPropertyRegistry)}.{nameof(OverridePath)}.",
                    new FileNotFoundException($"config/{FileName} not found."));
            }

            try
            {
                _declared = ReadDeclaredNames(path);
            }
            catch (Exception exc)
            {
                throw new GraphSchemaUnavailableException(
                    $"The Graph connection schema '{path}' could not be read, so the connector "
                    + "cannot know which properties it may publish. This is a configuration fault "
                    + $"affecting every record, not a bad source row: {exc.Message}",
                    exc);
            }
            _resolvedPath = path;
        }
    }

    private static string? ResolvePath()
    {
        foreach (var candidate in CandidatePaths())
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    private static IEnumerable<string?> CandidatePaths()
    {
        yield return _overridePath;
        yield return Path.Combine(Directory.GetCurrentDirectory(), "config", FileName);
        yield return Path.Combine(AppContext.BaseDirectory, "config", FileName);
    }
}
