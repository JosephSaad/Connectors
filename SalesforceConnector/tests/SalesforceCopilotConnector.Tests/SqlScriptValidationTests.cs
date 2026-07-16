// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.Dac;
using Microsoft.SqlServer.Dac.Model;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SalesforceCopilotConnector.Tests;

/// <summary>
/// Static validation of <c>scripts/sql/create-database.sql</c> without a live SQL Server.
///
/// The SQL backend only runs when <c>USE_SQL_SERVER=true</c>, and the live integration
/// tests self-skip unless <c>SQLSERVER_TEST_CONNECTION_STRING</c> is set — so on a
/// machine with no SQL Server the entire DDL surface would otherwise ship unexecuted.
/// These tests close most of that gap offline:
///
///  1. the script parses cleanly under the real SQL Server 2019 grammar
///     (<see cref="TSql150Parser"/> is the actual parser frontend, not a lint);
///  2. every DDL statement is idempotent by construction (guarded CREATEs,
///     CREATE OR ALTER) — the re-run safety the script header promises;
///  3. the procedure set matches docs/SQL_CONTRACT.md exactly, both directions;
///  4. every C# stored-procedure call site names an existing procedure, passes only
///     declared parameters, and supplies every parameter that has no default — the
///     "wrong @Param name" class of bug that otherwise only a live server reports;
///  5. a DacFx semantic model (the offline engine behind SQL database projects)
///     builds and validates the script — unresolved tables/columns inside
///     procedure and view bodies fail here.
///
/// Read-only over repo files; no env vars or process-global seams touched.
/// </summary>
public class SqlScriptValidationTests
{
    // ── repo file access ────────────────────────────────────────────────────────

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && dir.GetFiles("SalesforceCopilotConnector.sln").Length == 0)
            dir = dir.Parent;
        Assert.True(dir != null, "could not locate repo root (SalesforceCopilotConnector.sln) above " + AppContext.BaseDirectory);
        return dir!.FullName;
    }

    private static string ScriptPath => Path.Combine(RepoRoot(), "scripts", "sql", "create-database.sql");
    private static string ContractPath => Path.Combine(RepoRoot(), "docs", "SQL_CONTRACT.md");
    private static string SrcRoot => Path.Combine(RepoRoot(), "src", "SalesforceCopilotConnector");

    /// <summary>
    /// Apply the same preprocessing sqlcmd does before the server sees the script:
    /// collect <c>:setvar NAME VALUE</c> defaults, strip sqlcmd directives
    /// (lines starting with <c>:</c>), and substitute <c>$(NAME)</c> references.
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
                sb.Append('\n'); // keep line numbers aligned with the file
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

    /// <summary>Collects every node of the DDL statement types that must be guarded.</summary>
    private sealed class GuardableCollector : TSqlFragmentVisitor
    {
        public List<TSqlStatement> Found { get; } = new();
        public override void ExplicitVisit(CreateTableStatement node) { Found.Add(node); base.ExplicitVisit(node); }
        public override void ExplicitVisit(CreateIndexStatement node) { Found.Add(node); base.ExplicitVisit(node); }
        public override void ExplicitVisit(AlterTableAddTableElementStatement node) { Found.Add(node); base.ExplicitVisit(node); }
        public override void ExplicitVisit(CreateDatabaseStatement node) { Found.Add(node); base.ExplicitVisit(node); }
    }

    /// <summary>Marks guardable statements that sit inside the THEN branch of an IF.</summary>
    private sealed class GuardedCollector : TSqlFragmentVisitor
    {
        public HashSet<TSqlStatement> Guarded { get; } = new();
        public override void ExplicitVisit(IfStatement node)
        {
            var inner = new GuardableCollector();
            node.ThenStatement?.Accept(inner);
            foreach (var s in inner.Found) Guarded.Add(s);
            base.ExplicitVisit(node);
        }
    }

    private sealed class ModuleCollector : TSqlFragmentVisitor
    {
        public Dictionary<string, List<ProcedureParameter>> Procedures { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<TSqlStatement> Modules { get; } = new();
        public List<TSqlStatement> NonIdempotentModules { get; } = new();

        public override void ExplicitVisit(CreateOrAlterProcedureStatement node)
        {
            Procedures[node.ProcedureReference.Name.BaseIdentifier.Value] = node.Parameters.ToList();
            Modules.Add(node);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(CreateOrAlterViewStatement node)
        {
            Modules.Add(node);
            base.ExplicitVisit(node);
        }

        // Plain CREATE (no OR ALTER) breaks re-runs — the contract requires OR ALTER.
        public override void ExplicitVisit(CreateProcedureStatement node) { NonIdempotentModules.Add(node); base.ExplicitVisit(node); }
        public override void ExplicitVisit(CreateViewStatement node) { NonIdempotentModules.Add(node); base.ExplicitVisit(node); }
        public override void ExplicitVisit(CreateFunctionStatement node) { NonIdempotentModules.Add(node); base.ExplicitVisit(node); }
        public override void ExplicitVisit(CreateTriggerStatement node) { NonIdempotentModules.Add(node); base.ExplicitVisit(node); }
    }

    // ── C# call-site extraction ────────────────────────────────────────────────

    private sealed record CallSite(string File, int Line, string Procedure, HashSet<string> Parameters);

    /// <summary>
    /// Scan all C# under src/ for stored-procedure call sites. Both shapes are
    /// uniform in this codebase: a <c>"dbo.usp_X"</c> literal (via <c>Proc(...)</c>
    /// or a <c>CommandText =</c> assignment) followed by straight-line
    /// <c>Parameters.AddWithValue("@Y", ...)</c> / <c>Parameters.Add("@Y", ...)</c> /
    /// <c>Parameters.Add(new SqlParameter("@Y", ...))</c> lines. A method
    /// declaration ends the current site so parameters never leak across methods.
    /// </summary>
    private static List<CallSite> ExtractCallSites()
    {
        var procLiteral = new Regex("\"dbo\\.(usp_\\w+)\"");
        var paramAdd = new Regex("Parameters\\.\\w+\\s*\\(\\s*(?:new\\s+SqlParameter\\s*\\(\\s*)?\"(@\\w+)\"");
        var methodDecl = new Regex("^\\s*(public|private|internal|protected)[^=]*\\(");

        var sites = new List<CallSite>();
        foreach (var file in Directory.EnumerateFiles(SrcRoot, "*.cs", SearchOption.AllDirectories).OrderBy(f => f))
        {
            CallSite? current = null;
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (methodDecl.IsMatch(lines[i])) current = null;

                var proc = procLiteral.Match(lines[i]);
                if (proc.Success)
                {
                    current = new CallSite(Path.GetFileName(file), i + 1, proc.Groups[1].Value,
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                    sites.Add(current);
                    continue;
                }

                var param = paramAdd.Match(lines[i]);
                if (param.Success) current?.Parameters.Add(param.Groups[1].Value);
            }
        }
        return sites;
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

        var modules = new ModuleCollector();
        fragment.Accept(modules);
        Assert.True(modules.NonIdempotentModules.Count == 0,
            "modules using plain CREATE instead of CREATE OR ALTER:\n" +
            string.Join("\n", modules.NonIdempotentModules.Select(s => $"  line {s.StartLine}: {s.GetType().Name}")));

        // The script must contain a meaningful amount of DDL — an empty parse would
        // vacuously pass the guards above.
        Assert.True(all.Found.Count >= 10, $"only {all.Found.Count} DDL statements found — extraction broken?");
        Assert.True(modules.Procedures.Count >= 25, $"only {modules.Procedures.Count} procedures found — extraction broken?");
    }

    // ── 3. contract doc ⇄ script drift ──────────────────────────────────────────

    [Fact]
    public void ContractDocAndScriptAgreeOnProcedureSet()
    {
        var fragment = ParseScript(out _);
        var modules = new ModuleCollector();
        fragment.Accept(modules);
        var inScript = new HashSet<string>(modules.Procedures.Keys, StringComparer.OrdinalIgnoreCase);

        var inContract = Regex.Matches(File.ReadAllText(ContractPath), "usp_\\w+")
            .Select(m => m.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = inContract.Except(inScript, StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
        var undocumented = inScript.Except(inContract, StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();

        Assert.True(missing.Count == 0,
            "procedures named in docs/SQL_CONTRACT.md but missing from create-database.sql: " + string.Join(", ", missing));
        Assert.True(undocumented.Count == 0,
            "procedures in create-database.sql but absent from docs/SQL_CONTRACT.md: " + string.Join(", ", undocumented));
    }

    // ── 4. C# call sites ⇄ procedure signatures ────────────────────────────────

    [Fact]
    public void CSharpCallSitesMatchProcedureSignatures()
    {
        var fragment = ParseScript(out _);
        var modules = new ModuleCollector();
        fragment.Accept(modules);

        var sites = ExtractCallSites();
        Assert.True(sites.Count >= 25, $"only {sites.Count} call sites extracted — extractor broken?");

        var failures = new List<string>();
        foreach (var site in sites)
        {
            if (!modules.Procedures.TryGetValue(site.Procedure, out var declared))
            {
                failures.Add($"{site.File}:{site.Line} calls {site.Procedure}, which create-database.sql does not define");
                continue;
            }

            var declaredNames = declared.Select(p => p.VariableName.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var passed in site.Parameters.Where(p => !declaredNames.Contains(p)).OrderBy(p => p))
                failures.Add($"{site.File}:{site.Line} passes {passed}, but {site.Procedure} has no such parameter " +
                             $"(declared: {string.Join(", ", declaredNames.OrderBy(n => n))})");

            // Value == null means the parameter has no DEFAULT — the server rejects
            // any call that omits it, so every call site must supply it.
            foreach (var required in declared.Where(p => p.Value == null).Select(p => p.VariableName.Value))
                if (!site.Parameters.Contains(required))
                    failures.Add($"{site.File}:{site.Line} calls {site.Procedure} without required parameter {required}");
        }

        Assert.True(failures.Count == 0,
            "C# call sites disagree with create-database.sql procedure signatures:\n  " + string.Join("\n  ", failures));
    }

    // ── 5. DacFx semantic model (offline column/table reference binding) ───────

    /// <summary>
    /// A DacFx model is declarative — it cannot hold the script's imperative
    /// idempotency wrappers (IF guards, migration ALTER ADDs). Reduce the script to
    /// the schema a fresh deployment produces: unwrap guarded CREATEs, fold each
    /// migration <c>ALTER TABLE ... ADD</c> column into its CREATE TABLE, and turn
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

        var addedColumns = guardables.Found.OfType<AlterTableAddTableElementStatement>()
            .ToLookup(a => a.SchemaObjectName.BaseIdentifier.Value, StringComparer.OrdinalIgnoreCase);

        var generator = new Sql150ScriptGenerator();
        var chunks = new List<string>();

        foreach (var table in guardables.Found.OfType<CreateTableStatement>())
        {
            foreach (var alter in addedColumns[table.SchemaObjectName.BaseIdentifier.Value])
            {
                foreach (var col in alter.Definition.ColumnDefinitions)
                    if (!table.Definition.ColumnDefinitions.Any(c =>
                            string.Equals(c.ColumnIdentifier.Value, col.ColumnIdentifier.Value, StringComparison.OrdinalIgnoreCase)))
                        table.Definition.ColumnDefinitions.Add(col);
            }
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
            // dacpac models take CREATE, not CREATE OR ALTER — same object either way.
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
            try { model.AddObjects(chunk); }
            catch (DacModelException ex)
            {
                Assert.Fail($"DacFx rejected schema chunk:\n{chunk}\n---\n{ex.Message}");
            }
        }

        // Unresolved references inside proc/view bodies surface as warnings from
        // TSqlModel.Validate — they are the typo'd-column bugs this test exists to
        // catch, so treat them as failures alongside real errors. sp_getapplock and
        // friends live in master and are legitimately outside the model.
        var problems = model.Validate()
            .Where(m => m.MessageType == DacMessageType.Error
                        || m.Message.Contains("unresolved reference", StringComparison.OrdinalIgnoreCase))
            .Where(m => !m.Message.Contains("sp_getapplock", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(problems.Count == 0,
            "DacFx semantic validation problems (unresolved columns/tables, invalid constructs):\n" +
            string.Join("\n", problems.Select(e => $"  {e.MessageType} {e.Prefix}{e.Number} [{e.ElementType}]: {e.Message}")));
    }
}
