// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// AclScaleGuardTests.cs — #9 large-group ACL scale guard.

using SalesforceCopilotConnector.Graph;

namespace SalesforceCopilotConnector.Tests.TestGraph;

[Collection("EnvVars")]
public sealed class AclScaleGuardTests : IDisposable
{
    private readonly string?[] _saved =
    {
        Environment.GetEnvironmentVariable(AclScaleGuard.ThresholdEnvVar),
        Environment.GetEnvironmentVariable(AclScaleGuard.ActionEnvVar),
        Environment.GetEnvironmentVariable(AclScaleGuard.GroupEnvVar),
    };

    public AclScaleGuardTests() => ClearAll();

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AclScaleGuard.ThresholdEnvVar, _saved[0]);
        Environment.SetEnvironmentVariable(AclScaleGuard.ActionEnvVar, _saved[1]);
        Environment.SetEnvironmentVariable(AclScaleGuard.GroupEnvVar, _saved[2]);
    }

    private static void ClearAll()
    {
        Environment.SetEnvironmentVariable(AclScaleGuard.ThresholdEnvVar, null);
        Environment.SetEnvironmentVariable(AclScaleGuard.ActionEnvVar, null);
        Environment.SetEnvironmentVariable(AclScaleGuard.GroupEnvVar, null);
    }

    private static List<Dictionary<string, string>> UserAcl(int n)
    {
        var acl = new List<Dictionary<string, string>>(n);
        for (var i = 0; i < n; i++)
            acl.Add(new() { ["accessType"] = "grant", ["type"] = "user", ["value"] = $"user-{i}" });
        return acl;
    }

    [Fact]
    public void DisabledByDefault_NeverFires_ReportsAceCount()
    {
        var policy = AclScaleGuard.Policy.Read();
        Assert.False(policy.Enabled);

        var decision = AclScaleGuard.Apply(policy, "001", "Account", UserAcl(5000));

        Assert.False(decision.Fired);
        Assert.Equal(5000, decision.AceCount);   // per-item ACE count still observed
    }

    [Fact]
    public void WithinThreshold_DoesNotFire()
    {
        Environment.SetEnvironmentVariable(AclScaleGuard.ThresholdEnvVar, "100");
        var decision = AclScaleGuard.Apply(AclScaleGuard.Policy.Read(), "001", "Account", UserAcl(100));
        Assert.False(decision.Fired);  // count == threshold is not "exceeds"
    }

    [Fact]
    public void OverThreshold_WarnAction_KeepsAclButFires()
    {
        Environment.SetEnvironmentVariable(AclScaleGuard.ThresholdEnvVar, "100");
        // default action = warn
        var acl = UserAcl(250);
        var decision = AclScaleGuard.Apply(AclScaleGuard.Policy.Read(), "001", "Account", acl);

        Assert.True(decision.Fired);
        Assert.Equal(AclScaleGuardActions.Warn, decision.Action);
        Assert.Equal(250, decision.AceCount);
        Assert.Same(acl, decision.Acl);   // warn does not alter the ACL
    }

    [Fact]
    public void OverThreshold_GroupAction_CollapsesToSingleGroupAce()
    {
        Environment.SetEnvironmentVariable(AclScaleGuard.ThresholdEnvVar, "100");
        Environment.SetEnvironmentVariable(AclScaleGuard.ActionEnvVar, "group");
        Environment.SetEnvironmentVariable(AclScaleGuard.GroupEnvVar, "grp-1234");

        var decision = AclScaleGuard.Apply(AclScaleGuard.Policy.Read(), "001", "Account", UserAcl(2500));

        Assert.True(decision.Fired);
        Assert.Equal(AclScaleGuardActions.Group, decision.Action);
        var ace = Assert.Single(decision.Acl!);
        Assert.Equal("grant", ace["accessType"]);
        Assert.Equal("group", ace["type"]);
        Assert.Equal("grp-1234", ace["value"]);
    }

    [Fact]
    public void OverThreshold_GroupActionWithoutGroup_FallsBackToWarn()
    {
        Environment.SetEnvironmentVariable(AclScaleGuard.ThresholdEnvVar, "100");
        Environment.SetEnvironmentVariable(AclScaleGuard.ActionEnvVar, "group");
        // no ACL_SCALE_GUARD_GROUP configured
        var acl = UserAcl(200);
        var decision = AclScaleGuard.Apply(AclScaleGuard.Policy.Read(), "001", "Account", acl);

        Assert.True(decision.Fired);
        Assert.Equal(AclScaleGuardActions.Warn, decision.Action);
        Assert.Same(acl, decision.Acl);
    }

    [Fact]
    public void OverThreshold_FallbackAction_DeniesEveryone()
    {
        Environment.SetEnvironmentVariable(AclScaleGuard.ThresholdEnvVar, "100");
        Environment.SetEnvironmentVariable(AclScaleGuard.ActionEnvVar, "fallback");

        var decision = AclScaleGuard.Apply(AclScaleGuard.Policy.Read(), "001", "Account", UserAcl(150));

        Assert.True(decision.Fired);
        Assert.Equal(AclScaleGuardActions.Fallback, decision.Action);
        var ace = Assert.Single(decision.Acl!);
        Assert.Equal("deny", ace["accessType"]);
        Assert.Equal("everyone", ace["type"]);
    }
}
