// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// External connection lifecycle management.
//
// Functions in this module create, verify, configure, and tear down Microsoft
// Graph external connections used by the Salesforce CRM connector.
//
// Key functions
// -------------
// EnsureConnectionAsync(config, client, initialTimestamp)
//     Idempotently creates the external connection.  If the connection already
//     exists it is a no-op; if not, it is created.  Retries on auth errors
//     (prompting the operator to grant admin consent) until the configured
//     timeout elapses.
//
// IsConnectionReadyAsync(config, client)
//     Returns true when the connection state is "ready" AND a schema
//     has been registered.
//
// SetSearchSettingsAsync(config, client)
//     PATCHes the connection's "searchSettings" with the adaptive card
//     result template defined in config/template.json.  Skipped if search
//     settings are already present.
//
// DeleteConnectionAsync / ClearConnectionItemsAsync
//     Destructive helpers used during development and reset workflows.

using System.Text.Json.Nodes;
using Azure.Identity;
using SalesforceCopilotConnector.Infrastructure;
using SalesforceCopilotConnector.Salesforce;

namespace SalesforceCopilotConnector.Graph;

public static class Connection
{
    private static readonly IAppLogger Logger = Logging.GetLogger("salesforce_connector");
    private static bool _consentRequested = false;

    /// <summary>Create a new external connection in Microsoft Graph.</summary>
    public static async Task CreateConnectionAsync(AppConfig config, GraphClient client)
    {
        Logger.Info($"Creating connection {config.Connector.Id}.");
        await client.PostAsync(
            GraphClient.ExternalConnectionsPath,
            jsonBody: new JsonObject
            {
                ["id"] = config.Connector.Id,
                ["name"] = config.Connector.Name,
                ["description"] = config.Connector.Description,
            },
            headers: new Dictionary<string, string> { ["content-type"] = "application/json" });
        Logger.Info($"Connection {config.Connector.Id} was created");
    }

    /// <summary>Retrieve the external connection metadata from Microsoft Graph.</summary>
    public static async Task<JsonObject> GetConnectionAsync(AppConfig config, GraphClient client)
    {
        var payload = await client.GetAsync($"{GraphClient.ExternalConnectionsPath}/{config.Connector.Id}");
        return payload as JsonObject ?? new JsonObject();
    }

