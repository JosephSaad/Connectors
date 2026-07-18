# Operator guides

Deployment, monitoring, troubleshooting and support documentation for the five
connectors, written for IT operators (no deep Microsoft Graph / OAuth / Copilot
background assumed). Each concept is explained in plain language, with intake
checklists, "information to request" callouts, and glossaries.

**Read [`00_START_HERE.pdf`](00_START_HERE.pdf) first**, then the
[Tenant & Common Concepts Guide](00_Tenant_and_Common_Concepts_Guide.docx), then
the guide for the connector you are deploying.

| # | Full guide (Word) | Quick reference (PDF) | Covers |
|---|---|---|---|
| 00 | [Tenant & Common Concepts Guide](00_Tenant_and_Common_Concepts_Guide.docx) | [Start Here](00_START_HERE.pdf) | Shared theory: Copilot & the index, Microsoft Graph, identity/ACLs, the connector engine, security, tenant quotas, deployment, monitoring, DR, master glossary |
| 01 | [Salesforce Connector Guide](01_Salesforce_Connector_Guide.docx) | [Quick ref](01_Salesforce_QuickRef.pdf) | Salesforce CRM — sharing-model ACLs |
| 02 | [Clarizen Connector Guide](02_Clarizen_Connector_Guide.docx) | [Quick ref](02_Clarizen_QuickRef.pdf) | Planview AdaptiveWork — financial-field governance, TDW bulk, webhooks |
| 03 | [Seismic Connector Guide](03_Seismic_Connector_Guide.docx) | [Quick ref](03_Seismic_QuickRef.pdf) | Seismic — version-aware ingest, No-MNE exclusion, re-ACL |
| 04 | [Altrata Connector Guide](04_Altrata_Connector_Guide.docx) | [Quick ref](04_Altrata_QuickRef.pdf) | Altrata — seat-only entitlement, DSAR erasure (personal data) |
| 05 | [Hadoop BDH Connector Guide](05_Hadoop_BDH_Connector_Guide.docx) | [Quick ref](05_Hadoop_BDH_QuickRef.pdf) | BDH data mart — filter-first at 150M+ scale, fail-closed guard, 24h lag |

## Notes for readers

- **Full guides are Word (`.docx`)**; quick references are one-page **PDFs**. The
  quick-ref cards distil each guide for use at the desk — the `.docx` guides are
  the authoritative detail.
- **Table of contents:** each Word guide has an auto-updating contents field.
  Word shows a "right-click → Update Field" placeholder until you open the file
  and update it — this is normal for generated documents.
- These guides are grounded in the connector code as of this commit, so exact
  environment-variable names, metric names and limits match what is deployed.
  See also [`../TENANT_GOVERNANCE.md`](../TENANT_GOVERNANCE.md) for the shared
  tenant quota and capacity plan.
