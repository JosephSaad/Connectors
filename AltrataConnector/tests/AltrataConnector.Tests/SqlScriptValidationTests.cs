// Static validation of scripts/sql/create-database.sql without a live SQL
// Server (ported from the reference connector's SqlScriptValidationTests):
//
//  1. the script parses cleanly under the real SQL Server 2019 grammar
//     (TSql150Parser is the actual parser frontend, not a lint);
//  2. every DDL statement is idempotent by construction (guarded CREATEs) —
//     the re-run safety the script header promises;
//  3. the script does not drift from the DDL constants embedded in the
//     connector (SqlStateStore.SchemaScript / SqlServerIdentityStore
//     .SchemaScript) — the runtime auto-provisions from those constants, so
//     they and this file must describe the same schema;
//  4. a DacFx semantic model (the offline engine behind SQL database projects)
//     builds and validates the declarative schema.
//
// Read-only over repo files; no env vars or process-global seams touched.

using System.Text;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.Dac;
using Microsoft.SqlServer.Dac.Model;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using AltrataConnector.Identity;
using AltrataConnector.State;

namespace AltrataConnector.Tests;

public class SqlScriptValidationTests
{
    // ── repo file access ────────────────────────────────────────────────────────

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && dir.GetFiles("AltrataConnector.sln").Length == 0)
            dir = dir.Parent;
        Assert.True(dir != null,
            "could not locate repo root (AltrataConnector.sln) above " + AppContext.BaseDirectory);
        return dir!.FullName;
    }

    private static string ScriptPath => Path.Combine(RepoRoot(), "scripts", "sql", "create-database.sql");

    /// <summary>
    /// Apply the same preprocessing sqlcmd does before the server sees the
    /// script: collect :setvar defaults, strip sqlcmd directives, substitute
    /// $(NAME) references.
    /// </summary>
    private static string SqlcmdPreprocess(string raw)
    {
        var setvars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder();
        foreach (var line in raw.Replace("\r\n", "\n").Split('\n'))
        {
            var setvar = Regex.Match(line, "^\\s*:setvar\\s+(\\w+)\\s+\"?([^\"]*?)\"?\\s*$");
            if (setvar.Success)
            {
                setvars[setvar.Groups[1].Value] = setvar.Groups[2].Value;
                sb.Append('\n');  // keep line numbers aligned with the file
                continue;
            }
            if (Regex.IsMatch(line, "^\\s*:")) { sb.Append('\n'); continue; }
            sb.Append(line).Append('\n');
        }
        return Regex.Replace(sb.ToString(), "\\$\\((\\w+)\\)",
            m => setvars.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);
    }

    private static TSqlFragment ParseScript(out IList<ParseError> errors)
    {
        var sql = SqlcmdPreprocess(File.ReadAllText(ScriptPath));
        var parser = new TSql150Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(sql);
        return parser.Parse(reader, out errors);
    }

    // ── AST collectors ──────────────────────────────────────────────────────────

    private sealed class GuardableCollector : TSqlFragmentVisitor
    {
        public List<TSqlStatement> Found { get; } = new();
        public override void ExplicitVisit(CreateTableStatement node) { Found.Add(node); base.ExplicitVisit(node); }
        public override void ExplicitVisit(CreateIndexStatement node) { Found.Add(node); base.ExplicitVisit(node); }
        public override void ExplicitVisit(CreateDatabaseStatement node) { Found.Add(node); base.ExplicitVisit(node); }
        public override void ExplicitVisit(CreateLoginStatement node) { Found.Add(node); base.ExplicitVisit(node); }
        public override void ExplicitVisit(CreateUserStatement node) { Found.Add(node); base.ExplicitVisit(node); }
    }

    private sealed class GuardedCollector : TSqlFragmentVisitor
    {
        public HashSet<TSqlStatement> Guarded { get; } = new();
        public override void ExplicitVisit(IfStatement node)
        {
            var inner = new GuardableCollector();
            node.ThenStatement?.Accept(inner);
            foreach (var statement in inner.Found)
                Guarded.Add(statement);
            base.ExplicitVisit(node);
        }
    }

    // ── 1. real-grammar parse ───────────────────────────────────────────────────

    [Fact]
    public void ScriptParsesCleanUnderSqlServer2019Grammar()
    {
        ParseScript(out var errors);
        Assert.True(errors.Count == 0,
            "create-database.sql has T-SQL syntax errors:\n" +
            string.Join("\n", errors.Select(e => $"  line {e.Line}, col {e.Column}: {e.Message}")));
    }

    // ── 2. idempotency by construction ─────────────────────────────────────────

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
            "DDL statements not wrapped in an existence guard (breaks re-run safety):\n" +
            string.Join("\n", unguarded.Select(s => $"  line {s.StartLine}: {s.GetType().Name}")));

        // The script must contain a meaningful amount of DDL — an empty parse
        // would vacuously pass the guard check above. 9 tables + 1 database.
        Assert.True(all.Found.Count >= 10,
            $"only {all.Found.Count} DDL statements found — extraction broken?");
    }

    // ── 3. drift check: script ⇄ embedded runtime constants ────────────────────

    private static string NormalizeSql(string sql) =>
        Regex.Replace(sql.Replace("\r\n", "\n"), "\\s+", " ").Trim();

    [Fact]
    public void ScriptContainsExactlyTheEmbeddedStateSchema()
    {
        var script = NormalizeSql(File.ReadAllText(ScriptPath));
        Assert.Contains(NormalizeSql(SqlStateStore.SchemaScript), script);
    }

    [Fact]
    public void ScriptContainsExactlyTheEmbeddedIdentitySchema()
    {
        var script = NormalizeSql(File.ReadAllText(ScriptPath));
        Assert.Contains(NormalizeSql(SqlServerIdentityStore.SchemaScript), script);
    }

    [Fact]
    public void EveryTableTheCodeTouchesExistsInTheScript()
    {
        // Every "dbo.xxx" referenced from C# must be created by the script —
        // the "typo'd table name" class of bug a live server would report.
        var fragment = ParseScript(out _);
        var tables = new GuardableCollector();
        fragment.Accept(tables);
        var created = tables.Found.OfType<CreateTableStatement>()
            .Select(t => t.SchemaObjectName.BaseIdentifier.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var srcRoot = Path.Combine(RepoRoot(), "src", "AltrataConnector");
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories))
        {
            foreach (Match match in Regex.Matches(File.ReadAllText(file), @"dbo\.(altrata_\w+)"))
                referenced.Add(match.Groups[1].Value);
        }

        Assert.True(referenced.Count >= 9, $"only {referenced.Count} dbo.* references found — extractor broken?");
        var missing = referenced.Except(created, StringComparer.OrdinalIgnoreCase).OrderBy(t => t).ToList();
        Assert.True(missing.Count == 0,
            "C# references tables the script does not create: " + string.Join(", ", missing));
    }

    // ── 4. DacFx semantic model ────────────────────────────────────────────────

    [Fact]
    public void SemanticModelBuildsAndValidatesWithoutErrors()
    {
        var fragment = ParseScript(out var parseErrors);
        Assert.Empty(parseErrors);

        var guardables = new GuardableCollector();
        fragment.Accept(guardables);

        // A DacFx model is declarative — unwrap the guarded CREATE TABLEs into
        // the schema a fresh deployment produces.
        var generator = new Sql150ScriptGenerator();
        using var model = new TSqlModel(SqlServerVersion.Sql150, new TSqlModelOptions());
        var tables = guardables.Found.OfType<CreateTableStatement>().ToList();
        Assert.True(tables.Count >= 9, $"only {tables.Count} tables extracted — collector broken?");
        foreach (var table in tables)
        {
            generator.GenerateScript(table, out var sql);
            model.AddObjects(sql);
        }

        var problems = model.Validate()
            .Where(m => m.MessageType == DacMessageType.Error
                        || m.Message.Contains("unresolved reference", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.True(problems.Count == 0,
            "DacFx semantic validation problems:\n" +
            string.Join("\n", problems.Select(p => $"  {p.MessageType} {p.Prefix}{p.Number}: {p.Message}")));
    }
}
