// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// FlsLeakRegressionTests.cs  (WP-SF-3)
// ------------------------------------
// Regression tests for four PROVEN over-sharing holes left open by WP-SF-2.
// Each test below is the probe that demonstrated the leak, kept verbatim so the
// hole cannot silently reopen:
//
//   1. NESTED SUB-FIELD, GRAPH-PROPERTY SPELLING.  The content loop's nested
//      "Parent.Child" gate passed null for propertyName while its property-loop
//      counterpart passed the real mapped name. A drop expressed in Graph-property
//      spelling therefore gated the property and LEAKED the value into the body.
//
//   2. __System.* USER-ID COLUMNS.  props[__System.User.CreatedBy.Id] and its
//      LastModifiedById twin were written with no FLS gate at all. Dead on the
//      ingest path (GraphSchemaProperties never carries those names) but LIVE on
//      the direct-converter path, where BuildSchemaProperties unions them in.
//
//   3. COMPOUND FIELDS — CLOSED BY REMOVAL, NOT BY A FIX.  The attempted fix
//      inferred a compound's components FROM FIELD NAMES and propagated drops
//      between them. That is unsound: on a Person-Accounts org it dropped
//      Account.Name for EVERY account, business accounts included. It was removed,
//      and the tests below now pin the REMOVAL plus the accepted residual (a hidden
//      component can still reach the index via an indexed compound). See docs/FLS.md.
//
//   4. CROSS-OBJECT RELATIONSHIP FIELDS.  Dotted selectedFields keys ("Contact.Phone"
//      on Case) were fed to FlsPolicy.ComputeDrops as candidates but matched against
//      GovernedFields, which only ever holds BARE field names from the OWN object —
//      so they were never FLS-evaluated at all.
//
// Every drop assertion checks the WHOLE item: properties AND content body.

using System.Text.Json.Nodes;
using SalesforceCopilotConnector.AclEngine;
using SalesforceCopilotConnector.Item;

namespace SalesforceCopilotConnector.Tests.TestAclEngine;

/// <summary>
/// HOLE 1 — a nested relationship sub-field drop expressed in GRAPH-PROPERTY
/// spelling gated the property path but leaked into the searchable content body.
/// </summary>
public class FlsNestedSubFieldPropertySpellingTests
{
    private const string Secret = "250000-OWNER-COMP-SECRET";

    /// <summary>
    /// selectedFields maps the DOTTED Salesforce path to a Graph property, so
    /// "Owner.Compensation__c" travels the nested property path and the drop can
    /// legitimately be spelled either way. Only the SF spelling worked.
    /// </summary>
    private static SalesforceObjectHandler Handler()
    {
        var config = new JsonObject
        {
            ["objectName"] = "Account",
            ["selectedFields"] = new JsonObject
            {
                ["Name"] = "Title",
                ["Owner.Compensation__c"] = "OwnerComp",
            },
        };
        return new SalesforceObjectHandler(config)
        {
            GraphSchemaProperties = new HashSet<string> { "ObjectName", "url", "Title", "OwnerComp" },
        };
    }

    private static JsonObject ConvertOne(SalesforceObjectHandler handler)
    {
        var record = new JsonObject
        {
            ["attributes"] = new JsonObject { ["type"] = "Account" },
            ["Id"] = "001NESTPROP",
            ["Name"] = "Acme Corp",
            ["Owner"] = new JsonObject
            {
                ["Name"] = "Owner Name",
                ["Compensation__c"] = Secret,
            },
        };
        var items = handler.ConstructIngestionItems(
            new JsonObject { ["records"] = new JsonArray { record } },
            FlsFixtures.InstanceUrl,
            new HashSet<string> { "ObjectName", "url", "Title", "OwnerComp" });
        return Assert.Single(items);
    }

    [Fact]
    public void GraphPropertySpelledDropGatesTheNestedContentBodyToo()
    {
        // THE PROBE. Pre-fix: absent from properties, PRESENT in content.parsedData.
        var handler = Handler();
        handler.ApplyFlsDrops(new[] { "OwnerComp" });

        var item = ConvertOne(handler);

        FlsFixtures.AssertDroppedFromBothLoops(item, Secret);
        Assert.DoesNotContain("Owner.Compensation__c", FlsFixtures.ContentBody(item));
    }

    [Fact]
    public void SalesforceSpelledDropStillGatesBothLoops()
    {
        // The spelling that already worked must keep working.
        var handler = Handler();
        handler.ApplyFlsDrops(new[] { "Owner.Compensation__c" });

        FlsFixtures.AssertDroppedFromBothLoops(ConvertOne(handler), Secret);
    }

    [Fact]
    public void UnrestrictedNestedSubFieldsSurviveInTheContentBody()
    {
        // The gate must not become a blanket drop of every nested sub-field.
        var handler = Handler();
        handler.ApplyFlsDrops(new[] { "OwnerComp" });

        Assert.Contains("Owner.Name: Owner Name", FlsFixtures.ContentBody(ConvertOne(handler)));
    }
}

/// <summary>
/// HOLE 2 — the two <c>__System.User.*.Id</c> properties were emitted with no FLS
/// gate. <see cref="Converter.BuildSchemaProperties"/> unions those names in, and
/// that set is the fallback whenever <c>GraphSchemaProperties</c> is null, so the
/// direct-converter path emitted them unconditionally.
/// </summary>
public class FlsSystemUserIdColumnTests
{
    private const string CreatorId = "005CREATOR-SECRET";
    private const string ModifierId = "005MODIFIER-SECRET";

    /// <summary>
    /// A handler with GraphSchemaProperties left NULL, exactly like the direct
    /// SalesforceConverter path, so schemaProperties (which BuildSchemaProperties
    /// populates with the __System.* names) is what gates emission.
    /// </summary>
    private static SalesforceObjectHandler Handler()
    {
        var config = new JsonObject
        {
            ["objectName"] = "Account",
            ["selectedFields"] = new JsonObject { ["Name"] = "Title" },
        };
        return new SalesforceObjectHandler(config);
    }

    private static HashSet<string> SchemaProperties()
    {
        var handlers = new Dictionary<string, SalesforceObjectHandler> { ["Account"] = Handler() };
        var props = Converter.BuildSchemaProperties(handlers);
        // The union really does include the two system columns — that is the
        // precondition that makes this path live.
        Assert.Contains(Converter.SystemCreatedByUserId, props);
        Assert.Contains(Converter.SystemModifiedByUserId, props);
        return props;
    }

    private static JsonObject ConvertOne(SalesforceObjectHandler handler)
    {
        var record = new JsonObject
        {
            ["attributes"] = new JsonObject { ["type"] = "Account" },
            ["Id"] = "001SYSCOL",
            ["Name"] = "Acme Corp",
            ["CreatedById"] = CreatorId,
            ["LastModifiedById"] = ModifierId,
        };
        var items = handler.ConstructIngestionItems(
            new JsonObject { ["records"] = new JsonArray { record } },
            FlsFixtures.InstanceUrl,
            SchemaProperties());
        return Assert.Single(items);
    }

    [Fact]
    public void CreatedByIdDropSuppressesTheSystemCreatedByProperty()
    {
        // THE PROBE. Pre-fix: __System.User.CreatedBy.Id carried the id verbatim.
        var handler = Handler();
        handler.ApplyFlsDrops(new[] { "CreatedById" });

        var item = ConvertOne(handler);

        Assert.DoesNotContain(CreatorId, FlsFixtures.PropertiesJson(item));
        FlsFixtures.AssertDroppedFromBothLoops(item, CreatorId);
    }

