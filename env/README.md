# Env

Environment variable configuration files for local development.

> **⚠️ Security:** Files containing actual credentials (`.env.local`, `.env.local.user`) must **never** be committed to source control. Only `.env.local.example` should be tracked.

## Files

| File | Description |
|------|-------------|
| `.env.local.example` | Template with placeholder values — copy this to `.env.local` and fill in your credentials. |
| `.env.local` | Main config: Salesforce instance URL, API version, client ID, Azure AD app/tenant IDs, query tuning parameters, batching/parallelism settings, and ACL engine flags. |
| `.env.local.user` | Secrets file containing `SECRET_SALESFORCE_CLIENT_SECRET` and `SECRET_AAD_APP_CLIENT_SECRET`. |

## Quick Setup

```bash
cp env/.env.local.example env/.env.local
# Edit .env.local with your Salesforce and Azure AD credentials
# Create .env.local.user with client secrets
```

Refer to `python run.py guide` for the full list of required environment variables.

## Variable Reference

### Core / Identity
| Variable | Required | Description |
|----------|----------|-------------|
| `CONNECTOR_ID` | ✅ | Unique ID for the Graph external connection |
| `CONNECTOR_NAME` | ✅ | Display name for the connection |
| `CONNECTOR_DESCRIPTION` | ✅ | Description shown in Microsoft Search |
| `TEAMSFX_ENV` | ✅ | Environment label (e.g. `local`) |
| `APP_NAME_SUFFIX` | ✅ | Suffix appended to app names (e.g. `local`) |

### Salesforce
| Variable | Required | Default | Description |
|----------|----------|---------|-------------|
| `SALESFORCE_INSTANCE_URL` | ✅ | — | Your Salesforce org URL |
| `SALESFORCE_API_VERSION` | ✅ | `v60.0` | Salesforce REST API version |
| `SALESFORCE_CLIENT_ID` | ✅ | — | Connected App Consumer Key |
| `SECRET_SALESFORCE_CLIENT_SECRET` | ✅ | — | Connected App Consumer Secret (in `.env.local.user`) |
| `SALESFORCE_QUERY_LIMIT` | — | `0` | SOQL LIMIT clause. `0` = full pagination (production). Use small value for dev/debug. |
| `SALESFORCE_BATCH_SIZE` | — | `100` | Max IDs per SOQL `IN` clause |

### Azure AD / Graph
| Variable | Required | Default | Description |
|----------|----------|---------|-------------|
| `AAD_APP_CLIENT_ID` | ✅ | — | Entra app client ID |
| `AAD_APP_OBJECT_ID` | ✅ | — | Entra app object ID |
| `AAD_APP_TENANT_ID` | ✅ | — | Entra tenant ID |
| `AAD_APP_OAUTH_AUTHORITY` | ✅ | — | Full OAuth authority URL |
| `AAD_APP_OAUTH_AUTHORITY_HOST` | ✅ | — | OAuth authority host |
| `SECRET_AAD_APP_CLIENT_SECRET` | ✅ | — | Entra app client secret (in `.env.local.user`) |
| `GRAPH_API_VERSION` | — | `beta` | Graph API version (`v1.0` or `beta`) |
| `GRAPH_MAX_RETRIES` | — | `4` | Max retries on Graph API failures |
| `GRAPH_RETRY_BACKOFF_BASE` | — | `2` | Exponential backoff base (seconds) |
| `GRAPH_RETRY_JITTER` | — | `false` | `true` → ±20% jitter on computed retry backoff (server `Retry-After` still honoured exactly). Recommended in HA — see `docs/RETRY.md`. |

### Connection & Schema Provisioning
| Variable | Required | Default | Description |
|----------|----------|---------|-------------|
| `CONNECTION_TIMEOUT_SECONDS` | — | `600` | Max wait time for connection provisioning |
| `CONNECTION_RETRY_INTERVAL_SECONDS` | — | `15` | Poll interval for connection status |
| `SCHEMA_RETRY_INTERVAL_SECONDS` | — | `15` | Poll interval for schema provisioning |

### Batching & Parallelism
| Variable | Required | Default | Description |
|----------|----------|---------|-------------|
| `INGEST_CHUNK_SIZE` | — | `2000` | Salesforce records per ACL+Graph $batch cycle. Align to 2000 (one SF page). Safe up to ~5000. |
| `INGEST_GRAPH_BATCH_SIZE` | — | `20` | Requests per Graph `$batch` POST. Hard-capped at 20 by the API. |
| `GRAPH_BATCH_WORKERS` | — | `8` | Concurrent Graph `$batch` workers (alias of `GRAPH_CONCURRENT_BATCHES`, which wins if both are set). Higher = more throughput but may cause 429s — start at 2. |

### Operations
| Variable | Required | Default | Description |
|----------|----------|---------|-------------|
| `LOG_RETENTION_DAYS` | — | `0` | `> 0` → prune `logs/{prefix}_{timestamp}/` run dirs (and, with `USE_SQL_SERVER=true`, SQL history via `usp_PruneHistory`) older than N days at each command/cycle start. `0` = keep forever. |

### ACL Engine
| Variable | Required | Default | Description |
|----------|----------|---------|-------------|
| `ACL_MAX_PARENT_DEPTH` | — | `5` | Max depth when following `ControlledByParent` ACL chains |
| `USE_NEW_ACL_ENGINE` | — | `true` | Use the new ACL engine instead of the legacy resolver |
| `USE_GROUP_ACL` | — | `true` | Enable group-based ACL (public groups, queues, territories) |
| `OWD_OVERRIDES` | — | _(empty)_ | Force specific OWD values for testing. JSON, e.g. `{"Account":"Private"}` |
