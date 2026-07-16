// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Tests for acl_engine.group_id_formats — External group ID format constants.
// Port of tests/test_acl_engine/test_group_id_formats.py.

using SalesforceCopilotConnector.AclEngine;

namespace SalesforceCopilotConnector.Tests.TestAclEngine;

/// <summary>Verify format strings produce correct group IDs.</summary>
public class GroupIdFormatsTests
{
    [Fact]
    public void TopLevel()
    {
        Assert.Equal("AccountTopLevel", SfGroupIdFormats.TopLevel.Format("Account"));
    }

    [Fact]
    public void GlobalUsers()
    {
        Assert.Equal("LeadGlobalUsers", SfGroupIdFormats.GlobalUsers.Format("Lead"));
    }

    [Fact]
    public void AllInternalUsers()
    {
        Assert.Equal("CaseAllInternalUsers", SfGroupIdFormats.AllInternalUsers.Format("Case"));
    }

    [Fact]
    public void Role()
    {
        Assert.Equal("Account00E123Role", SfGroupIdFormats.Role.Format("Account", "00E123"));
    }

    [Fact]
    public void RoleAndSubordinates()
    {
        Assert.Equal(
            "Account00E123RoleAndSubordinates",
            SfGroupIdFormats.RoleAndSubordinates.Format("Account", "00E123"));
    }

    [Fact]
    public void RoleNoParents()
    {
        Assert.Equal("Account00E123RoleNoParents", SfGroupIdFormats.RoleNoParents.Format("Account", "00E123"));
    }

    [Fact]
    public void RoleAndSubordinatesNoParents()
    {
        Assert.Equal(
            "Account00E123RoleAndSubordinatesNoParents",
            SfGroupIdFormats.RoleAndSubordinatesNoParents.Format("Account", "00E123"));
    }

    [Fact]
    public void PublicGroup()
    {
        Assert.Equal("Account00G456PublicGroup", SfGroupIdFormats.PublicGroup.Format("Account", "00G456"));
    }

    [Fact]
    public void Manager()
    {
        Assert.Equal("Opportunity005789Manager", SfGroupIdFormats.Manager.Format("Opportunity", "005789"));
    }

    [Fact]
    public void ManagerAndSubordinates()
    {
        Assert.Equal(
            "Opportunity005789ManagerAndSubordinates",
            SfGroupIdFormats.ManagerAndSubordinates.Format("Opportunity", "005789"));
    }

    [Fact]
    public void Territory()
    {
        Assert.Equal("Account0ML123Territory", SfGroupIdFormats.Territory.Format("Account", "0ML123"));
    }

    [Fact]
    public void TerritoryAndSubordinates()
    {
        Assert.Equal(
            "Account0ML123TerritoryAndSubordinates",
            SfGroupIdFormats.TerritoryAndSubordinates.Format("Account", "0ML123"));
    }

    /// <summary>Same inputs always produce the same output.</summary>
    [Fact]
    public void FormatsAreDeterministic()
    {
        for (var i = 0; i < 10; i++)
            Assert.Equal("Account00EABCRole", SfGroupIdFormats.Role.Format("Account", "00EABC"));
    }

    [Fact]
    public void DifferentObjectsProduceDifferentIds()
    {
        Assert.NotEqual(SfGroupIdFormats.TopLevel.Format("Account"), SfGroupIdFormats.TopLevel.Format("Lead"));
    }

    /// <summary>Verify typical 18-char Salesforce IDs work correctly.</summary>
    [Fact]
    public void EighteenCharSalesforceId()
    {
        var sfId = "00E5g000001ABCdEF";
        var result = SfGroupIdFormats.Role.Format("Account", sfId);
        Assert.Equal($"Account{sfId}Role", result);
        Assert.Contains(sfId, result);
    }

    // ── Sanitization tests ────────────────────────────────────────────────

    /// <summary>Custom object names like 'My_Custom__c' must produce alphanumeric IDs.</summary>
    [Fact]
    public void CustomObjectUnderscoresStripped()
    {
        var result = SfGroupIdFormats.TopLevel.Format("Account_Owner_Name__c");
        Assert.Equal("AccountOwnerNamecTopLevel", result);
        Assert.DoesNotContain("_", result);
    }

    [Fact]
    public void CustomObjectRoleStripped()
    {
        var result = SfGroupIdFormats.Role.Format("ACS_Customer__c", "00E123");
        Assert.Equal("ACSCustomerc00E123Role", result);
        Assert.DoesNotContain("_", result);
    }

    [Fact]
    public void HyphensStripped()
    {
        var result = SfGroupIdFormats.PublicGroup.Format("Account", "00G-456-XYZ");
        Assert.Equal("Account00G456XYZPublicGroup", result);
        Assert.DoesNotContain("-", result);
    }

    [Fact]
    public void SpacesStripped()
    {
        var result = SfGroupIdFormats.GlobalUsers.Format("My Object");
        Assert.Equal("MyObjectGlobalUsers", result);
        Assert.DoesNotContain(" ", result);
    }

    /// <summary>Already-clean inputs produce the same result as before.</summary>
    [Fact]
    public void CleanInputUnchanged()
    {
        Assert.Equal("AccountTopLevel", SfGroupIdFormats.TopLevel.Format("Account"));
        Assert.Equal("Lead00E123Role", SfGroupIdFormats.Role.Format("Lead", "00E123"));
    }
}