    [Fact]
    public void LastModifiedByIdDropSuppressesTheSystemModifiedByProperty()
    {
        var handler = Handler();
        handler.ApplyFlsDrops(new[] { "LastModifiedById" });

        FlsFixtures.AssertDroppedFromBothLoops(ConvertOne(handler), ModifierId);
    }

    /// <summary>
    /// THE PROBE for the half-true claim. The docstring on
    /// <c>IsSystemUserColumnRestricted</c> asserted a drop "can legitimately be
    /// spelled three ways … All three must gate." It held in two directions only.
    /// Spelling the drop <c>__System.User.CreatedBy.Id</c> suppressed the system
    /// column and left <c>CreatedByUrl</c> publishing the identical user Id:
    /// <c>CreatedByUrl="https://…/ZZUSER9"</c>. The alias closure makes the three
    /// spellings genuinely interchangeable.
    /// </summary>
    [Theory]
    [InlineData("CreatedById")]
    [InlineData("CreatedByUrl")]
    [InlineData(Converter.SystemCreatedByUserId)]
    public void EverySpellingOfACreatedByDropGatesEveryPropertyItFeeds(string spelling)
    {
        var handler = Handler();
        handler.ApplyFlsDrops(new[] { spelling });

        var item = ConvertOne(handler);
        var props = (JsonObject)item["properties"]!;

        Assert.False(props.ContainsKey(Converter.SystemCreatedByUserId));
        Assert.False(props.ContainsKey("CreatedByUrl"));
        FlsFixtures.AssertDroppedFromBothLoops(item, CreatorId);
        // The twin column is untouched — the closure is per field, not a blanket drop.
        Assert.Equal(ModifierId, props[Converter.SystemModifiedByUserId]!.GetValue<string>());
    }

    /// <summary>The same invariant on the LastModifiedById twin — proven, not assumed.</summary>
    [Theory]
    [InlineData("LastModifiedById")]
    [InlineData("LastModifiedByUrl")]
    [InlineData(Converter.SystemModifiedByUserId)]
    public void EverySpellingOfALastModifiedByDropGatesEveryPropertyItFeeds(string spelling)
    {
        var handler = Handler();
        handler.ApplyFlsDrops(new[] { spelling });

        var item = ConvertOne(handler);
        var props = (JsonObject)item["properties"]!;

        Assert.False(props.ContainsKey(Converter.SystemModifiedByUserId));
        Assert.False(props.ContainsKey("LastModifiedByUrl"));
        FlsFixtures.AssertDroppedFromBothLoops(item, ModifierId);
        Assert.Equal(CreatorId, props[Converter.SystemCreatedByUserId]!.GetValue<string>());
    }

    /// <summary>
    /// The closure is not limited to the two user-Id columns: it is computed from the
    /// declared maps, so it holds for EVERY field with more than one spelling. OwnerId
    /// feeds OwnerUrl and was never individually reported.
    /// </summary>
    [Theory]
    [InlineData("OwnerId")]
    [InlineData("OwnerUrl")]
    public void EverySpellingOfAnOwnerDropGatesTheOwnerUrl(string spelling)
    {
        const string OwnerId = "005OWNER-SECRET";
        var handler = Handler();
        handler.ApplyFlsDrops(new[] { spelling });

        var record = new JsonObject
        {
            ["attributes"] = new JsonObject { ["type"] = "Account" },
            ["Id"] = "001OWNER",
            ["Name"] = "Acme Corp",
            ["OwnerId"] = OwnerId,
        };
        var item = Assert.Single(handler.ConstructIngestionItems(
            new JsonObject { ["records"] = new JsonArray { record } },
            FlsFixtures.InstanceUrl,
            SchemaProperties()));

        FlsFixtures.AssertDroppedFromBothLoops(item, OwnerId);
    }

    [Fact]
    public void MetadataPropertySpellingGatesTheSystemColumnToo()
    {
        // The safe direction: a drop naming the ordinary metadata property
        // (CreatedByUrl) — or the Salesforce field — must ALSO suppress the
        // __System.* twin, since both carry the same user id.
        var handler = Handler();
        handler.ApplyFlsDrops(new[] { "CreatedByUrl" });

        var item = ConvertOne(handler);

        FlsFixtures.AssertDroppedFromBothLoops(item, CreatorId);
    }

    [Fact]
    public void UnrestrictedSystemColumnsAreStillEmitted()
    {
        var handler = Handler();
        handler.ApplyFlsDrops(new[] { "SomeUnrelatedField__c" });

        var props = (JsonObject)ConvertOne(handler)["properties"]!;

        Assert.Equal(CreatorId, props[Converter.SystemCreatedByUserId]!.GetValue<string>());
        Assert.Equal(ModifierId, props[Converter.SystemModifiedByUserId]!.GetValue<string>());
    }
}

/// <summary>
/// HOLE 3 — COMPOUND FIELDS: CLOSED BY NOT QUERYING THEM.
///
/// <para>THE ORIGINAL DEFECT, which three rounds of instance-fixes did not close: a
/// Salesforce COMPOUND (BillingAddress, MailingAddress, Lead.Address, …) carries no
/// <c>FieldPermissions</c> rows of its own — FLS lives on the COMPONENTS. Feeding
/// <c>handler.SelectedFields.Keys</c> to <c>FlsPolicy.ComputeDrops</c> therefore
/// offered the policy only the compound, so no component was EVER EVALUATED, and the
/// component values landed verbatim in the searchable content body. Verbatim from the
/// probe against the shipped config, every address component readable by nobody:
/// <c>DROPS=[Parent.Name]</c> — zero address drops — and
/// <c>'BillingAddress.street: ZZSENTINEL9 Secret Lane, BillingAddress.city: ZZCITY9'</c>
/// in the body.</para>
///
/// <para>A previous attempt propagated drops between a compound and components
/// INFERRED FROM FIELD NAMES. That was unsound and was removed: on a Person-Accounts
/// org Salutation/FirstName/LastName carry real rows, so restricting any one of them
/// dropped Account.Name for EVERY account, business accounts included.</para>
///
/// <para>THE FIX IS STRUCTURAL AND HAS NO INFERENCE IN IT. Compounds are no longer
/// selected at all. Components are selected individually — each one therefore carries
/// its own FieldPermissions evidence and is gated by LITERAL name — and the displayed
/// address is assembled from them via the config's <c>addressFields</c> map. Any value
/// that still arrives compound-shaped fails CLOSED: it is indexed by no route, in
/// neither loop. That is what makes this cover shapes nobody enumerated — custom
/// address compounds, geolocation compounds, Person-Account address compounds, and any
/// object added to the config later.</para>
/// </summary>
public class FlsCompoundNotIndexableTests
{
    private const string AccountName = "ACME-BUSINESS-ACCOUNT-NAME";
    private const string SalutationSecret = "MS-SALUTATION-SENTINEL";

    /// <summary>One distinguishable sentinel per address slot, so a sweep can tell exactly which leaked.</summary>
    private static readonly (string Slot, string Field, string Value)[] Components =
    {
        ("street", "BillingStreet", "ZZSTREET9 Secret Lane"),
        ("city", "BillingCity", "ZZCITY9"),
        ("state", "BillingState", "ZZSTATE9"),
        ("postalCode", "BillingPostalCode", "ZZPOST9"),
        ("country", "BillingCountry", "ZZCOUNTRY9"),
    };

    /// <summary>
    /// Every test runs through BOTH assembly routings. A value travels the PROPERTY
    /// loop when its target Graph property is in the schema and the CONTENT loop when
    /// it is not, so flipping the schema flips the route for the whole record. A gate
    /// applied to one loop only moves the leak to the other, which is the failure this
    /// whole file exists to prevent.
    /// </summary>
    public static TheoryData<bool> BothLoops => new() { true, false };

