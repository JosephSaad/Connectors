// Item/AlwaysEmittedProperties.cs
// -------------------------------
// THE registry of Graph property names this connector writes onto EVERY item by
// itself, independently of selectedFields — the single symbol every collision
// check reads.
//
// Why this file exists rather than a list inside the validator: the validator
// used to source its list from ItemConverter.StandardPropertyNames, correctly,
// and was still wrong — because ItemConverter is not the only emitter.
// SensitivityClassifier.Classify runs AFTER ItemConverter.Convert (Graph/
// Ingest.cs) and stamps two more properties, which no check knew about. A
// selectedFields entry mapped onto one of them was accepted by preflight and
// then destroyed at crawl time, and a columnPolicies entry named after one made
// preflight report a restriction the item did not deliver.
//
// The rule for anyone adding a property that is written on every item:
//   1. declare its name as a constant on the class that writes it,
//   2. add that constant to that class's own contributed list, and
//   3. add that list to Contributors below.
// ConfigPreflightClosureTests executes the two KNOWN emitters (ItemConverter.Convert
// and SensitivityClassifier.Classify) end-to-end and fails if either emits a property
// missing from Names, so steps 1-2 cannot be forgotten silently.
//
// Step 3 is NOT pinned. Nothing enumerates emitter classes: the closure tests call
// Convert and Classify by name, so a THIRD emitter invoked from the pipeline would be
// caught by no test. If you add one, add it to Contributors AND to those tests.

namespace HadoopConnector.Item;

/// <summary>
/// Every Graph property name emitted on every item outside <c>selectedFields</c>,
/// contributed by the emitters themselves.
/// </summary>
public static class AlwaysEmittedProperties
{
    /// <summary>
    /// The per-emitter lists, each owned by the class that does the writing.
    /// One entry per emitter — never a copy of the names.
    /// </summary>
    public static readonly IReadOnlyList<IReadOnlyList<string>> Contributors = new[]
    {
        ItemConverter.StandardPropertyNames,
        SensitivityClassifier.AlwaysEmittedPropertyNames,
    };

    /// <summary>
    /// The flattened set. selectedFields cannot safely name any of these on
    /// EITHER side: a value mapping ONTO one is overwritten (or overwrites the
    /// emitter's value, depending on which runs last) and a columnPolicies entry
    /// named after one reports a restriction that is not delivered, because a
    /// property of exactly that name goes on being emitted and indexed.
    /// <para>
    /// Emission that is conditional (IconUrl and DataAsOf are written only when
    /// non-empty; the classifier's two only when CLASSIFICATION=true) still
    /// belongs here: whether they are populated is a property of the record or
    /// the deployment, so a config colliding with them is broken in some
    /// deployments and not others, which is worse than being broken in all.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<string> Names =
        Contributors.SelectMany(names => names).ToArray();
}
