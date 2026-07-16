// Static validation of scripts/sql/create-database.sql without a live SQL
// Server (ported from the reference connector's offline SQL validation suite).
//
// The SQL backend only runs when USE_SQL_SERVER=true, so on a machine with no
// SQL Server the DDL surface would otherwise ship unexecuted. These tests
// close most of that gap offline:
//
//  1. the script parses cleanly under the real SQL Server 2019 grammar
//     (TSql150Parser is the actual parser frontend, not a lint);
//  2. every DDL statement is idempotent by construction (guarded CREATEs) —
//     the re-run safety the script header promises;
//  3. the in-code auto-provision DDL (SqlExecutor.SchemaDdl) and the script
//     agree on the exact table/column shapes, both directions;
//  4. the table set matches docs/SQL_CONTRACT.md, both directions;
//  5. a DacFx semantic model (the offline engine behind SQL database
//     projects) builds and validates the declarative schema.

using System.Text;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.Dac;
using Microsoft.SqlServer.Dac.Model;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SeismicConnector.Infrastructure;

namespace SeismicConnector.Tests;

public class SqlScriptValidationTests
{
    // ── repo file access ─────────────────────────────────────────────────────

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && dir.GetFiles("SeismicConnector.sln").Length == 0)
            dir = dir.Parent;
        Assert.True(dir != null,
            "could not locate repo root (SeismicConnector.sln) above " + AppContext.BaseDirectory);
        return dir!.FullName;
    }

    private static string ScriptPath => Path.Combine(RepoRoot(), "scripts", "sql", "create-database.sql");
    private static string LoginScriptPath => Path.Combine(RepoRoot(), "scripts", "sql", "create-login.sql");
    private static string ContractPath => Path.Combine(RepoRoot(), "docs", "SQL_CONTRACT.md");

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
            if (Regex.IsMatch(line, "^\\s*:"))
            {
                sb.Append('\n');
                continue;
            }
            sb.Append(line).Append('\n');
        }
        return Regex.Replace(sb.ToString(), "\\$\\((\\w+)\\)",
            m => setvars.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);
    }

    private static TSqlFragment Parse(string sql, out IList<ParseError> errors)
    {
        var parser = new TSql150Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(sql);
        return parser.Parse(reader, out errors);
    }

    private static TSqlFragment ParseScript(out IList<ParseError> errors) =>
        Parse(SqlcmdPreprocess(File.ReadAllText(ScriptPath)), out errors);

    // ── AST collectors ───────────────────────────────────────────────────────

    /// <summary>Collects every node of the DDL statement types that must be guarded.</summary>
    private sealed class GuardableCollector : TSqlFragmentVisitor
    {
        public List<TSqlStatement> Found { get; } = new();

        public override void ExplicitVisit(CreateTableStatement node)
        {
            Found.Add(node);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(CreateIndexStatement node)
        {
            Found.Add(node);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(CreateDatabaseStatement node)
        {
            Found.Add(node);
            base.ExplicitVisit(node);
        }
    }

    /// <summary>Marks guardable statements that sit inside the THEN branch of an IF.</summary>
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

    /// <summary>table name (lowercase) → column names (lowercase).</summary>
    private static Dictionary<string, HashSet<string>> ExtractTables(string sql)
    {
        var fragment = Parse(sql, out var errors);
        Assert.True(errors.Count == 0,
            "T-SQL syntax errors:\n"
            + string.Join("\n", errors.Select(e => $"  line {e.Line}: {e.Message}")));
        var collector = new GuardableCollector();
        fragment.Accept(collector);
        var tables = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in collector.Found.OfType<CreateTableStatement>())
        {
            var name = table.SchemaObjectName.BaseIdentifier.Value;
            tables[name] = new HashSet<string>(
                table.Definition.ColumnDefinitions.Select(c => c.ColumnIdentifier.Value),
                StringComparer.OrdinalIgnoreCase);
        }
        return tables;
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

    [Fact]
    public void LoginScriptParsesCleanUnderSqlServer2019Grammar()
    {
        Parse(SqlcmdPreprocess(File.ReadAllText(LoginScriptPath)), out var errors);
        Assert.True(errors.Count == 0,
            "create-login.sql has T-SQL syntax errors:\n"
            + string.Join("\n", errors.Select(e => $"  line {e.Line}, col {e.Column}: {e.Message}")));
    }

    [Fact]
    public void InCodeAutoProvisionDdlParsesClean()
    {
        Parse(SqlExecutor.SchemaDdl, out var errors);
        Assert.True(errors.Count == 0,
            "SqlExecutor.SchemaDdl has T-SQL syntax errors:\n"
            + string.Join("\n", errors.Select(e => $"  line {e.Line}: {e.Message}")));
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

        // The script must contain a meaningful amount of DDL — an empty parse
        // would vacuously pass the guards above.
        Assert.True(all.Found.Count >= 9,
            $"only {all.Found.Count} DDL statements found — extraction broken?");
    }

    [Fact]
    public void InCodeAutoProvisionDdlIsIdempotentByConstruction()
    {
        var fragment = Parse(SqlExecutor.SchemaDdl, out var errors);
        Assert.Empty(errors);
        var all = new GuardableCollector();
        fragment.Accept(all);
        var guarded = new GuardedCollector();
        fragment.Accept(guarded);
        var unguarded = all.Found.Where(s => !guarded.Guarded.Contains(s)).ToList();
        Assert.True(unguarded.Count == 0,
            "SqlExecutor.SchemaDdl statements not wrapped in an existence guard:\n"
            + string.Join("\n", unguarded.Select(s => $"  line {s.StartLine}: {s.GetType().Name}")));
    }

    // ── 3. code DDL ⇄ script drift ──────────────────────────────────────────

    [Fact]
    public void InCodeDdlAndScriptAgreeOnTableShapes()
    {
        var scriptTables = ExtractTables(SqlcmdPreprocess(File.ReadAllText(ScriptPath)));
        var codeTables = ExtractTables(SqlExecutor.SchemaDdl);

        var missingFromScript = codeTables.Keys.Except(scriptTables.Keys, StringComparer.OrdinalIgnoreCase).ToList();
        var missingFromCode = scriptTables.Keys.Except(codeTables.Keys, StringComparer.OrdinalIgnoreCase).ToList();
        Assert.True(missingFromScript.Count == 0,
            "tables in SqlExecutor.SchemaDdl but not in create-database.sql: "
            + string.Join(", ", missingFromScript));
        Assert.True(missingFromCode.Count == 0,
            "tables in create-database.sql but not in SqlExecutor.SchemaDdl: "
            + string.Join(", ", missingFromCode));

        foreach (var (table, codeColumns) in codeTables)
        {
            var scriptColumns = scriptTables[table];
            Assert.True(codeColumns.SetEquals(scriptColumns),
                $"table {table} column drift — code: [{string.Join(",", codeColumns.OrderBy(c => c))}] "
                + $"script: [{string.Join(",", scriptColumns.OrderBy(c => c))}]");
        }
    }

    // ── 4. contract doc ⇄ script drift ──────────────────────────────────────

    [Fact]
    public void ContractDocAndScriptAgreeOnTableSet()
    {
        var scriptTables = ExtractTables(SqlcmdPreprocess(File.ReadAllText(ScriptPath)))
            .Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var contract = File.ReadAllText(ContractPath);
        var contractTables = Regex.Matches(contract, @"dbo\.(\w+)")
            .Select(m => m.Groups[1].Value)
            .Where(name => !name.StartsWith("usp_", StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var undocumented = scriptTables.Except(contractTables, StringComparer.OrdinalIgnoreCase).ToList();
        var phantom = contractTables.Except(scriptTables, StringComparer.OrdinalIgnoreCase).ToList();
        Assert.True(undocumented.Count == 0,
            "tables in the script but missing from docs/SQL_CONTRACT.md: " + string.Join(", ", undocumented));
        Assert.True(phantom.Count == 0,
            "tables documented in docs/SQL_CONTRACT.md but absent from the script: " + string.Join(", ", phantom));
    }

    // ── 5. DacFx semantic model ──────────────────────────────────────────────

    /// <summary>
    /// Re-script each guarded CREATE as a bare declarative statement (dacpac
    /// models take unguarded CREATEs) — CREATE DATABASE / USE are omitted.
    /// </summary>
    private static List<string> DeclarativeSchemaChunks()
    {
        var fragment = ParseScript(out var errors);
        Assert.Empty(errors);
        var guardables = new GuardableCollector();
        fragment.Accept(guardables);

        var generator = new Sql150ScriptGenerator();
        var chunks = new List<string>();
        foreach (var statement in guardables.Found)
        {
            if (statement is CreateDatabaseStatement)
                continue;
            generator.GenerateScript(statement, out var sql);
            chunks.Add(sql);
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