    private static HashSet<string> GraphProps(bool viaPropertyLoop) =>
        viaPropertyLoop
            ? new HashSet<string> { "ObjectName", "url", "Title", "BillingAddressText" }
            : new HashSet<string> { "ObjectName", "url", "Title" };

    /// <summary>
    /// The shape config/schema.json now ships: components in selectedFields, the
    /// compound absent, and an addressFields group declaring the reassembly.
    /// </summary>
    private static SalesforceObjectHandler Handler(
        bool viaPropertyLoop,
        string[]? manualFlsFields = null)
    {
        var selected = new JsonObject
        {
            ["Name"] = "Title",
            ["Salutation"] = "_sf_Salutation",
        };
        var components = new JsonObject();
        foreach (var (slot, field, _) in Components)
        {
            selected[field] = "_sf_" + field;
            components[slot] = field;
        }

        var config = new JsonObject
        {
            ["objectName"] = "Account",
            ["selectedFields"] = selected,
            ["addressFields"] = new JsonObject
            {
                ["BillingAddress"] = new JsonObject
                {
                    ["property"] = "BillingAddressText",
                    ["components"] = components,
                },
            },
        };
        if (manualFlsFields is not null)
        {
            var arr = new JsonArray();
            foreach (var f in manualFlsFields)
                arr.Add(f);
            config["flsFields"] = arr;
        }

        return new SalesforceObjectHandler(config)
        {
            GraphSchemaProperties = GraphProps(viaPropertyLoop),
        };
    }

    private static JsonObject Record()
    {
        var record = new JsonObject
        {
            ["attributes"] = new JsonObject { ["type"] = "Account" },
            ["Id"] = "001COMPOUND",
            ["Name"] = AccountName,
            ["Salutation"] = SalutationSecret,
        };
        foreach (var (_, field, value) in Components)
        {
            record[field] = value;
        }
        return record;
    }

    private static JsonObject ConvertOne(
        SalesforceObjectHandler handler, bool viaPropertyLoop, JsonObject? record = null)
    {
        var items = handler.ConstructIngestionItems(
            new JsonObject { ["records"] = new JsonArray { record ?? Record() } },
            FlsFixtures.InstanceUrl,
            GraphProps(viaPropertyLoop));
        return Assert.Single(items);
    }

    /// <summary>The assembled address text, wherever this routing put it.</summary>
    private static string AssembledAddress(JsonObject item, bool viaPropertyLoop)
    {
        if (viaPropertyLoop)
        {
            return ((JsonObject)item["properties"]!)["BillingAddressText"]?.GetValue<string>() ?? "";
        }
        var body = FlsFixtures.ContentBody(item);
        var marker = "BillingAddress: ";
        var at = body.IndexOf(marker, StringComparison.Ordinal);
        return at < 0 ? "" : body[(at + marker.Length)..];
    }

    // ── THE OUTPUT SHAPE IS PRESERVED ────────────────────────────────────────

    [Theory]
    [MemberData(nameof(BothLoops))]
    public void WithNothingRestrictedTheAssembledTextMatchesTheOldCompoundSerialisation(bool viaPropertyLoop)
    {
        // The exact string the removed compound path produced for this address, so a
        // regression in assembly ORDER or PUNCTUATION is caught, not just a leak.
        const string Expected = "ZZSTREET9 Secret Lane, ZZCITY9, ZZSTATE9 - ZZPOST9, ZZCOUNTRY9";

        var item = ConvertOne(Handler(viaPropertyLoop), viaPropertyLoop);

        Assert.Equal(Expected, AssembledAddress(item, viaPropertyLoop));
    }

    // ── THE CLASS, SWEPT AT ITS BOUNDARIES ───────────────────────────────────

