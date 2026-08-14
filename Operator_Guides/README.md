# Operator guides

Deployment, monitoring, troubleshooting and support documentation for the five
connectors, written for IT operators (no deep Microsoft Graph / OAuth / Copilot
background assumed). Each concept is explained in plain language, with intake
checklists, "information to request" callouts, and glossaries.

**Read [`00_START_HERE.pdf`](00_START_HERE.pdf) first**, then the
[Tenant & Common Concepts Guide](00_Tenant_and_Common_Concepts_Guide.docx), then
the guide for the connector you are deploying.

The one-page architecture picture —
[Connectors_Architecture_Diagram.svg](Connectors_Architecture_Diagram.svg) —
shows all five connectors, their source systems, the Microsoft Graph API,
Entra ID, the Copilot semantic index and the supporting services on one canvas.

## Programme planning

- [Connector_Program_Project_Plan.xlsx](Connector_Program_Project_Plan.xlsx) —
  bank-realistic implementation plan (123 tasks, WBS, dependencies, computed
  dates, roles). Colour-coded gates: purple = bank-resource queues (hiring
  with notice periods, infra/PKI/identity/firewall/DBA/SIEM/source-admin lead
  times), red = security gates (threat model, pen test, sign-offs,
  DPIA/legal), amber = CAB/change cycles. Calendar models US federal bank
  holidays plus a 15-Dec → 08-Jan year-end blackout. Scope: 4 connectors —
  the Salesforce connector is deferred (Hadoop BDH carries the Salesforce
  data); Seismic pilots, with a Phase-4 checkpoint to revisit Salesforce.
- [Connector_Program_Project_Plan.msproj.xml](Connector_Program_Project_Plan.msproj.xml) —
  the same plan as MS Project XML (open in Microsoft Project → "As a new
  project"; the bank calendar and dependency network import and reschedule
  natively).
- [GENESIS_PROMPT.md](GENESIS_PROMPT.md) — the compiled prompt that would
  reproduce this entire programme: connectors, hardening waves, documentation
  set, diagrams and programme artefacts.

| # | Full guide (Word) | Quick reference (PDF) | Covers |
|---|---|---|---|
| 00 | [Tenant & Common Concepts Guide](00_Tenant_and_Common_Concepts_Guide.docx) | [Start Here](00_START_HERE.pdf) | Shared theory: Copilot & the index, Microsoft Graph, identity/ACLs, the connector engine, security, tenant quotas, deployment, monitoring, DR, master glossary |
| 01 | [Salesforce Connector Guide](01_Salesforce_Connector_Guide.docx) | [Quick ref](01_Salesforce_QuickRef.pdf) | Salesforce CRM — sharing-model ACLs |
| 02 | [Clarizen Connector Guide](02_Clarizen_Connector_Guide.docx) | [Quick ref](02_Clarizen_QuickRef.pdf) | Planview AdaptiveWork — financial-field governance, TDW bulk, webhooks |
| 03 | [Seismic Connector Guide](03_Seismic_Connector_Guide.docx) | [Quick ref](03_Seismic_QuickRef.pdf) | Seismic — version-aware ingest, No-MNE exclusion, re-ACL |
| 04 | [Altrata Connector Guide](04_Altrata_Connector_Guide.docx) | [Quick ref](04_Altrata_QuickRef.pdf) | Altrata — seat-only entitlement, DSAR erasure (personal data) |
| 05 | [Hadoop BDH Connector Guide](05_Hadoop_BDH_Connector_Guide.docx) | [Quick ref](05_Hadoop_BDH_QuickRef.pdf) | BDH data mart — filter-first at 150M+ scale, fail-closed guard, 24h lag |

## Architecture, governance and commercial

Not indexed above, but part of the delivered set:

| Document | What it is |
|---|---|
| [Connector_Parameter_Reference.html](Connector_Parameter_Reference.html) | Every environment variable across all five connectors — 440 parameters with meaning, default, suggested value and owning team. Reconciled against the source on 14 Aug 2026. |
| [Connectors_Architecture_Diagram.svg](Connectors_Architecture_Diagram.svg) | Deployment and data flow on one canvas. |
| [SIEM_Integration_Diagram.svg](SIEM_Integration_Diagram.svg) | Event Log / SIEM and OpenTelemetry wiring. |
| [Release_Notes_Hardening_Programme.docx](Release_Notes_Hardening_Programme.docx) | Hardening and consolidation release notes (v3.0 covers the chassis re-platform). |
| [Connector_Platform_Gap_Analysis.docx](Connector_Platform_Gap_Analysis.docx) | Platform gaps with remediation sizing. |
| [Availability_Tier_ARB_Decision_Paper.docx](Availability_Tier_ARB_Decision_Paper.docx) | Availability tier for ARB. **Still unapproved.** |
| [Wave1_External_Dependency_Requests.docx](Wave1_External_Dependency_Requests.docx) | External dependency requests, incl. R1 to Microsoft on Purview enforcement. |
| [Enterprise_Architect_Role_Charter.pptx](Enterprise_Architect_Role_Charter.pptx) | EA role charter and architecture principles. |
| [Connector_Operations_Deck.pptx](Connector_Operations_Deck.pptx) | The full operator guidance as slides (831). |
| [Webzion_SoW_Copilot_Connector_Programme.docx](Webzion_SoW_Copilot_Connector_Programme.docx) | Statement of work. |

## Notes for readers

- **Full guides are Word (`.docx`)**; quick references are one-page **PDFs**. The
  quick-ref cards distil each guide for use at the desk — the `.docx` guides are
  the authoritative detail.
- **Table of contents:** each Word guide has an auto-updating contents field.
  Word shows a "right-click → Update Field" placeholder until you open the file
  and update it — this is normal for generated documents.
- **Currency.** The Word guides, the decks, the diagrams, the SoW and the
  parameter reference were reconciled against the connector source on
  **14 August 2026** — environment-variable names, test counts, the chassis
  version and the architecture description all match the tree at that date.
  The one-page **PDF quick references have NOT been regenerated** and still
  carry July test counts; treat the `.docx` guides as authoritative where they
  disagree. See also [`../TENANT_GOVERNANCE.md`](../TENANT_GOVERNANCE.md) for
  the shared tenant quota and capacity plan.
