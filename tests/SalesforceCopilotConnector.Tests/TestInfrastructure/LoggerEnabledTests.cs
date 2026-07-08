// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using SalesforceCopilotConnector.Infrastructure;
using Xunit;

namespace SalesforceCopilotConnector.Tests.TestInfrastructure;

/// <summary>
/// <see cref="LoggerObject.IsEnabledFor"/> mirrors Python's <c>Logger.isEnabledFor</c>:
/// compare against the effective level (own level, else nearest ancestor, else WARNING).
/// The ingest pipeline relies on it to skip serializing $batch responses when DEBUG
/// output would be dropped anyway.
/// </summary>
public class LoggerEnabledTests
{
    [Fact]
    public void RespectsOwnLevel()
    {
        var logger = (LoggerObject)Logging.GetLogger("tests.enabledfor.own");
        logger.Level = LogLevels.Info;

        Assert.False(logger.IsEnabledFor(LogLevels.Debug));
        Assert.True(logger.IsEnabledFor(LogLevels.Info));
        Assert.True(logger.IsEnabledFor(LogLevels.Error));

        logger.Level = LogLevels.NotSet;  // restore inheritance for other tests
    }

    [Fact]
    public void InheritsAncestorLevel()
    {
        var parent = (LoggerObject)Logging.GetLogger("tests.enabledfor");
        var child = (LoggerObject)Logging.GetLogger("tests.enabledfor.child.grandchild");
        parent.Level = LogLevels.Error;

        Assert.False(child.IsEnabledFor(LogLevels.Warning));
        Assert.True(child.IsEnabledFor(LogLevels.Error));

        parent.Level = LogLevels.NotSet;
    }

    [Fact]
    public void FallsBackToRootLevel()
    {
        // No level on the chain → the root logger's level decides (Python root
        // defaults to WARNING; SetupLogging may have re-leveled it in this process,
        // so pin and restore to keep the test order-independent).
        var previousRootLevel = Logging.Root.Level;
        try
        {
            Logging.Root.Level = LogLevels.Warning;
            var logger = (LoggerObject)Logging.GetLogger("tests.enabledfor.unset.chain");

            Assert.False(logger.IsEnabledFor(LogLevels.Debug));
            Assert.False(logger.IsEnabledFor(LogLevels.Info));
            Assert.True(logger.IsEnabledFor(LogLevels.Warning));
        }
        finally
        {
            Logging.Root.Level = previousRootLevel;
        }
    }
}
