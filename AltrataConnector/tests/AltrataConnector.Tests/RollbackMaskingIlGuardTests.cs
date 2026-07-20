// RollbackMaskingIlGuardTests.cs
// ------------------------------
// The guard against the rollback-after-commit masking defect, moved off SOURCE
// TEXT and onto the COMPILED ARTEFACT.
//
// WHAT THE DEFECT IS. SqlStateStore's two transactional dead-letter write paths
// (AddDeadLetters, MutateDeadLetters) once wrapped their body in
// `catch { txn.Rollback(); throw; }`. When the fault came from txn.Commit(),
// SqlTransaction.Rollback threw InvalidOperationException on the completed
// transaction and REPLACED the SqlException. SqlStateStore.Execute's two
// handlers both filter on `catch (SqlException)`, so the masked exception
// matched NEITHER: ShouldRetry was never consulted, a genuinely uncommitted
// batch was never retried, and the dead letters were LOST rather than
// duplicated. The shipped shape is `using var txn` with no catch — Dispose
// rolls back an uncommitted transaction and is a documented no-op on a
// completed one, so the original exception reaches the executor intact.
//
// WHY THIS FILE EXISTS. The previous sole guard was a regex over SqlStateStore.cs
// source text — @"^\s*\w+\.Rollback\(\)" with RegexOptions.Multiline. It matches
// only a Rollback call that begins a line, so it tested LAYOUT, not behaviour.
// Reinstating the exact defect with the catch collapsed onto one line —
//     } catch { txn.Rollback(); throw; }
// — left the FULL suite green. Measured on this tree BEFORE this file existed:
// Failed: 0, Passed: 724, Skipped: 0, Total: 724, with that defect in place.
// The five-line formatting of the identical code did trip it. That is a guard
// keyed to whitespace.
//
// WHAT THIS FILE ASSERTS INSTEAD. Both checks run against the IL that the C#
// compiler emitted into AltrataConnector.dll, which carries no whitespace,
// comments or line breaks at all. Any formatting of the defect compiles to the
// same IL, so no formatting can evade these:
//
//   1. NoCompiledMethodInTheSqlStoreCallsRollback — an exhaustive scan of every
//      IL byte offset of every method compiled into SqlStateStore (including
//      every compiler-generated lambda body) for a call/callvirt to a method
//      named Rollback.
//   2. TheAddDeadLettersWritePathCompilesWithNoCatchHandler /
//      TheMutateDeadLettersWritePathCompilesWithNoCatchHandler — per path, one
//      test each, asserting the compiled body carries zero catch or filter
//      exception-handling clauses (`using var txn` emits only Finally).
//
// HONEST STATEMENT OF WHAT IS *NOT* COVERED. These are assertions about
// compiled code, NOT about runtime behaviour. Nothing here executes
// SqlStateStore.AddDeadLetters or SqlStateStore.MutateDeadLetters, and no test
// in this suite does: both go through Execute, which opens a
// Microsoft.Data.SqlClient SqlConnection, and there is no SQL Server on this
// host and no container runtime (docker/colima/podman) to start one.
// SqlException cannot be constructed by a test either. A behavioural test that
// drives the real bodies through a real commit failure is therefore NOT
// possible here and is NOT claimed. What is claimed is narrower and true: the
// defect cannot be reintroduced into these two paths, in any formatting,
// without one of these tests going red.
//
// The runtime MECHANISM — that Rollback-after-Commit throws and that the throw
// replaces the original exception — is separately demonstrated against a real
// ADO.NET provider (Microsoft.Data.Sqlite) by
// StateContractTests.cs / TransactionRollbackMaskingTests.

using System.Reflection;
using AltrataConnector.State;

namespace AltrataConnector.Tests;

public class RollbackMaskingIlGuardTests
{
    // ---- the scanner ------------------------------------------------------

    /// <summary>Every method compiled into SqlStateStore: its own members plus
    /// the nested types the compiler generates for closures, whose lambda
    /// bodies are where the transactional write paths actually live.</summary>
    private static IEnumerable<MethodBase> CompiledMethodsOf(Type type)
    {
        const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic |
                                   BindingFlags.Instance | BindingFlags.Static |
                                   BindingFlags.DeclaredOnly;

        var types = new List<Type> { type };
        types.AddRange(type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic));