    /// <summary>
    /// All 32 subsets of the five address components, both routings — 64 cases. Not
    /// hand-picked: the restriction sets are GENERATED, so the empty set, every
    /// singleton, every partial and the full set are all covered, including the
    /// boundaries (nothing restricted / everything restricted) that hand-picked cases
    /// historically missed.
    /// </summary>
    public static TheoryData<bool, int> EveryRestrictionSubset()
    {
        var data = new TheoryData<bool, int>();
        foreach (var viaPropertyLoop in new[] { true, false })
        {
            for (var mask = 0; mask < 1 << 5; mask++)
            {
                data.Add(viaPropertyLoop, mask);
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(EveryRestrictionSubset))]
    public void EveryRestrictedComponentVanishesAndEveryPermittedOneSurvives(bool viaPropertyLoop, int mask)
    {
        var restricted = Components.Where((_, i) => (mask & (1 << i)) != 0).ToArray();
        var permitted = Components.Where((_, i) => (mask & (1 << i)) == 0).ToArray();

        var handler = Handler(viaPropertyLoop);
        handler.ApplyFlsDrops(restricted.Select(c => c.Field));

        var item = ConvertOne(handler, viaPropertyLoop);
        var whole = item.ToJsonString();

        foreach (var (_, _, value) in restricted)
        {
            // Nowhere in the item: not the property, not the body, not the assembly.
            FlsFixtures.AssertDroppedFromBothLoops(item, value);
        }
        foreach (var (_, _, value) in permitted)
        {
            Assert.Contains(value, whole, StringComparison.Ordinal);
        }
        // Never collateral damage: the account name is not a component of anything.
        Assert.Contains(AccountName, whole, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(BothLoops))]
    public void RestrictingTheStreetLeavesTheRestOfTheAddressAssembled(bool viaPropertyLoop)
    {
        // THE PROBE, inverted. Pre-fix the street reached the body verbatim because
        // the component was never evaluated at all.
        var handler = Handler(viaPropertyLoop);
        handler.ApplyFlsDrops(new[] { "BillingStreet" });

        var item = ConvertOne(handler, viaPropertyLoop);

        FlsFixtures.AssertDroppedFromBothLoops(item, "ZZSTREET9 Secret Lane");
        Assert.Contains("ZZCITY9", AssembledAddress(item, viaPropertyLoop), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(BothLoops))]
    public void AnAddressWithEveryComponentRestrictedDisappearsEntirely(bool viaPropertyLoop)
    {
        var handler = Handler(viaPropertyLoop);
        handler.ApplyFlsDrops(Components.Select(c => c.Field));

        var item = ConvertOne(handler, viaPropertyLoop);

        Assert.Equal("", AssembledAddress(item, viaPropertyLoop));
        // And no empty husk is published either.
        Assert.DoesNotContain("BillingAddress", FlsFixtures.ContentBody(item), StringComparison.Ordinal);
        Assert.False(((JsonObject)item["properties"]!).ContainsKey("BillingAddressText"));
    }

    // ── ANY COMPOUND-SHAPED VALUE FAILS CLOSED, WHATEVER ITS NAME ────────────

    /// <summary>
    /// Compound shapes the config never declared. None of these was individually
    /// reported; all of them are the same defect, and the structural rule covers them
    /// without a rule per name.
    /// </summary>
    public static TheoryData<bool, string> UndeclaredCompoundShapes()
    {
        var data = new TheoryData<bool, string>();
        foreach (var viaPropertyLoop in new[] { true, false })
        {
            foreach (var field in new[]
                     {
                         "ShippingAddress",          // a shipped compound with no group declared
                         "PersonMailingAddress",     // Person-Accounts only
                         "Custom_Address__c",        // a customer's own address compound
                         "Warehouse_Location__c",    // a geolocation compound
                         "MailingAddress",
                     })
            {
                data.Add(viaPropertyLoop, field);
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(UndeclaredCompoundShapes))]
    public void AnyUndeclaredCompoundValueIsIndexedByNoRoute(bool viaPropertyLoop, string compoundField)
    {
        const string Secret = "ZZUNDECLARED9-COMPOUND-SECRET";

        // Selected and mapped to a property that IS in the schema for the property
        // routing — the strongest version of the case, since that is the route that
        // used to call SerializeAddressObject on the raw compound.
        var selected = new JsonObject { ["Name"] = "Title", [compoundField] = "BillingAddressText" };
        var config = new JsonObject { ["objectName"] = "Account", ["selectedFields"] = selected };
        var handler = new SalesforceObjectHandler(config)
        {
            GraphSchemaProperties = GraphProps(viaPropertyLoop),
        };

        var compoundValue = compoundField.Contains("Location", StringComparison.Ordinal)
            ? new JsonObject { ["latitude"] = Secret, ["longitude"] = "-0.1" }
            : new JsonObject
            {
                ["street"] = Secret,
                ["city"] = "ZZCITY9",
                ["postalCode"] = "ZZPOST9",
            };

        var record = new JsonObject
        {
            ["attributes"] = new JsonObject { ["type"] = "Account" },
            ["Id"] = "001UNDECL",
            ["Name"] = AccountName,
            [compoundField] = compoundValue,
        };

        // NO drops applied at all. The point is that a compound is un-evaluable, so it
        // must not be indexed even when nothing has been restricted.
        var item = ConvertOne(handler, viaPropertyLoop, record);

        FlsFixtures.AssertDroppedFromBothLoops(item, Secret);
        Assert.DoesNotContain("ZZCITY9", item.ToJsonString(), StringComparison.Ordinal);
        Assert.Contains(AccountName, item.ToJsonString(), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(BothLoops))]
    public void DeclaredRelationshipObjectsAreNotMistakenForCompounds(bool viaPropertyLoop)
    {
        // The fail-closed rule must not swallow real relationship sub-objects, whose
        // sub-fields have their own permissions and their own gates.
        var config = new JsonObject
        {
            ["objectName"] = "Case",
            ["selectedFields"] = new JsonObject
            {
                ["Name"] = "Title",
                ["Contact.Name"] = "ContactName",
            },
            ["relationshipObjects"] = new JsonObject { ["Contact"] = "Contact" },
        };
        var handler = new SalesforceObjectHandler(config)
        {
            GraphSchemaProperties = GraphProps(viaPropertyLoop),
        };

        var record = new JsonObject
        {
            ["attributes"] = new JsonObject { ["type"] = "Case" },
            ["Id"] = "500REL",
            ["Name"] = AccountName,
            ["Contact"] = new JsonObject
            {
                ["attributes"] = new JsonObject { ["type"] = "Contact" },
                ["Name"] = "ZZCONTACT9-NAME",
            },
        };

        var item = ConvertOne(handler, viaPropertyLoop, record);

        Assert.Contains("ZZCONTACT9-NAME", item.ToJsonString(), StringComparison.Ordinal);
    }

    // ── A COMPONENT MAPPED TO A REAL PROPERTY IS STILL PUBLISHED ────────────

    /// <summary>
    /// Components that exist only to feed the assembly map to <c>_sf_</c> placeholders
    /// and never become Graph properties. But an operator may also map a component to a
    /// REAL schema property, and then they asked for that property: it must be
    /// published, gated like any other field, AND still feed the assembled address.
    /// Suppressing it would be silent data loss.
    /// </summary>
    private static SalesforceObjectHandler HandlerWithPublishedComponent(string[]? manualFlsFields = null)
    {
        var selected = new JsonObject { ["Name"] = "Title" };
        var components = new JsonObject();
        foreach (var (slot, field, _) in Components)
        {
            // BillingStreet maps to a REAL schema property; the rest stay placeholders.
            selected[field] = field == "BillingStreet" ? "StreetProperty" : "_sf_" + field;
            components[slot] = field;
        }
        var config = new JsonObject
        {
            ["objectName"] = "Account",
            ["selectedFields"] = selected,
            ["addressFields"] = new JsonObject
            {
                ["BillingAddress"] = new JsonObject
                {
                    ["property"] = "BillingAddressText",
                    ["components"] = components,
                },
            },
        };
        if (manualFlsFields is not null)
        {
            var arr = new JsonArray();
            foreach (var f in manualFlsFields)
                arr.Add(f);
            config["flsFields"] = arr;
        }
        return new SalesforceObjectHandler(config)
        {
            GraphSchemaProperties = PublishedComponentGraphProps(),
        };
    }

    private static HashSet<string> PublishedComponentGraphProps() =>
        new() { "ObjectName", "url", "Title", "StreetProperty", "BillingAddressText" };

    private static JsonObject ConvertWithPublishedComponent(SalesforceObjectHandler handler) =>
        Assert.Single(handler.ConstructIngestionItems(
            new JsonObject { ["records"] = new JsonArray { Record() } },
            FlsFixtures.InstanceUrl,
            PublishedComponentGraphProps()));

    [Fact]
    public void AComponentMappedToARealPropertyIsPublishedAndAlsoFeedsTheAddress()
    {
        var props = (JsonObject)ConvertWithPublishedComponent(HandlerWithPublishedComponent())["properties"]!;

        Assert.Equal("ZZSTREET9 Secret Lane", props["StreetProperty"]!.GetValue<string>());
        Assert.Equal(
            "ZZSTREET9 Secret Lane, ZZCITY9, ZZSTATE9 - ZZPOST9, ZZCOUNTRY9",
            props["BillingAddressText"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("BillingStreet")]
    [InlineData("StreetProperty")]
    public void RestrictingAPublishedComponentRemovesItFromTheSTANDALONEPropertyAndTheAddress(string spelling)
    {
        // Both routes out of one field, gated by either spelling of the drop. Gating
        // one and not the other is the dual-loop hazard in miniature.
        var handler = HandlerWithPublishedComponent();
        handler.ApplyFlsDrops(new[] { spelling });

        var item = ConvertWithPublishedComponent(handler);
        var props = (JsonObject)item["properties"]!;

        FlsFixtures.AssertDroppedFromBothLoops(item, "ZZSTREET9 Secret Lane");
        Assert.False(props.ContainsKey("StreetProperty"));
        Assert.Equal("ZZCITY9, ZZSTATE9 - ZZPOST9, ZZCOUNTRY9", props["BillingAddressText"]!.GetValue<string>());
    }

    [Fact]
    public void APlaceholderComponentIsNeverEmittedAsItsOwnRawValue()
    {
        // The other side: components that feed only the assembly must not ALSO appear
        // raw in the searchable body, or the address is published twice.
        var item = ConvertWithPublishedComponent(HandlerWithPublishedComponent());
        var body = FlsFixtures.ContentBody(item);
        var whole = item.ToJsonString();

        Assert.DoesNotContain("BillingCity", body, StringComparison.Ordinal);
        Assert.DoesNotContain("BillingPostalCode", body, StringComparison.Ordinal);
        // Exactly one occurrence in the WHOLE item: inside the assembled address only.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(whole, "ZZCITY9"));
    }

    // ── PERSON-ACCOUNTS: THE CATASTROPHIC CASE STAYS CLOSED ──────────────────

    [Theory]
    [MemberData(nameof(BothLoops))]
    public void RestrictingSalutationDoesNotDropAccountName(bool viaPropertyLoop)
    {
        // Under the removed name-inferring feature "Salutation" was taken to be a
        // component of the "Name" compound, so this drop took the account name with it
        // for every account in the org. Nothing in the new fix infers anything, so
        // there is no mechanism by which this could return.
        var handler = Handler(viaPropertyLoop);
        handler.ApplyFlsDrops(new[] { "Salutation" });

        var item = ConvertOne(handler, viaPropertyLoop);

        Assert.Contains(AccountName, item.ToJsonString(), StringComparison.Ordinal);
        FlsFixtures.AssertDroppedFromBothLoops(item, SalutationSecret);
    }

    // ── THE OPERATOR'S LEVER NOW REACHES INTO THE ADDRESS ────────────────────

    [Theory]
    [MemberData(nameof(BothLoops))]
    public void ManualFlsFieldsCanWithholdASingleAddressComponent(bool viaPropertyLoop)
    {
        // Previously the manual list could only drop the WHOLE compound. Because
        // components are now selected in their own right, an operator can withhold one
        // part of an address and keep the rest.
        var handler = Handler(viaPropertyLoop, manualFlsFields: new[] { "BillingPostalCode" });

        var item = ConvertOne(handler, viaPropertyLoop);

        FlsFixtures.AssertDroppedFromBothLoops(item, "ZZPOST9");
        Assert.Equal(
            "ZZSTREET9 Secret Lane, ZZCITY9, ZZSTATE9, ZZCOUNTRY9",
            AssembledAddress(item, viaPropertyLoop));
    }
}

/// <summary>
/// The shipped config itself. The residual was LIVE on config/schema.json, so the
/// config is part of the fix and is asserted directly rather than through a fixture.
/// </summary>
public class ShippedSchemaCompoundTests
{
    /// <summary>Every compound address field Salesforce exposes on the shipped objects.</summary>
    private static readonly string[] KnownCompounds =
    {
        "BillingAddress", "ShippingAddress", "MailingAddress", "OtherAddress", "Address",
        "PersonMailingAddress", "PersonOtherAddress",
    };

    private static JsonArray ObjectList()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "config", "schema.json");
        return JsonNode.Parse(File.ReadAllText(path))!["objectList"]!.AsArray();
    }

    [Fact]
    public void NoShippedObjectSelectsACompoundAddressField()
    {
        foreach (var entry in ObjectList())
        {
            var obj = entry!.AsObject();
            var name = obj["objectName"]!.GetValue<string>();
            foreach (var field in obj["selectedFields"]!.AsObject().Select(p => p.Key))
            {
                Assert.DoesNotContain(field, KnownCompounds);
            }
            Assert.NotEqual("", name);
        }
    }

    [Fact]
    public void EveryDeclaredAddressGroupSelectsAllFiveOfItsComponents()
    {
        var groupsSeen = 0;
        foreach (var entry in ObjectList())
        {
            var obj = entry!.AsObject();
            if (obj["addressFields"] is not JsonObject groups)
            {
                continue;
            }
            var selected = obj["selectedFields"]!.AsObject();
            foreach (var group in groups)
            {
                groupsSeen++;
                var components = group.Value!["components"]!.AsObject();
                Assert.Equal(
                    new[] { "street", "city", "state", "postalCode", "country" },
                    components.Select(c => c.Key).ToArray());
                foreach (var component in components)
                {
                    // Selected ⇒ SELECTed in SOQL ⇒ offered to ComputeDrops as a
                    // candidate ⇒ carries real FieldPermissions evidence.
                    Assert.True(
                        selected.ContainsKey(component.Value!.GetValue<string>()),
                        $"{obj["objectName"]}: component {component.Value} is not in selectedFields");
                }
            }
        }
        // Account (Billing + Shipping), Contact (Mailing + Other), Lead (Address).
        Assert.Equal(5, groupsSeen);
    }

    [Fact]
    public void ComputeDropsNowSeesEveryAddressComponentOnTheShippedAccountEntry()
    {
        // THE PROBE, verbatim. Pre-fix this produced DROPS=[Parent.Name] — zero
        // address drops — because SelectedFields.Keys held only the compound.
        var account = ObjectList()
            .First(o => o!["objectName"]!.GetValue<string>() == "Account")!.AsObject();
        var handler = new SalesforceObjectHandler(account);

        var components = new[]
        {
            "BillingStreet", "BillingCity", "BillingState", "BillingPostalCode", "BillingCountry",
        };
        var perms = new FlsObjectPermissions(
            objectName: "Account",
            principalsInScope: new HashSet<string> { "p1", "p2" },
            governedFields: new HashSet<string>(components),
            readersByField: components.ToDictionary(
                c => c, _ => new HashSet<string>(StringComparer.Ordinal)));

        var drops = FlsPolicy.ComputeDrops(perms, handler.SelectedFields.Keys, FlsMode.Strict);

        foreach (var component in components)
        {
            Assert.Contains(drops, d => d.Field == component);
        }
    }
}

/// <summary>
/// HOLE 4 — cross-object relationship fields were never FLS-evaluated.
/// </summary>
public class FlsRelationshipFieldTests
{
    /// <summary>Case's own permissions: nothing dotted ever appears in GovernedFields.</summary>
    private static FlsObjectPermissions CasePerms() => new(
        objectName: "Case",
        principalsInScope: new HashSet<string> { "psRep", "psManager" },
        governedFields: new HashSet<string> { "InternalComments" },
        readersByField: new Dictionary<string, HashSet<string>>
        {
            ["InternalComments"] = new() { "psRep", "psManager" },
        });

    /// <summary>Contact's permissions: Phone is readable by ONE of the two principals.</summary>
    private static FlsObjectPermissions ContactPerms() => new(
        objectName: "Contact",
        principalsInScope: new HashSet<string> { "psRep", "psManager" },
        governedFields: new HashSet<string> { "Phone", "Name" },
        readersByField: new Dictionary<string, HashSet<string>>
        {
            ["Phone"] = new() { "psManager" },
            ["Name"] = new() { "psRep", "psManager" },
        });

    private static string[] Candidates() => new[] { "Subject", "InternalComments", "Contact.Phone", "Contact.Name" };

    [Fact]
    public void RelatedObjectPermissionsDecideADottedFieldWhenTheTargetIsResolvable()
    {
        // THE PROBE. Pre-fix ComputeDrops matched "Contact.Phone" against Case's
        // GovernedFields (bare, own-object) and therefore never dropped it.
        var related = new Dictionary<string, FlsObjectPermissions>(StringComparer.OrdinalIgnoreCase)
        {
            ["Contact"] = ContactPerms(),
        };

        var dropped = FlsPolicy.ComputeDrops(
                CasePerms(), Candidates(), FlsMode.Strict,
                relationshipMode: FlsRelationshipMode.Evaluate,
                relatedPermissions: related)
            .Select(d => d.Field).ToHashSet(StringComparer.Ordinal);

        Assert.Contains("Contact.Phone", dropped);       // 1 of 2 principals on Contact → dropped
        Assert.DoesNotContain("Contact.Name", dropped);  // 2 of 2 → kept
        Assert.DoesNotContain("Subject", dropped);       // ungoverned on Case → kept
    }

    [Fact]
    public void AnUnresolvableRelationshipTargetFailsClosed()
    {
        // No permissions for the related object ⇒ we cannot prove anyone may read
        // it, so evaluate mode drops it rather than guessing.
        var dropped = FlsPolicy.ComputeDrops(
                CasePerms(), new[] { "ReportsTo.Name" }, FlsMode.Strict,
                relationshipMode: FlsRelationshipMode.Evaluate,
                relatedPermissions: new Dictionary<string, FlsObjectPermissions>(StringComparer.OrdinalIgnoreCase))
            .Select(d => d.Field).ToHashSet(StringComparer.Ordinal);

        Assert.Contains("ReportsTo.Name", dropped);
    }

    [Fact]
    public void DropModeDropsEveryDottedFieldUnconditionally()
    {
        var dropped = FlsPolicy.ComputeDrops(
                CasePerms(), Candidates(), FlsMode.Strict,
                relationshipMode: FlsRelationshipMode.Drop)
            .Select(d => d.Field).ToHashSet(StringComparer.Ordinal);

        Assert.Contains("Contact.Phone", dropped);
        Assert.Contains("Contact.Name", dropped);
        Assert.DoesNotContain("Subject", dropped);
    }

    [Fact]
    public void IgnoreModeReproducesThePreFixBehaviourExactly()
    {
        // The documented escape hatch: dotted fields are not evaluated at all.
        var dropped = FlsPolicy.ComputeDrops(
                CasePerms(), Candidates(), FlsMode.Strict,
                relationshipMode: FlsRelationshipMode.Ignore)
            .Select(d => d.Field).ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain("Contact.Phone", dropped);
        Assert.DoesNotContain("Contact.Name", dropped);
    }

    [Fact]
    public void PermissiveModeAppliesToRelatedObjectsToo()
    {
        var related = new Dictionary<string, FlsObjectPermissions>(StringComparer.OrdinalIgnoreCase)
        {
            ["Contact"] = ContactPerms(),
        };

        var dropped = FlsPolicy.ComputeDrops(
                CasePerms(), Candidates(), FlsMode.Permissive,
                relationshipMode: FlsRelationshipMode.Evaluate,
                relatedPermissions: related)
            .Select(d => d.Field).ToHashSet(StringComparer.Ordinal);

        // 1 of 2 readers ⇒ permissive keeps it, exactly as for own-object fields.
        Assert.DoesNotContain("Contact.Phone", dropped);
    }
}

/// <summary>
/// (b) A zero-row FieldPermissions result was indistinguishable from "this org
/// governs nothing", i.e. a silent fail-OPEN. The signal below makes it visible
/// without changing a single drop decision.
/// </summary>
public class FlsZeroRowSignalTests
{
    [Fact]
    public void PermissionsRecordWhetherFieldPermissionsReturnedAnyRowsAtAll()
    {
        var governedNothing = new FlsObjectPermissions(
            objectName: "Account",
            principalsInScope: new HashSet<string> { "psRep" },
            governedFields: new HashSet<string>(StringComparer.Ordinal),
            readersByField: new Dictionary<string, HashSet<string>>(StringComparer.Ordinal),
            fieldPermissionRowsSeen: false);

        Assert.False(governedNothing.FieldPermissionRowsSeen);
        Assert.True(governedNothing.IsSuspectedFailOpen);
    }

    [Fact]
    public void AGenuinelyUngovernedObjectInAnOrgThatHasRowsIsNotFlaggedSuspect()
    {
        // Rows were seen for the query as a whole; this object simply has none.
        var perms = new FlsObjectPermissions(
            objectName: "Campaign",
            principalsInScope: new HashSet<string> { "psRep" },
            governedFields: new HashSet<string>(StringComparer.Ordinal),
            readersByField: new Dictionary<string, HashSet<string>>(StringComparer.Ordinal),
            fieldPermissionRowsSeen: true);

        Assert.False(perms.IsSuspectedFailOpen);
    }

    [Fact]
    public void TheSuspectSignalDoesNotChangeAnyDropDecision()
    {
        // Explicitly: the signal must not false-drop.
        var suspect = new FlsObjectPermissions(
            objectName: "Account",
            principalsInScope: new HashSet<string> { "psRep" },
            governedFields: new HashSet<string>(StringComparer.Ordinal),
            readersByField: new Dictionary<string, HashSet<string>>(StringComparer.Ordinal),
            fieldPermissionRowsSeen: false);

        var drops = FlsPolicy.ComputeDrops(suspect, new[] { "Name", "Compensation__c" }, FlsMode.Strict);

        Assert.Empty(drops);
    }

    [Fact]
    public void TheSuspectSignalSurvivesACacheRoundTrip()
    {
        var suspect = new FlsObjectPermissions(
            objectName: "Account",
            principalsInScope: new HashSet<string> { "psRep" },
            governedFields: new HashSet<string>(StringComparer.Ordinal),
            readersByField: new Dictionary<string, HashSet<string>>(StringComparer.Ordinal),
            fieldPermissionRowsSeen: false);

        var restored = FlsObjectPermissions.FromJson(suspect.ToJson());

        Assert.NotNull(restored);
        Assert.False(restored!.FieldPermissionRowsSeen);
        Assert.True(restored.IsSuspectedFailOpen);
    }

    [Fact]
    public void ManifestRecordsTheSuspectedFailOpenObjects()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fls-manifest-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = FlsManifest.Write(
                "TestConnector",
                FlsMode.Strict,
                enforcement: true,
                new Dictionary<string, FlsObjectDrops>(StringComparer.Ordinal)
                {
                    ["Account"] = new FlsObjectDrops(2, new List<FlsDrop> { new("Margin__c", "strict: test") }),
                },
                logsDir: dir,
                suspectedFailOpenObjects: new[] { "Campaign", "Account" });

            var doc = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            var suspects = (doc["suspectedFailOpen"] as JsonArray)!.Select(n => n!.GetValue<string>()).ToArray();

            Assert.Equal(new[] { "Account", "Campaign" }, suspects);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }
}

/// <summary>
/// The compound removal on the CRAWL path (<see cref="FlsPolicy.ComputeDrops"/>).
/// Each candidate is judged ONLY on its own FieldPermissions evidence; no drop is
/// inferred from a field's NAME resembling a component or a compound.
/// </summary>
public class FlsCompoundRemovalPolicyTests
{
    private static FlsObjectPermissions Perms() => new(
        objectName: "Account",
        principalsInScope: new HashSet<string> { "psRep", "psManager" },
        governedFields: new HashSet<string> { "BillingStreet", "ShippingCity" },
        readersByField: new Dictionary<string, HashSet<string>>
        {
            ["BillingStreet"] = new() { "psManager" },              // 1 of 2 → restricted
            ["ShippingCity"] = new() { "psManager", "psRep" },      // 2 of 2 → fine
        });

