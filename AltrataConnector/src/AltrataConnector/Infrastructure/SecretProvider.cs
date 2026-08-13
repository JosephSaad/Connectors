// Infrastructure/SecretProvider.cs
// --------------------------------
// SECRET_* resolution, delegated to the shared chassis.
//
// The chassis owns the mechanism (environment by default; Azure Key Vault when
// USE_KEY_VAULT is truthy, with the env name lowered and '_' → '-' to form the
// vault secret name). What stays here is the seam: this connector resolves
// secrets through an injectable ISecretProvider so Settings.Load can be given a
// fake, and the chassis exposes a static. The adapter below is that bridge and
// nothing more.
//
// TWO BEHAVIOUR CHANGES came with the move, both deliberate:
//
//  1. PRECEDENCE IS INVERTED. The old local implementation always read the
//     environment first and only consulted the vault when the variable was
//     empty, so a value on the host silently beat the vault. The chassis makes
//     the vault authoritative once USE_KEY_VAULT is on, falling back to the
//     environment only when a fetch fails. That loses a local-override
//     convenience and gains the property a regulated deployment needs: a stray
//     or stale environment variable on a node cannot shadow the vault.
//
//  2. A MISSING KEY_VAULT_URI NOW FAILS FAST. It used to log a warning and
//     return null, so a misconfigured node started up and failed later, opaquely,
//     on whatever first needed a secret. The chassis throws while configuration
//     is being read, naming the variable.
//
// The move also fixes a real defect. The old cache stored every outcome
// including nulls, so one transient Key Vault failure pinned "no secret" for the
// lifetime of the process — fatal in --continuous and service mode, where the
// next cycle should simply retry. The chassis caches successful fetches only.

namespace AltrataConnector.Infrastructure;

public interface ISecretProvider
{
    /// <summary>Resolve a SECRET_* variable; null when not found anywhere.</summary>
    string? Get(string envName);
}

/// <summary>
/// Adapts the chassis's static <c>SecretProvider.GetSecret</c> to this
/// connector's injectable <see cref="ISecretProvider"/>.
/// </summary>
public sealed class ChassisSecretProvider : ISecretProvider
{
    public string? Get(string envName) => Connector.Chassis.SecretProvider.GetSecret(envName);
}
