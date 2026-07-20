// Config/ConfigNullNormalizer.cs
// ------------------------------
// ONE mechanism that makes the whole deserialized config model null-safe by
// construction, replacing per-property guards.
//
// THE MECHANISM BEING DEFENDED AGAINST
// ------------------------------------
// System.Text.Json does not treat a C# property initializer as a default. Given
//
//     public List<ObjectConfig> ObjectList { get; set; } = new();
//
// the JSON `{"objectList": null}` does NOT leave the initializer in place — the
// deserializer calls the setter with null and OVERWRITES it. The same is true of
// every string property (`= string.Empty`), every dictionary, and every
// dictionary VALUE. So EVERY reference-typed member of the config model is a
// potential null the moment an operator writes `null` in schema.json, even
// though the C# type says non-nullable and the initializer says otherwise.
//
// Guarding the individual properties that happened to be reported does not close
// this: the hole is per-property and the model grows. A property added next year
// with `= new()` is born with the same defect. So the normalisation is done by
// WALKING the model reflectively — it covers every present property and every
// future one automatically, and there is no list of property names anywhere in
// this file to fall out of date.
//
// THE SEMANTICS, decided once for the whole model
// -----------------------------------------------
//   A JSON null is read as that member's EMPTY value:
//       string      -> ""            (never null)
//       collection  -> empty list / empty dictionary   (never null)
//       dict value  -> ""            (never null)
//   Whether "empty" is LEGAL is then decided by SchemaConfig.Validate, in exactly
//   the same way, with exactly the same message, as for a config that wrote ""
//   or omitted the key. Nulls therefore never crash and never take a private
//   path through validation.
//
// A consequence worth stating plainly: `null` is equivalent to ABSENT for every
// member whose default is already the empty value (which is all of them but one),
// and for `aclMode` — the single member with a non-empty initializer
// ("ownerOnly") — writing `null` is NOT the same as omitting the key: it yields
// the empty aclMode, which Validate rejects with 'invalid aclMode'. That is the
// fail-closed direction and is deliberate: an explicit null on the property that
// decides who can see the data should stop the load, not silently select a
// default. See docs/CONFIG_NULL_SEMANTICS.md.
//
// A null ELEMENT of an object list (`"objectList": [null]`) has no "empty value"
// that means anything, so it is a load error naming the offending JSON path.

using System.Collections;
using System.Reflection;
using System.Text.Json.Serialization;

namespace HadoopConnector.Config;

internal static class ConfigNullNormalizer
{
    /// <summary>
    /// Replace every null reference reachable from <paramref name="model"/> with
    /// its type's empty value, in place. Idempotent. Throws
    /// <see cref="InvalidDataException"/> — never a NullReferenceException — for
    /// the one shape that has no empty value: a null element of an object list.
    /// </summary>
    /// <param name="model">Root of the deserialized config model.</param>
    /// <param name="path">Config file path, for error messages.</param>
    internal static void Normalize(object model, string path) =>
        Walk(model, path, "$", new HashSet<object>(ReferenceEqualityComparer.Instance));

    /// <summary>Types this walker recurses INTO: the config model's own classes.
    /// Anything from another assembly (string, primitives, framework types) is a
    /// leaf, so the walk can never wander into unrelated object graphs.</summary>
    private static bool IsModelType(Type type) =>
        type.IsClass && type != typeof(string) && type.Assembly == typeof(ConfigNullNormalizer).Assembly;