    /// <summary>Patch the connection's search settings with the adaptive card result template.</summary>
    public static async Task SetSearchSettingsAsync(AppConfig config, GraphClient client)
    {
        var connection = await GetConnectionAsync(config, client);
        if (IsTruthy(connection["searchSettings"]))
        {
            return;
        }

        var payload = new JsonObject
        {
            ["searchSettings"] = new JsonObject
            {
                ["searchResultTemplates"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["id"] = "display",
                        ["layout"] = config.Connector.Template.DeepClone(),
                        ["priority"] = 1,
                    }
                }
            }
        };
        Logger.Info($"PATCH {GraphClient.ExternalConnectionsPath}/{config.Connector.Id}");
        await client.PatchAsync(
            $"{GraphClient.ExternalConnectionsPath}/{config.Connector.Id}",
            jsonBody: payload,
            headers: new Dictionary<string, string> { ["content-type"] = "application/json" });
    }

    /// <summary>Return true if the external connection exists, false otherwise.</summary>
    public static async Task<bool> ConnectionExistsAsync(AppConfig config, GraphClient client)
    {
        try
        {
            await GetConnectionAsync(config, client);
            return true;
        }
        catch (GraphApiError error)
        {
            Logger.Warning($"Can't find the connection {config.Connector.Id}: {error.Message}");
            return false;
        }
    }

    /// <summary>Ensure external connection exists. Returns "created", "existing", or null on failure.</summary>
    public static async Task<string?> EnsureConnectionAsync(AppConfig config, GraphClient client, double initialTimestamp)
    {
        var progress = Logging.GetLogger("progress");
        while (Monotonic() - initialTimestamp <= config.Tuning.ConnectionTimeoutSeconds)
        {
            try
            {
                await GetConnectionAsync(config, client);
                Logger.Info($"Connection {config.Connector.Id} already exists");
                progress.Info($"  Connection '{config.Connector.Id}' verified (existing)");
                return "existing";
            }
            catch (Exception error)  // runtime error fan-in
            {
                if (error is GraphApiError graphError && graphError.StatusCode == 404)
                {
                    await CreateConnectionAsync(config, client);
                    progress.Info($"  Connection '{config.Connector.Id}' created");
                    return "created";
                }

                if (IsAuthError(error))
                {
                    RequestAdminConsentOnce(config);
                    await Utils.DelayAsync(config.Tuning.ConnectionRetryIntervalSeconds);
                    continue;
                }

                Logger.Error($"Failed to ensure connection {config.Connector.Id}", error);
                return null;
            }
        }

        Logger.Error($"Could not create connection {config.Connector.Id} in under 10 minutes");
        return null;
    }

    /// <summary>Return true if the connection state is 'ready' and a schema is registered.</summary>
    public static async Task<bool> IsConnectionReadyAsync(AppConfig config, GraphClient client)
    {
        try
        {
            var connection = await GetConnectionAsync(config, client);
            var connectionState = connection["state"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(connectionState) && connectionState != "ready")
            {
                Logger.Info($"Connection {config.Connector.Id} is not ready");
                return false;
            }

            Logger.Info($"Connection {config.Connector.Id} is ready");

            if (!await Schema.SchemaExistsAsync(config, client))
            {
                Logger.Info("Schema is not deployed");
                return false;
            }

            return true;
        }
        catch (Exception error)  // runtime error fan-in
        {
            Logger.Error($"Error checking connection {config.Connector.Id}: {error.Message}", error);
            return false;
        }
    }

    /// <summary>Delete all items from the external connection. Returns the number of items deleted.</summary>
    public static async Task<int> ClearConnectionItemsAsync(AppConfig config, GraphClient client)
    {
        var deletedCount = 0;
        await foreach (var item in client.PaginateAsync($"{GraphClient.ExternalConnectionsPath}/{config.Connector.Id}/items"))
        {
            var itemId = item["id"]?.GetValue<string>();
            if (string.IsNullOrEmpty(itemId))
            {
                continue;
            }
            Logger.Info($"Deleting item {itemId} from connection {config.Connector.Id}");
            await client.DeleteAsync($"{GraphClient.ExternalConnectionsPath}/{config.Connector.Id}/items/{Uri.EscapeDataString(itemId)}");
            deletedCount++;
        }
        return deletedCount;
    }

    /// <summary>Delete the external connection, retrying on auth errors until timeout. Returns true on success.</summary>
    public static async Task<bool> DeleteConnectionAsync(AppConfig config, GraphClient client, double initialTimestamp)
    {
        while (Monotonic() - initialTimestamp <= config.Tuning.ConnectionTimeoutSeconds)
        {
            try
            {
                var connection = await GetConnectionAsync(config, client);
                if (connection.Count > 0)
                {
                    Logger.Info($"Deleting connection {config.Connector.Id}");
                    await client.DeleteAsync($"{GraphClient.ExternalConnectionsPath}/{config.Connector.Id}");
                    Logger.Info($"Connection {config.Connector.Id} was deleted");
                    return true;
                }
                return false;
            }
            catch (Exception error)  // runtime error fan-in
            {
                if (error is GraphApiError graphError && graphError.StatusCode == 404)
                {
                    Logger.Warning($"Connection {config.Connector.Id} does not exist");
                    return true;
                }

                if (IsAuthError(error))
                {
                    RequestAdminConsentOnce(config);
                    await Utils.DelayAsync(config.Tuning.ConnectionRetryIntervalSeconds);
                    continue;
                }

                Logger.Error($"Failed to delete connection {config.Connector.Id}", error);
                return false;
            }
        }

        Logger.Error($"Could not delete connection {config.Connector.Id} in under 10 minutes");
        return false;
    }

    /// <summary>Log a one-time warning with the Entra admin consent URL.</summary>
    private static void RequestAdminConsentOnce(AppConfig config)
    {
        if (_consentRequested)
        {
            return;
        }

        Logger.Warning(
            "You need to grant tenant-wide admin consent to the application in Entra ID. " +
            "Use this link to provide the consent: " +
            $"https://entra.microsoft.com/#view/Microsoft_AAD_RegisteredApps/ApplicationMenuBlade/~/CallAnAPI/appId/{config.ClientId}/isMSAApp~/false");
        _consentRequested = true;
    }

    /// <summary>Return true if the error indicates an authentication or authorization failure.</summary>
    private static bool IsAuthError(Exception error)
    {
        if (error is AuthenticationFailedException)
        {
            return true;
        }

        if (error is GraphApiError graphError)
        {
            if (graphError.StatusCode is 401 or 403)
            {
                return true;
            }
            if (graphError.StatusCode == -1 && graphError.Code == "AuthenticationRequiredError")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Python truthiness for a JSON node: null and empty objects/arrays are falsy.</summary>
    private static bool IsTruthy(JsonNode? node)
    {
        return node switch
        {
            null => false,
            JsonObject obj => obj.Count > 0,
            JsonArray arr => arr.Count > 0,
            _ => true,
        };
    }

    /// <summary>Monotonic clock in seconds (equivalent of Python time.monotonic()).</summary>
    private static double Monotonic()
    {
        return Environment.TickCount64 / 1000.0;
    }
}
