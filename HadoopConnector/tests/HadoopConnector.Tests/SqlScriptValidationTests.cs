// Static validation of scripts/sql/create-database.sql without a live SQL
// Server (ported from the Salesforce connector's offline SQL suite):
//
//  1. the script parses cleanly under the real SQL Server 2019 grammar
//     (TSql150Parser is the actual parser frontend, not a lint);
//  2. every DDL statement is idempotent by construction (guarded CREATEs,
//     CREATE OR ALTER modules) — the re-run safety the script header promises;
//  3. every dbo.* table/view referenced from C# inline SQL exists in the
//     script (this codebase uses parameterized inline SQL, not stored procs);
//  4. a DacFx semantic model (the offline engine behind SQL database
//     projects) builds and validates the script — unresolved tables/columns
//     inside view/procedure bodies fail here.
//
// Read-only over repo files; no env vars or process-global seams touched.

using System.Text.RegularExpressions;
using Microsoft.SqlServer.Dac;
using Microsoft.SqlServer.Dac.Model;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace HadoopConnector.Tests;

public class SqlScriptValidationTests
{
    // ── repo file access ─────────────────────────────────────────────────────

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && dir.GetFiles("HadoopConnector.sln").Length == 0)
            dir = dir.Parent;
        Assert.True(dir != null,
            "could not locate repo root (HadoopConnector.sln) above " + AppContext.BaseDirectory);
        return dir!.FullName;
    }

    private static string ScriptPath => Path.Combine(RepoRoot(), "scripts", "sql", "create-database.sql");
    private static string SrcRoot => Path.Combine(RepoRoot(), "src", "HadoopConnector");

    private static TSqlFragment ParseScript(out IList<ParseError> errors)
    {
        var parser = new TSql150Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(File.ReadAllText(ScriptPath));
        return parser.Parse(reader, out errors);
    }

    // ── AST collectors ───────────────────────────────────────────────────────

    private sealed class GuardableCollector : TSqlFragmentVisitor
    {
        public List<TSqlStatement> Found { get; } = new();
        public override void ExplicitVisit(CreateTableStatement node) { Found.Add(node); base.ExplicitVisit(node); }
        public override void ExplicitVisit(CreateIndexStatement node) { Found.Add(node); base.ExplicitVisit(node); }
        public override void ExplicitVisit(AlterTableAddTableElementStatement node) { Found.Add(node); base.ExplicitVisit(node); }
    }

    private sealed class GuardedCollector : TSqlFragmentVisitor
    {
        public HashSet<TSqlStatement> Guarded { get; } = new();
        public override void ExplicitVisit(IfStatement node)
        {
            var inner = new GuardableCollector();
            node.ThenStatement?.Accept(inner);
            foreach (var s in inner.Found)
                Guarded.Add(s);
            base.ExplicitVisit(node);
        }
    }

    private sealed class ModuleCollector : TSqlFragmentVisitor
    {
        public List<TSqlStatement> Modules { get; } = new();
        public List<TSqlStatement> NonIdempotentModules { get; } = new();

        public override void ExplicitVisit(CreateOrAlterProcedureStatement node) { Modules.Add(node); base.ExplicitVisit(node); }
        public override void ExplicitVisit(CreateOrAlterViewStatement node) { Modules.Add(node); base.ExplicitVisit(node); }

        // Plain CREATE (no OR ALTER) breaks re-runs.
        public override void ExplicitVisit(CreateProcedureStatement node) { NonIdempotentModules.Add(node); base.ExplicitVisit(node); }
        public override void ExplicitVisit(CreateViewStatement node) { NonIdempotentModules.Add(node); base.ExplicitVisit(node); }
        public override void ExplicitVisit(CreateFunctionStatement node) { NonIdempotentModules.Add(node); base.ExplicitVisit(node); }
        public override void ExplicitVisit(CreateTriggerStatement node) { NonIdempotentModules.Add(node); base.ExplicitVisit(node); }
    }

    // ── 1. real-grammar parse ────────────────────────────────────────────────

    [Fact]
    public void ScriptParsesCleanUnderSqlServer2019Grammar()
    {
        ParseScript(out var errors);
        Assert.True(errors.Count == 0,
            "create-database.sql has T-SQL syntax errors:\n"
            + string.Join("\n", errors.Select(e => $"  line {e.Line}, col {e.Column}: {e.Message}")));
    }

    // ── 2. idempotency by construction ───────────────────────────────────────

    [Fact]
    public void EveryDdlStatementIsIdempotentByConstruction()
    {
        var fragment = ParseScript(out var errors);
        Assert.Empty(errors);

        var all = new GuardableCollector();
        fragment.Accept(all);
        var guarded = new GuardedCollector();
        fragment.Accept(guarded);

        var unguarded = all.Found.Where(s => !guarded.Guarded.Contains(s)).ToList();
        Assert.True(unguarded.Count == 0,
            "DDL statements not wrapped in an existence guard (breaks re-run safety):\n"
            + string.Join("\n", unguarded.Select(s => $"  line {s.StartLine}: {s.GetType().Name}")));

        var modules = new ModuleCollector();
        fragment.Accept(modules);
        Assert.True(modules.NonIdempotentModules.Count == 0,
            "modules using plain CREATE instead of CREATE OR ALTER:\n"
            + string.Join("\n", modules.NonIdempotentModules.Select(s => $"  line {s.StartLine}: {s.GetType().Name}")));

        // An empty parse would vacuously pass the guards above.
        Assert.True(all.Found.Count >= 6,
            $"only {all.Found.Count} DDL statements found — extraction broken?");
        Assert.True(modules.Modules.Count >= 2,
            $"only {modules.Modules.Count} CREATE OR ALTER modules found — extraction broken?");
    }

    // ── 3. C# inline SQL ⇄ script table/view drift ───────────────────────────

    [Fact]
    public void CSharpInlineSqlReferencesOnlyTablesTheScriptCreates()
    {
        var fragment = ParseScript(out _);
        var tables = new GuardableCollector();
        fragment.Accept(tables);
        var modules = new ModuleCollector();
        fragment.Accept(modules);

        var defined = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in tables.Found.OfType<CreateTableStatement>())
            defined.Add(table.SchemaObjectName.BaseIdentifier.Value);
        foreach (var module in modules.Modules)
        {
            var name = module switch
            {
                CreateOrAlterViewStatement view => view.SchemaObjectName.BaseIdentifier.Value,
                CreateOrAlterProcedureStatement proc => proc.ProcedureReference.Name.BaseIdentifier.Value,
                _ => null,
            };
            if (name is not null)
                defined.Add(name);
        }

        var referenced = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var dboRef = new Regex(@"dbo\.(\w+)");
        foreach (var file in Directory.EnumerateFiles(SrcRoot, "*.cs", SearchOption.AllDirectories))
        {
            foreach (Match match in dboRef.Matches(File.ReadAllText(file)))
            {
                var name = match.Groups[1].Value;
                if (!referenced.TryGetValue(name, out var files))
                    referenced[name] = files = new List<string>();
                files.Add(Path.GetFileName(file));
            }
        }

        Assert.True(referenced.Count >= 5,
            $"only {referenced.Count} dbo.* references extracted from C# — extractor broken?");

        var missing = referenced.Keys
            .Where(name => !defined.Contains(name))
            .OrderBy(name => name)
            .ToList();
        Assert.True(missing.Count == 0,
            "C# inline SQL references objects create-database.sql does not define:\n"
            + string.Join("\n", missing.Select(m => $"  dbo.{m} (from {string.Join(", ", referenced[m].Distinct())})")));
    }

    // ── 4. DacFx semantic model (offline column/table reference binding) ─────

    /// <summary>
    /// A DacFx model is declarative — unwrap guarded CREATEs and turn
    /// CREATE OR ALTER into the CREATE that dacpac models require.
    /// </summary>
    private static List<string> DeclarativeSchemaChunks()
    {
        var fragment = ParseScript(out var parseErrors);
        Assert.Empty(parseErrors);

        var guardables = new GuardableCollector();
        fragment.Accept(guardables);
        var modules = new ModuleCollector();
        fragment.Accept(modules);

        var generator = new Sql150ScriptGenerator();
        var chunks = new List<string>();

        foreach (var table in guardables.Found.OfType<CreateTableStatement>())
        {
            generator.GenerateScript(table, out var sql);
            chunks.Add(sql);
        }
        foreach (var index in guardables.Found.OfType<CreateIndexStatement>())
        {
            generator.GenerateScript(index, out var sql);
            chunks.Add(sql);
        }
        foreach (var module in modules.Modules)
        {
            generator.GenerateScript(module, out var sql);
            var idx = sql.IndexOf("CREATE OR ALTER ", StringComparison.OrdinalIgnoreCase);
            Assert.True(idx >= 0, "module did not script as CREATE OR ALTER:\n" + sql);
            chunks.Add(sql.Remove(idx + 7, 9)); // "CREATE OR ALTER " → "CREATE "
        }
        return chunks;
    }

    [Fact]
    public void SemanticModelBuildsAndValidatesWithoutErrors()
    {
        using var model = new TSqlModel(SqlServerVersion.Sql150, new TSqlModelOptions());
        foreach (var chunk in DeclarativeSchemaChunks())
        {
            try
            {
                model.AddObjects(chunk);
            }
            catch (DacModelException ex)
            {
                Assert.Fail($"DacFx rejected schema chunk:\n{chunk}\n---\n{ex.Message}");
            }
        }

        var problems = model.Validate()
            .Where(m => m.MessageType == DacMessageType.Error
                        || m.Message.Contains("unresolved reference", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(problems.Count == 0,
            "DacFx semantic validation problems (unresolved columns/tables, invalid constructs):\n"
            + string.Join("\n", problems.Select(e => $"  {e.MessageType} {e.Prefix}{e.Number}: {e.Message}")));
    }
}
