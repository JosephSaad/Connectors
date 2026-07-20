// ConfigNullNormalizerTests.cs
// ----------------------------
// The normaliser is the ONE mechanism that makes the config model null-safe, so
// each of its branches is exercised on its own rather than only through
// SchemaConfig.Load. Two reasons that matters:
//
//   * Load-level tests cannot distinguish "the null was repaired" from "the null
//     was rejected by a later validation rule that treats empty the same way" —
//     both end in the same message. Neutering the repair therefore leaves the
//     load-level tests green while the mechanism is broken for any future member
//     whose empty value IS legal.
//   * Branches that no CURRENT member of SchemaConfig can reach (a dictionary
//     whose values are not strings, a list whose elements have no empty form)
//     still have to behave, because the model grows. They are driven here
//     directly against a purpose-built root object.
//
// These tests exercise production code — ConfigNullNormalizer itself. Nothing
// here re-implements it or compares it to a model of what it should do.

using System.Text.Json.Serialization;
using HadoopConnector.Config;

namespace HadoopConnector.Tests;

public class ConfigNullNormalizerTests
{
    /// <summary>A root object covering every member SHAPE the walker handles.
    /// Only the ROOT's own members are involved, so this stands in for "some
    /// config class", not for SchemaConfig specifically.</summary>
    private sealed class Shapes
    {
        public string Text { get; set; } = "seed";
        public List<string> Words { get; set; } = new() { "seed" };
        public Dictionary<string, string> Map { get; set; } = new();
        public Dictionary<string, int[]> NoEmptyFormValues { get; set; } = new();
        public List<int[]> NoEmptyFormElements { get; set; } = new();
        public int Number { get; set; } = 7;

        [JsonIgnore]
        public string? Ignored { get; set; } = null;

        public string ReadOnly => Text;
    }

    private static void Normalize(object model) => ConfigNullNormalizer.Normalize(model, "test.json");

    [Fact]
    public void ANullStringMember_BecomesEmpty()
    {
        var shapes = new Shapes { Text = null! };

        Normalize(shapes);

        Assert.Equal(string.Empty, shapes.Text);
    }

    [Fact]
    public void ANullCollectionMember_BecomesAnEmptyCollectionOfTheRightType()
    {
        var shapes = new Shapes { Words = null!, Map = null! };

        Normalize(shapes);

        Assert.Empty(shapes.Words);
        Assert.Empty(shapes.Map);
    }

    [Fact]
    public void ANullDictionaryVALUE_BecomesEmptyRatherThanStayingNull()
    {
        var shapes = new Shapes { Map = new Dictionary<string, string> { ["a"] = null!, ["b"] = "keep" } };

        Normalize(shapes);

        Assert.Equal(string.Empty, shapes.Map["a"]);
        Assert.Equal("keep", shapes.Map["b"]);
    }

    [Fact]
    public void ANullStringLISTElement_BecomesEmptyRatherThanStayingNull()
    {
        var shapes = new Shapes { Words = new List<string> { "a", null!, "c" } };

        Normalize(shapes);

        Assert.Equal(new[] { "a", string.Empty, "c" }, shapes.Words);
    }

    // No empty form ⇒ a named load error, never a null left in place for some
    // later reader to dereference.
    [Fact]
    public void ANullDictionaryValueWithNoEmptyForm_IsANamedLoadError()
    {
        var shapes = new Shapes
        {
            NoEmptyFormValues = new Dictionary<string, int[]> { ["rows"] = null! },
        };

        var exc = Assert.Throws<InvalidDataException>(() => Normalize(shapes));

        Assert.Contains("noEmptyFormValues.rows", exc.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANullListElementWithNoEmptyForm_IsANamedLoadError()
    {
        var shapes = new Shapes { NoEmptyFormElements = new List<int[]> { new[] { 1 }, null! } };

        var exc = Assert.Throws<InvalidDataException>(() => Normalize(shapes));

        Assert.Contains("noEmptyFormElements[1]", exc.Message, StringComparison.Ordinal);
    }

    // Value types and read-only members are not deserialization targets and must
    // be left exactly alone — a walker that touched them would be rewriting
    // config nobody wrote.
    [Fact]
    public void ValueTypesAndComputedMembers_AreLeftAlone()
    {
        var shapes = new Shapes { Number = 42, Text = "keep" };

        Normalize(shapes);

        Assert.Equal(42, shapes.Number);
        Assert.Equal("keep", shapes.Text);
        Assert.Equal("keep", shapes.ReadOnly);
        Assert.Null(shapes.Ignored);   // [JsonIgnore] ⇒ not a bound member
    }

    [Fact]
    public void NormalizingTwice_ChangesNothingTheSecondTime()
    {
        var shapes = new Shapes { Text = null!, Words = null!, Map = null! };

        Normalize(shapes);
        var words = shapes.Words;
        var map = shapes.Map;
        Normalize(shapes);

        Assert.Same(words, shapes.Words);
        Assert.Same(map, shapes.Map);
        Assert.Equal(string.Empty, shapes.Text);
    }

    // The real model, nested: a null on the CHILD object must be repaired too,
    // which only happens if the walker recurses through the list.
    [Fact]
    public void TheRealModel_IsWalkedRecursivelyIntoItsChildObjects()
    {
        var config = new SchemaConfig
        {
            ObjectList = new List<ObjectConfig>
            {
                new()
                {
                    ObjectName = "Contact",
                    DisplayName = null!,
                    AclMode = null!,
                    SelectedFields = null!,
                    ColumnPolicies = null!,
                    OwnerField = null!,
                    SourcePath = null!,
                },
            },
        };

        Normalize(config);
        var obj = config.ObjectList[0];

        Assert.Equal(string.Empty, obj.DisplayName);
        Assert.Equal(string.Empty, obj.AclMode);
        Assert.Equal(string.Empty, obj.OwnerField);
        Assert.Equal(string.Empty, obj.SourcePath);
        Assert.NotNull(obj.SelectedFields);
        Assert.NotNull(obj.ColumnPolicies);
    }
}
