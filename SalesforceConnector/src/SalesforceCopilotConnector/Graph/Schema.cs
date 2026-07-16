// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Graph connector schema registration.
//
// Manages the external connection schema that defines which properties
// (fields) the connector exposes to Microsoft Search.  The schema is read
// from config/graph-schema.json at startup and registered via a PATCH
// to /external/connections/{id}/schema.
//
// Key functions
// -------------
// EnsureSchemaAsync(config, client)
//     Idempotently registers the schema.  If a schema already exists the call
//     is a no-op; if not, the function waits briefly and then creates it.
//     Retries on transient errors using the configured retry interval.
//
// SchemaExistsAsync(config, client)
//     Returns true if the connection already has a registered schema.
//
// CreateSchemaAsync(config, client)
//     Issues the PATCH request to create / update the schema.  This is a
//     long-running Graph operation; GraphClient follows the "Location"
//     header automatically.

using System.Text.Json;
using System.Text.Json.Nodes;
using SalesforceCopilotConnector.Infrastructure;
using SalesforceCopilotConnector.Salesforce;

namespace SalesforceCopilotConnector.Graph;

public static class Schema
{
    private static readonly IAppLogger Logger = Logging.GetLogger("salesforce_connector");

    private static readonly JsonSerializerOptions IndentedOptions = new() { WriteIndented = true };

    /// <summary>Register the external connection schema via a PATCH request (long-running operation).</summary>
    public static async Task CreateSchemaAsync(AppConfig config, GraphClient client)
    {
        Logger.Info($"Creating schema for connection {config.Connector.Id}. This should take under 10 minutes...");
        var url = $"{GraphClient.ExternalConnectionsPath}/{config.Connector.Id}/schema";
        var requestBody = new JsonObject
        {
            ["baseType"] = "microsoft.graph.externalItem",
            ["properties"] = config.Connector.Schema.DeepClone(),
        };
        try
        {
            await client.PatchAsync(
                url,
                jsonBody: requestBody,
                headers: new Dictionary<string, string> { ["content-type"] = "application/json" });
        }
        catch (GraphApiError e)
        {
            var responseBody = e.Body is JsonObject or JsonArray
                ? ((JsonNode)e.Body!).ToJsonString(IndentedOptions)
                : e.Body?.ToString();
            Logger.Error(
                $"Schema creation failed for connection {config.Connector.Id}.\n" +
                $"  URL: PATCH {url}\n" +
                $"  Status: {e.StatusCode}\n" +
                $"  Request body:\n{requestBody.ToJsonString(IndentedOptions)}\n" +
                $"  Response body:\n{responseBody}");
            throw;
        }
        Logger.Info($"Schema for connection {config.Connector.Id} was created");
    }

    /// <summary>Retrieve the current schema for the external connection.</summary>
    public static async Task<JsonObject> GetSchemaAsync(AppConfig config, GraphClient client)
    {
        var payload = await client.GetAsync($"{GraphClient.ExternalConnectionsPath}/{config.Connector.Id}/schema");
        return payload as JsonObject ?? new JsonObject();
    }

    /// <summary>Return true if a schema is registered for the external connection.</summary>
    public static async Task<bool> SchemaExistsAsync(AppConfig config, GraphClient client)
    {
        try
        {
            await GetSchemaAsync(config, client);
            return true;
        }
        catch (GraphApiError error)
        {
            Logger.Warning($"Can't find the schema for connection {config.Connector.Id}: {error.Message}");
            return false;
        }
    }

    /// <summary>Idempotently register the schema, creating it if not found.</summary>
    public static async Task EnsureSchemaAsync(AppConfig config, GraphClient client)
    {
        const int maxCreateAttempts = 3;
        while (true)
        {
            try
            {
                await GetSchemaAsync(config, client);
                return;
            }
            catch (GraphApiError error)
            {
                if (error.StatusCode == 404)
                {
                    Logger.Info("Schema not found. Waiting 5 seconds before creating...");
                    await Utils.DelayAsync(5);
                    for (var attempt = 1; attempt <= maxCreateAttempts; attempt++)
                    {
                        try
                        {
                            await CreateSchemaAsync(config, client);
                            return;
                        }
                        catch (GraphApiError createErr)
                        {
                            if (createErr.StatusCode == 400 && attempt < maxCreateAttempts)
                            {
                                Logger.Warning(
                                    $"Schema creation attempt {attempt}/{maxCreateAttempts} failed (400). " +
                                    $"Connection may still be provisioning — retrying in {config.Tuning.SchemaRetryIntervalSeconds}s...");
                                await Utils.DelayAsync(config.Tuning.SchemaRetryIntervalSeconds);
                            }
                            else
                            {
                                throw;
                            }
                        }
                    }
                    return;
                }

                Logger.Warning($"Schema check failed for {config.Connector.Id}. Retrying...");
                await Utils.DelayAsync(config.Tuning.SchemaRetryIntervalSeconds);
            }
        }
    }
}