        foreach (var candidate in types)
        {
            var declaring = candidate.IsGenericTypeDefinition ? candidate : candidate;
            foreach (var method in declaring.GetMethods(Flags))
                yield return method;
            foreach (var ctor in declaring.GetConstructors(Flags))
                yield return ctor;
        }
    }

    /// <summary>The compiler-generated body of a lambda declared inside
    /// <paramref name="sourceMethodName"/>. Roslyn names it
    /// <c>&lt;SourceMethodName&gt;b__N_M</c> and hoists it either onto the
    /// declaring type or onto a <c>&lt;&gt;c__DisplayClassNN_0</c> closure
    /// class, so both are searched.
    ///
    /// This resolution is asserted to find EXACTLY ONE method. A rename or a
    /// refactor that moved the body would otherwise leave the per-path tests
    /// below scanning nothing and passing vacuously.</summary>
    private static MethodBase LambdaBodyOf(Type type, string sourceMethodName)
    {
        var marker = "<" + sourceMethodName + ">b__";
        var matches = CompiledMethodsOf(type)
            .Where(m => m.Name.StartsWith(marker, StringComparison.Ordinal))
            .ToList();

        Assert.True(matches.Count == 1,
            $"expected exactly 1 compiled lambda body for {type.Name}.{sourceMethodName}, " +
            $"found {matches.Count} ({string.Join(", ", matches.Select(m => m.DeclaringType!.Name + "." + m.Name))}). " +
            "The scan anchor is broken — fix it rather than deleting the assertion, " +
            "or the rollback-masking guard silently covers nothing.");

        return matches[0];
    }

    /// <summary>IL opcodes that carry a 4-byte method token: call (0x28) and
    /// callvirt (0x6F). A `txn.Rollback()` compiles to one of these.</summary>
    private const byte OpCall = 0x28;
    private const byte OpCallvirt = 0x6F;

    /// <summary>Names every method a method's IL might call, found by testing
    /// EVERY byte offset in the body — not sampled, not aligned to instruction
    /// boundaries, not restricted to any subset of offsets.
    ///
    /// The direction of that exhaustiveness is what makes this sound. A real
    /// call site is always the 5-byte sequence {opcode, token[4]} at SOME
    /// offset, and every offset is tested, so a call to Rollback CANNOT be
    /// missed — there are no false negatives by construction. The cost is that
    /// operand bytes of unrelated instructions can coincidentally resolve to a
    /// method token, i.e. false POSITIVES are possible. On the clean tree the
    /// scan reports zero Rollback hits across all of SqlStateStore, so no such
    /// coincidence exists today; were one to appear it would fail the test
    /// loudly rather than hide a defect.</summary>
    private static List<string> RollbackCallsIn(MethodBase method)
    {
        var hits = new List<string>();
        var body = method.GetMethodBody();
        var il = body?.GetILAsByteArray();
        if (il == null)
            return hits;

        var module = method.Module;
        var typeArgs = method.DeclaringType is { IsGenericType: true } dt
            ? dt.GetGenericArguments() : null;
        var methodArgs = method.IsGenericMethodDefinition ? method.GetGenericArguments() : null;

        for (var offset = 0; offset + 4 < il.Length; offset++)
        {
            if (il[offset] != OpCall && il[offset] != OpCallvirt)
                continue;

            var token = BitConverter.ToInt32(il, offset + 1);
            MethodBase? target;
            try
            {
                target = module.ResolveMethod(token, typeArgs, methodArgs);
            }
            catch
            {
                continue;   // not a method token at this offset
            }

            if (target != null && target.Name == "Rollback")
                hits.Add($"{method.DeclaringType!.Name}.{method.Name} calls " +
                         $"{target.DeclaringType?.Name}.{target.Name} at IL offset {offset}");
        }

        return hits;
    }

    // ---- the scanner is not vacuous ---------------------------------------

    /// <summary>A decoy carrying the EXACT defect shape, in the EXACT one-line
    /// formatting that evaded the old regex, compiled into this test assembly.
    /// It exists so <see cref="TheScannerDetectsTheDefectInItsOneLineForm"/>
    /// can prove the scanner reports a hit when a hit is there. Without this,
    /// a scanner that always returned an empty list would pass every other test
    /// in this file.</summary>
    private static void DecoyCarryingTheDefect(Microsoft.Data.Sqlite.SqliteConnection conn)
    {
        using var txn = conn.BeginTransaction();
        try { txn.Commit(); } catch { txn.Rollback(); throw; }
    }

    /// <summary>A Rollback reached by the OTHER opcode. DbTransaction.Rollback
    /// is virtual, so <see cref="DecoyCarryingTheDefect"/> only ever exercises
    /// the callvirt (0x6F) arm of the opcode clause — deleting the call (0x28)
    /// arm survived a mutation run against that decoy alone. A method invoked
    /// on a STRUCT instance compiles to `call`, so this decoy covers the second
    /// arm, and the two clauses are then covered one each rather than
    /// collectively.</summary>
    private struct DecoyValueTransaction
    {
        public int Touched;
        public void Rollback() => Touched++;
    }

    private static int DecoyWithANonVirtualRollback()
    {
        var txn = new DecoyValueTransaction();
        txn.Rollback();
        return txn.Touched;
    }

    [Theory]
    [InlineData(nameof(DecoyCarryingTheDefect), OpCallvirt)]
    [InlineData(nameof(DecoyWithANonVirtualRollback), OpCall)]
    public void TheScannerDetectsARollbackReachedByEitherCallOpcode(string decoyName, byte expectedOpcode)
    {
        var decoy = typeof(RollbackMaskingIlGuardTests).GetMethod(
            decoyName, BindingFlags.NonPublic | BindingFlags.Static)!;

        // The theory rows are only meaningful if each decoy really does compile
        // to the opcode it claims; assert that rather than trusting the label.
        var il = decoy.GetMethodBody()!.GetILAsByteArray()!;
        Assert.Contains(expectedOpcode, il);

        var hits = RollbackCallsIn(decoy);
        Assert.True(hits.Count >= 1,
            $"the IL scanner found no Rollback call in {decoyName}, which provably contains one " +
            $"reached by opcode 0x{expectedOpcode:X2} — the scanner is broken and the guards in " +
            "this file are passing vacuously.");
    }

    [Fact]
    public void TheScannerDetectsTheCatchOfTheDefectInItsOneLineForm()
    {
        // The second detector: the decoy's one-line catch is visible as a catch
        // clause in the compiled body, whatever the source layout.
        var decoy = typeof(RollbackMaskingIlGuardTests).GetMethod(
            nameof(DecoyCarryingTheDefect),
            BindingFlags.NonPublic | BindingFlags.Static)!;

        var clauses = decoy.GetMethodBody()!.ExceptionHandlingClauses;
        Assert.Contains(clauses, c => c.Flags == ExceptionHandlingClauseOptions.Clause);
    }

    // ---- the guards -------------------------------------------------------

    [Fact]
    public void NoCompiledMethodInTheSqlStoreCallsRollback()
    {
        var hits = CompiledMethodsOf(typeof(SqlStateStore))
            .SelectMany(RollbackCallsIn)
            .ToList();

        Assert.True(hits.Count == 0,
            $"SqlStateStore's COMPILED code calls Rollback() {hits.Count} time(s): " +
            string.Join("; ", hits) + ". " +
            "SqlTransaction.Rollback throws InvalidOperationException on a completed or broken " +
            "transaction and REPLACES the SqlException that Execute's two handlers filter on, so " +
            "ShouldRetry is never consulted and an uncommitted batch is lost rather than retried. " +
            "Use `using var txn` — Dispose rolls back an uncommitted transaction and is a no-op " +
            "on a completed one.");
    }

    [Fact]
    public void TheAddDeadLettersWritePathCompilesWithNoCatchHandler()
    {
        AssertNoCatchHandler(nameof(SqlStateStore.AddDeadLetters));
    }

    [Fact]
    public void TheMutateDeadLettersWritePathCompilesWithNoCatchHandler()
    {
        AssertNoCatchHandler(nameof(SqlStateStore.MutateDeadLetters));
    }

    /// <summary>Per-path, one clause at a time: the compiled transactional body
    /// must carry no catch and no exception filter. `using var txn` emits only
    /// Finally clauses; ANY catch around a transactional body is the shape that
    /// can replace the exception on its way to the executor.</summary>
    private static void AssertNoCatchHandler(string sourceMethodName)
    {
        var lambda = LambdaBodyOf(typeof(SqlStateStore), sourceMethodName);
        var clauses = lambda.GetMethodBody()!.ExceptionHandlingClauses;

        var catches = clauses.Count(c => c.Flags == ExceptionHandlingClauseOptions.Clause);
        var filters = clauses.Count(c => c.Flags == ExceptionHandlingClauseOptions.Filter);

        Assert.True(catches == 0,
            $"SqlStateStore.{sourceMethodName}'s transactional body compiles to {catches} catch " +
            "handler(s). A catch around a transactional body can replace the SqlException that " +
            "Execute filters on — the rollback-after-commit masking defect. Use `using var txn` " +
            "with no catch.");

        Assert.True(filters == 0,
            $"SqlStateStore.{sourceMethodName}'s transactional body compiles to {filters} exception " +
            "filter(s), which is the same hazard written as `catch when (...)`.");

        // Not vacuous in the other direction either: the body really does open
        // a transaction under `using`, which is what emits the Finally that
        // makes dropping the catch safe.
        Assert.Contains(clauses, c => c.Flags == ExceptionHandlingClauseOptions.Finally);
    }
}