    /// <summary>Person-Accounts shape: the person-name parts carry REAL rows.</summary>
    private static FlsObjectPermissions PersonAccountPerms() => new(
        objectName: "Account",
        principalsInScope: new HashSet<string> { "psRep", "psManager" },
        governedFields: new HashSet<string> { "Salutation", "FirstName", "LastName" },
        readersByField: new Dictionary<string, HashSet<string>>
        {
            ["Salutation"] = new() { "psManager" },                 // 1 of 2 → restricted
            ["FirstName"] = new() { "psManager" },                  // 1 of 2 → restricted
            ["LastName"] = new() { "psManager", "psRep" },          // 2 of 2 → fine
        });

    [Fact]
    public void ARestrictedComponentDoesNotDropTheCompoundCandidate()
    {
        // BillingAddress carries no FieldPermissions rows of its own, so it is not
        // dropped. This is the accepted residual — see docs/FLS.md.
        var drops = FlsPolicy.ComputeDrops(
            Perms(), new[] { "BillingAddress", "BillingStreet", "ShippingAddress" }, FlsMode.Strict,
            relationshipMode: FlsRelationshipMode.Ignore);

        Assert.DoesNotContain(drops, d => d.Field == "BillingAddress");
        Assert.DoesNotContain(drops, d => d.Field == "ShippingAddress");
        // The component with real evidence against it IS still dropped.
        Assert.Contains(drops, d => d.Field == "BillingStreet");
    }

