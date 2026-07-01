# C# Port Conventions — Salesforce Copilot Connector

Source Python repo: `/Users/joseph/Teams/Salesforce-Custom-Copilot-Connector`
Target C# project: `/Users/joseph/Teams/SalesforceCopilotConnector/src/SalesforceCopilotConnector`
Test project: `/Users/joseph/Teams/SalesforceCopilotConnector/tests/SalesforceCopilotConnector.Tests`

## Goal
A faithful, complete 1:1 behavioral port. Same commands, same log messages, same file formats
(JSON/JSONL state files, checkpoints, failed-record logs must stay byte-compatible with the
Python version), same API calls, same retry/backoff logic, same edge-case handling.

## Project facts
- net8.0 console app, `<Nullable>enable</Nullable>`, `<ImplicitUsings>enable</ImplicitUsings>`.
- NuGet available: `Azure.Identity`, `Spectre.Console`, `DotNetEnv`. Tests: xUnit.
- Root namespace `SalesforceCopilotConnector`.

## File / naming mapping
| Python | C# |
|---|---|
| `salesforce/` | `Salesforce/` ns `SalesforceCopilotConnector.Salesforce` |
| `graph/` | `Graph/` ns `SalesforceCopilotConnector.Graph` |
| `acl_engine/` | `AclEngine/` ns `SalesforceCopilotConnector.AclEngine` |
| `item/` | `Item/` ns `SalesforceCopilotConnector.Item` |
| `commands/` | `Commands/` ns `SalesforceCopilotConnector.Commands` |
| `config/` | `Config/` ns `SalesforceCopilotConnector.Config` |
| `run.py` | `Program.cs` |
| `dashboard.py` | `Dashboard.cs` (ns root) |

- One C# file per Python file: `api_client.py` → `ApiClient.cs`; keep one primary class per file.
- Python class names keep their names. Module-level functions → `public static class <ModuleName>`
  (e.g. `utils.py` functions → `static class Utils`).
- snake_case → PascalCase for types/methods/properties; camelCase parameters/locals.
- Async everywhere I/O happens: `requests` calls become `async Task<...>` with `Async` suffix,
  awaited all the way up; command handlers are `async Task<bool>`/`Task<int>`.
- Python dataclasses → mutable classes (or records where obviously value-like) with property
  initializers matching Python defaults.

## Library mapping
- `requests` → `HttpClient` (one shared instance per client class; set timeouts to match).
- `azure-identity` `ClientSecretCredential` → `Azure.Identity.ClientSecretCredential`,
  `GetTokenAsync(new TokenRequestContext(scopes))`.
- `python-dotenv` → `DotNetEnv` (`Env.Load(path)`), plus `Environment.GetEnvironmentVariable`.
- `rich` → `Spectre.Console` (`AnsiConsole.MarkupLine`, tables, panels, progress). Escape `[` as `[[`.
- JSON: `System.Text.Json` (+ `System.Text.Json.Nodes` `JsonObject`/`JsonArray`/`JsonNode` for
  dynamic dict-shaped payloads). Wire names must match Python exactly — use `[JsonPropertyName]`
  on typed models. Serialize with `JsonSerializerOptions { WriteIndented = ... }` to match Python
  `json.dump(..., indent=2)` where used.
- `pathlib`/`os` → `System.IO`. Keep the same relative paths (`logs/`, `env/`, etc.) resolved
  against the current working directory, exactly like Python.
- `datetime.utcnow()` etc. → `DateTime.UtcNow`; ISO formats via `ToString("o")` or the exact
  format string Python uses — match string output.

## Logging (shared contract — implemented once in `Infrastructure/Logging.cs`)
Mirrors Python `logging`: console shows WARNING+ by default, everything with `--verbose`;
a log file always captures all levels.
```csharp
namespace SalesforceCopilotConnector.Infrastructure;
public interface IAppLogger
{
    void Debug(string message);
    void Info(string message);
    void Warning(string message);
    void Error(string message);
    void Error(string message, Exception ex);
}
public static class Logging
{
    public static void Configure(bool verbose, string? logFilePath = null);
    public static IAppLogger GetLogger(string name);  // name = python __name__ equivalent, e.g. "salesforce.api_client"
}
```
Every ported file gets `private static readonly IAppLogger Logger = Logging.GetLogger("<python module name>");`
Keep log message text identical to Python.

## Cross-module references
Other modules are being ported in parallel by other agents. Do NOT stub or re-implement their
types. Derive the exact C# signature by reading the dependency's Python source and applying these
same conventions (snake_case→PascalCase, Async suffix on I/O methods, module functions on
`static class <ModuleName>`). If both of you follow the rules the signatures match.

## Style
- File header on every file:
  `// Copyright (c) Microsoft Corporation.` / `// Licensed under the MIT License.`
- Docstrings → XML doc comments (`/// <summary>`). Preserve inline comments.
- Preserve constants, magic numbers, URLs, error strings verbatim.
- No extra abstractions, DI containers, or interfaces beyond what the Python has. Keep it direct.

## Tests
- xUnit; file mapping `tests/test_acl_engine/test_principal_mapper.py` →
  `TestAclEngine/PrincipalMapperTests.cs`, ns `SalesforceCopilotConnector.Tests.TestAclEngine`.
- Python mocks of HTTP → fake `HttpMessageHandler`; monkeypatched methods → `virtual` methods
  overridden in test doubles ONLY if the Python test requires it (then mark the production method
  `virtual` — allowed deviation).
- Preserve every assertion and test case name (`test_foo_bar` → `FooBar` fact/theory).