    private static void Walk(object node, string path, string jsonPath, HashSet<object> seen)
    {
        if (!seen.Add(node))
            return;

        foreach (var property in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0 || !property.CanRead)
                continue;
            // Computed/derived members are not deserialization targets.
            if (property.IsDefined(typeof(JsonIgnoreAttribute), inherit: true))
                continue;

            var memberPath = $"{jsonPath}.{JsonNameOf(property)}";
            var value = property.GetValue(node);

            if (value is null)
            {
                // Only a writable member can be repaired. A read-only reference
                // member cannot have been set by the deserializer either, so
                // there is nothing to repair.
                if (property.CanWrite && EmptyValueFor(property.PropertyType) is { } empty)
                    property.SetValue(node, empty);
                continue;
            }

            NormalizeValue(value, path, memberPath, seen);
        }
    }

    private static void NormalizeValue(object value, string path, string jsonPath, HashSet<object> seen)
    {
        switch (value)
        {
            case string:
                return;

            case IDictionary dictionary:
                NormalizeDictionary(dictionary, path, jsonPath, seen);
                return;

            case IList list:
                NormalizeList(list, path, jsonPath, seen);
                return;

            default:
                if (IsModelType(value.GetType()))
                    Walk(value, path, jsonPath, seen);
                return;
        }
    }

    private static void NormalizeDictionary(
        IDictionary dictionary, string path, string jsonPath, HashSet<object> seen)
    {
        var valueType = ValueTypeOf(dictionary.GetType());
        // Snapshot the keys: entries are rewritten during the sweep.
        var keys = new List<object>(dictionary.Count);
        foreach (var key in dictionary.Keys)
            keys.Add(key);

        foreach (var key in keys)
        {
            var entryPath = $"{jsonPath}.{key}";
            var entry = dictionary[key];
            if (entry is null)
            {
                if (EmptyValueFor(valueType) is { } empty)
                {
                    dictionary[key] = empty;
                    continue;
                }
                throw NullElement(path, entryPath);
            }
            NormalizeValue(entry, path, entryPath, seen);
        }
    }

    private static void NormalizeList(IList list, string path, string jsonPath, HashSet<object> seen)
    {
        var elementType = ElementTypeOf(list.GetType());
        for (var i = 0; i < list.Count; i++)
        {
            var entryPath = $"{jsonPath}[{i}]";
            var entry = list[i];
            if (entry is null)
            {
                // A null string in a string list is "the empty string"; a null
                // OBJECT has no empty value that means anything (an all-defaults
                // object is a guess, not the operator's intent), so it is a load
                // error that names exactly where it is.
                if (elementType == typeof(string))
                {
                    list[i] = string.Empty;
                    continue;
                }
                throw NullElement(path, entryPath);
            }
            NormalizeValue(entry, path, entryPath, seen);
        }
    }

    private static InvalidDataException NullElement(string path, string jsonPath) =>
        new($"Schema config '{path}': {jsonPath} is null. Everywhere else in this file a JSON "
            + "null is read as that key's EMPTY value, but a null entry in a list of objects has "
            + "no meaningful empty form — it would be an object with no objectName, no "
            + "selectedFields and no aclMode. Remove the entry, or fill it in.");

    /// <summary>The empty value standing in for a null of this type, or null when
    /// the type has none (a value type, or a class with no usable empty form).</summary>
    private static object? EmptyValueFor(Type type)
    {
        if (type == typeof(string))
            return string.Empty;
        if (type.IsValueType || type.IsAbstract || type.IsInterface)
            return null;
        // Collections and any other model class with a parameterless constructor:
        // `new()` IS the empty value, and it is what the property initializer the
        // deserializer overwrote would have produced.
        if (typeof(IEnumerable).IsAssignableFrom(type) && type.GetConstructor(Type.EmptyTypes) is not null)
            return Activator.CreateInstance(type);
        return null;
    }

    private static Type ElementTypeOf(Type listType) =>
        ClosedGenericArgument(listType, typeof(IList<>), index: 0) ?? typeof(object);

    private static Type ValueTypeOf(Type dictionaryType) =>
        ClosedGenericArgument(dictionaryType, typeof(IDictionary<,>), index: 1) ?? typeof(object);

    private static Type? ClosedGenericArgument(Type type, Type openInterface, int index)
    {
        foreach (var candidate in type.GetInterfaces())
        {
            if (candidate.IsGenericType && candidate.GetGenericTypeDefinition() == openInterface)
                return candidate.GetGenericArguments()[index];
        }
        return null;
    }

    /// <summary>The JSON key this property binds to, matching what the
    /// deserializer used, so error messages name what the operator wrote.</summary>
    private static string JsonNameOf(PropertyInfo property) =>
        property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
        ?? char.ToLowerInvariant(property.Name[0]) + property.Name[1..];
}