    [Fact]
    public void RestrictedPersonNamePartsDoNotDropAccountName()
    {
        // THE CATASTROPHIC CASE on the crawl path. Salutation and FirstName are
        // genuinely restricted here; under the removed feature that dropped "Name"
        // for every account in the org, business accounts included.
        var drops = FlsPolicy.ComputeDrops(
            PersonAccountPerms(), new[] { "Name", "Salutation", "FirstName", "LastName" },
            FlsMode.Strict, relationshipMode: FlsRelationshipMode.Ignore);

        Assert.DoesNotContain(drops, d => d.Field == "Name");
        Assert.DoesNotContain(drops, d => d.Field == "LastName");
        Assert.Contains(drops, d => d.Field == "Salutation");
        Assert.Contains(drops, d => d.Field == "FirstName");
    }

    [Fact]
    public void NoDropReasonEverMentionsCompoundPropagation()
    {
        // Guard against the feature returning under another name: every reason must
        // trace to the field's OWN evidence.
        var drops = FlsPolicy.ComputeDrops(
            PersonAccountPerms(), new[] { "Name", "Salutation", "FirstName" },
            FlsMode.Strict, relationshipMode: FlsRelationshipMode.Ignore);

        Assert.All(drops, d => Assert.DoesNotContain("compound", d.Reason, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DescribeFallbackJudgesEachFieldOnItsOwnVisibility()
    {
        // Under describe fallback "absent" means "hidden". Each field is judged on
        // its own visibility — no inference from names in either direction.
        var perms = new FlsObjectPermissions(
            objectName: "Account",
            principalsInScope: new HashSet<string>(StringComparer.Ordinal),
            governedFields: new HashSet<string>(StringComparer.Ordinal),
            readersByField: new Dictionary<string, HashSet<string>>(StringComparer.Ordinal),
            fromDescribeFallback: true,
            describeVisibleFields: new HashSet<string> { "Name", "BillingAddress" });

        var drops = FlsPolicy.ComputeDrops(
            perms, new[] { "Name", "BillingAddress", "Margin__c" }, FlsMode.Strict);

        Assert.Equal(new[] { "Margin__c" }, drops.Select(d => d.Field).ToArray());
    }
}

/// <summary>
/// End-to-end wiring for relationship fields: FlsEnforcement.ApplyAsync must
/// resolve each relationship through the handler's operator-declared
/// <c>relationshipObjects</c> map, fetch the target object, and evaluate against it.
/// </summary>
public class FlsRelationshipWiringTests : IDisposable
{
    private readonly string _tmp = Directory.CreateTempSubdirectory("fls_rel_").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private sealed class StubFetcher : FlsFetcher
    {
        private readonly FlsSnapshot _snapshot;
        public List<string> Requested = new();

        public StubFetcher(FlsSnapshot snapshot)
            : base(new FlsFetcherTests.ScriptedSfClient(), null, FlsFixtures.InstanceUrl)
        {
            _snapshot = snapshot;
        }

        public override Task<FlsSnapshot> FetchAsync(IEnumerable<string> objectNames, bool forceRefresh = false)
        {
            Requested.AddRange(objectNames);
            return Task.FromResult(_snapshot);
        }
    }

    private static FlsSnapshot Snapshot()
    {
        var snapshot = new FlsSnapshot();
        snapshot.Set("Case", new FlsObjectPermissions(
            objectName: "Case",
            principalsInScope: new HashSet<string> { "psRep", "psManager" },
            governedFields: new HashSet<string> { "Subject" },
            readersByField: new Dictionary<string, HashSet<string>>
            {
                ["Subject"] = new() { "psRep", "psManager" },
            }));
        snapshot.Set("Contact", new FlsObjectPermissions(
            objectName: "Contact",
            principalsInScope: new HashSet<string> { "psRep", "psManager" },
            governedFields: new HashSet<string> { "Phone", "Name" },
            readersByField: new Dictionary<string, HashSet<string>>
            {
                ["Phone"] = new() { "psManager" },                  // 1 of 2 → dropped
                ["Name"] = new() { "psRep", "psManager" },          // 2 of 2 → kept
            }));
        return snapshot;
    }

    private static SalesforceObjectHandler CaseHandler() => new(new JsonObject
    {
        ["objectName"] = "Case",
        ["selectedFields"] = new JsonObject
        {
            ["Subject"] = "Subject",
            ["Contact.Phone"] = "_sf_ContactPhone",
            ["Contact.Name"] = "ContactName",
            ["Parent.CaseNumber"] = "_sf_ParentCaseNumber",
        },
        ["relationshipObjects"] = new JsonObject { ["Contact"] = "Contact" },
    });

    [Fact]
    public async Task DeclaredRelationshipIsFetchedAndEvaluatedAgainstItsTargetObject()
    {
        var handler = CaseHandler();
        var fetcher = new StubFetcher(Snapshot());

        var summary = await FlsEnforcement.ApplyAsync(
            fetcher,
            new Dictionary<string, SalesforceObjectHandler> { ["Case"] = handler },
            connectorId: "RelWire",
            mode: FlsMode.Strict,
            logsDir: _tmp,
            relationshipMode: FlsRelationshipMode.Evaluate);

        // Contact joined the fetch purely because Case declares it as a target.
        Assert.Contains("Contact", fetcher.Requested);

        var dropped = summary["Case"].Drops.Select(d => d.Field).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("Contact.Phone", dropped);          // 1 of 2 on Contact
        Assert.DoesNotContain("Contact.Name", dropped);     // 2 of 2 on Contact
        Assert.Contains("Parent.CaseNumber", dropped);      // undeclared → fails closed
        Assert.DoesNotContain("Subject", dropped);
    }

    [Fact]
    public async Task TheDroppedRelationshipFieldIsGoneFromBothLoops()
    {
        const string phoneSecret = "555-SECRET-PHONE";
        var handler = CaseHandler();
        handler.GraphSchemaProperties = new HashSet<string>
        {
            "ObjectName", "url", "Subject", "_sf_ContactPhone", "ContactName",
        };

        await FlsEnforcement.ApplyAsync(
            new StubFetcher(Snapshot()),
            new Dictionary<string, SalesforceObjectHandler> { ["Case"] = handler },
            connectorId: "RelWire2",
            mode: FlsMode.Strict,
            logsDir: _tmp,
            relationshipMode: FlsRelationshipMode.Evaluate);

        var record = new JsonObject
        {
            ["attributes"] = new JsonObject { ["type"] = "Case" },
            ["Id"] = "500REL",
            ["Subject"] = "Broken widget",
            ["Contact"] = new JsonObject
            {
                ["Name"] = "Dana Public",
                ["Phone"] = phoneSecret,
            },
        };
        var item = Assert.Single(handler.ConstructIngestionItems(
            new JsonObject { ["records"] = new JsonArray { record } },
            FlsFixtures.InstanceUrl,
            handler.GraphSchemaProperties!));

        FlsFixtures.AssertDroppedFromBothLoops(item, phoneSecret);
        Assert.Contains("Dana Public", item.ToJsonString());
    }

    [Fact]
    public void TheShippedSchemaDeclaresATargetForEveryRelationshipItUses()
    {
        // Every dotted selectedFields key in config/schema.json must have a
        // declared target, or evaluate mode silently drops it.
        var config = Converter.LoadConverterConfig(SchemaConfigPath());
        var handlers = Converter.BuildHandlersFromConfig(config);

        var undeclared = new List<string>();
        foreach (var handler in handlers.Values)
        {
            foreach (var key in handler.SelectedFields.Keys.Where(k => k.Contains('.', StringComparison.Ordinal)))
            {
                var relationship = key[..key.IndexOf('.', StringComparison.Ordinal)];
                if (!handler.RelationshipObjects.ContainsKey(relationship))
                    undeclared.Add($"{handler.ObjectName}.{key}");
            }
        }

        Assert.Empty(undeclared);
    }

    [Fact]
    public void EveryDeclaredRelationshipTargetIsItselfAConfiguredObject()
    {
        // Otherwise the target's permissions could never be fetched from the
        // configured object list and the field would fail closed anyway.
        var config = Converter.LoadConverterConfig(SchemaConfigPath());
        var handlers = Converter.BuildHandlersFromConfig(config);

        foreach (var handler in handlers.Values)
        {
            foreach (var target in handler.RelationshipObjects.Values)
                Assert.Contains(target, handlers.Keys);
        }
    }

    private static string SchemaConfigPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "config", "schema.json")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "config", "schema.json");
    }
}

/// <summary>
/// THE OPERATOR LEVER'S TWO SILENT FAILURE MODES.
///
/// <para>(a) CASE SENSITIVITY. <c>_effectiveFlsFields</c> matched with
/// <c>StringComparer.Ordinal</c>, so a hand-typed <c>flsFields</c> entry that differed
/// only in case silently dropped NOTHING while looking, in config, exactly like a
/// drop. Proven: the spelling <c>billingaddress</c> leaked
/// <c>'BillingAddress.street: ZZCASE9 Lane'</c> in full where <c>BillingAddress</c>
/// dropped it cleanly. Matching is now case-insensitive (a typo WORKS) and a warning
/// names the mismatch (a typo is also VISIBLE). Never a silent no-op.</para>
///
/// <para>(b) UNDECLARED PROPERTIES. The retained legacy line wrote
/// <c>props[flsField] = null</c> unconditionally, so naming any non-property field
/// posted an UNDECLARED null property to Graph — and Graph/Ingest.cs applies no
/// schema-conformance filter before push. Proven: <c>PROPS</c> included
/// <c>"BillingAddress":null</c>, <c>UNDECLARED=[BillingAddress]</c>.</para>
/// </summary>
public class FlsOperatorLeverSpellingTests
{
    private const string Secret = "ZZCASE9-COMPENSATION-SECRET";
    private const string MarginSecret = "ZZCASE9-MARGIN-SECRET";

