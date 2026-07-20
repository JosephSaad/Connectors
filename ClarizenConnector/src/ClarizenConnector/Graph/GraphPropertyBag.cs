// Graph/GraphPropertyBag.cs
// -------------------------
// The property map of an ExternalItem. Deliberately NOT a Dictionary: every
// write goes through GraphPropertyRegistry, so stamping a property the Graph
// connection schema does not declare is impossible to express — the mistake
// fails at the line that made it, naming the property, whether the name arrived
// as a const, a bare string literal, or a value computed at runtime.
//
// See GraphPropertyRegistry for why this replaced a curated-symbol-list guard.

using System.Collections;

namespace ClarizenConnector.Graph;

public sealed class GraphPropertyBag : IEnumerable<KeyValuePair<string, object?>>
{
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);

    /// <summary>Read a property; writing one asserts it is declared in the Graph
    /// connection schema first.</summary>
    public object? this[string name]
    {
        get => _values[name];
        set => Set(name, value);
    }

    /// <summary>The one write path. Throws
    /// <see cref="UndeclaredGraphPropertyException"/> for a name
    /// config/graph-schema.json does not declare.</summary>
    public void Set(string name, object? value)
    {
        GraphPropertyRegistry.AssertDeclared(name);
        _values[name] = value;
    }

    public int Count => _values.Count;

    public IEnumerable<string> Names => _values.Keys;

    public bool ContainsKey(string name) => _values.ContainsKey(name);

    public bool TryGetValue(string name, out object? value) => _values.TryGetValue(name, out value);

    public object? GetValueOrDefault(string name) => _values.GetValueOrDefault(name);

    public bool Remove(string name) => _values.Remove(name);

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => _values.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
