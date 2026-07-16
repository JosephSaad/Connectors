// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Tests for the guide command.

using SalesforceCopilotConnector.Commands;

namespace SalesforceCopilotConnector.Tests.TestCommands;

[Collection("CommandHooks")]
public class CmdGuideTests
{
    [Fact]
    public async Task CmdGuidePrintsWithoutError()
    {
        // cmd_guide should run without raising.
        var args = new ParsedArgs();
        var originalOut = Console.Out;
        try
        {
            using var buffer = new StringWriter();
            Console.SetOut(buffer);
            // Just verify no exception; guide writes to the (redirected) stdout
            await Guide.CmdGuide(args);
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public void GuideTextContainsKeySections()
    {
        // Verify the guide constant contains expected sections.
        Assert.Contains("OVERVIEW", Guide.GuideText);
        Assert.True(Guide.GuideText.Contains("STEP 1") || Guide.GuideText.Contains("PREREQUISITES"));
        Assert.True(Guide.GuideText.Contains("STEP 3") || Guide.GuideText.Contains("COMMANDS"));
    }
}