    private static HashSet<string> GraphProps() =>
        new() { "ObjectName", "url", "Title", "Compensation" };

    private static SalesforceObjectHandler Handler(params string[] flsFields)
    {
        var arr = new JsonArray();
        foreach (var f in flsFields)
            arr.Add(f);
        var config = new JsonObject
        {
            ["objectName"] = "Account",
            ["selectedFields"] = new JsonObject
            {
                ["Name"] = "Title",
                ["Compensation__c"] = "Compensation",
                ["Margin__c"] = "Margin",
            },
            ["flsFields"] = arr,
        };
        return new SalesforceObjectHandler(config) { GraphSchemaProperties = GraphProps() };
    }

    private static JsonObject ConvertOne(SalesforceObjectHandler handler)
    {
        var record = new JsonObject
        {
            ["attributes"] = new JsonObject { ["type"] = "Account" },
            ["Id"] = "001CASE",
            ["Name"] = "Acme Corp",
            ["Compensation__c"] = Secret,
            ["Margin__c"] = MarginSecret,
        };
        return Assert.Single(handler.ConstructIngestionItems(
            new JsonObject { ["records"] = new JsonArray { record } },
            FlsFixtures.InstanceUrl,
            GraphProps()));
    }

    /// <summary>
    /// Every casing variant of BOTH spellings (the Salesforce field and the Graph
    /// property), on BOTH assembly routings — Compensation__c travels the property
    /// loop, Margin__c the content loop. Generated, not hand-picked.
    /// </summary>
    public static TheoryData<string, string> EveryCasingVariant()
    {
        var data = new TheoryData<string, string>();
        foreach (var (canonical, secret) in new[]
                 {
                     ("Compensation__c", Secret),
                     ("Compensation", Secret),
                     ("Margin__c", MarginSecret),
                     ("Margin", MarginSecret),
                 })
        {
            foreach (var variant in new[]
                     {
                         canonical,
                         canonical.ToUpperInvariant(),
                         canonical.ToLowerInvariant(),
                         string.Concat(canonical[..1].ToLowerInvariant(), canonical[1..]),
                     }.Distinct(StringComparer.Ordinal))
            {
                data.Add(variant, secret);
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(EveryCasingVariant))]
    public void AnyCasingOfAnFlsFieldsEntryDropsTheField(string spelling, string secret)
    {
        // THE PROBE. Pre-fix every non-exact casing here leaked the value in full.
        FlsFixtures.AssertDroppedFromBothLoops(ConvertOne(Handler(spelling)), secret);
    }

    [Fact]
    public void CasingStillDoesNotBleedAcrossDIFFERENTFields()
    {
        // Case-insensitivity must not become a substring or fuzzy match: naming one
        // field never touches another.
        var item = ConvertOne(Handler("compensation"));

        FlsFixtures.AssertDroppedFromBothLoops(item, Secret);
        Assert.Contains(MarginSecret, item.ToJsonString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Compensation")]
    [InlineData("compensation")]
    [InlineData("COMPENSATION")]
    public void ADeclaredPropertyIsNulledUnderTheSCHEMASSpellingOnly(string spelling)
    {
        var props = (JsonObject)ConvertOne(Handler(spelling))["properties"]!;

        // Present and null under the schema's spelling…
        Assert.True(props.ContainsKey("Compensation"));
        Assert.Null(props["Compensation"]);
        // …and no differently-cased ghost of it alongside.
        Assert.Equal(1, props.Count(p => StringComparer.OrdinalIgnoreCase.Equals(p.Key, "Compensation")));
    }

    /// <summary>
    /// The undeclared-property class, swept: anything an operator can type that is not
    /// a Graph schema property must add no key at all. Margin is a mapped property that
    /// is deliberately NOT in the schema; the rest are a compound, a relationship path
    /// and a plain typo.
    /// </summary>
    [Theory]
    [InlineData("BillingAddress")]
    [InlineData("billingaddress")]
    [InlineData("Margin")]
    [InlineData("Margin__c")]
    [InlineData("Parent.Name")]
    [InlineData("NoSuchField__c")]
    public void AnFlsFieldsEntryThatIsNotAGraphPropertyAddsNoProperty(string spelling)
    {
        // THE PROBE. Pre-fix: PROPS included "BillingAddress":null, UNDECLARED=[BillingAddress].
        var props = (JsonObject)ConvertOne(Handler(spelling))["properties"]!;

        foreach (var key in props.Select(p => p.Key))
        {
            Assert.Contains(key, GraphProps());
        }
    }

    [Fact]
    public void NoPropertyIsEverEmittedThatTheGraphSchemaDoesNotDeclare()
    {
        // The whole class at once: every entry above, applied together.
        var handler = Handler(
            "BillingAddress", "billingaddress", "Margin", "Parent.Name", "NoSuchField__c", "compensation");

        var props = (JsonObject)ConvertOne(handler)["properties"]!;

        var undeclared = props.Select(p => p.Key).Where(k => !GraphProps().Contains(k)).ToArray();
        Assert.Empty(undeclared);
    }
}
