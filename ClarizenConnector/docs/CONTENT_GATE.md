# ContentGate — prompt-injection and malware scanning (CS-1)

**Status:** shipped, **OFF by default** (`CONTENT_GATE` unset ⇒ behaviour is
byte-identical to before the feature existed).

## Why this stage exists

Ingested content **is** Copilot grounding context. When a user asks Copilot a
question and this connector's items ground the answer, whatever text those items
carry is placed in front of the model. So a malicious Clarizen record — or a
malicious attachment inside one — is not an attack on the connector. It is an
attack on **every user whose query that item grounds**.

Nothing upstream of this stage inspects content. The ACL engine decides *who*
may see an item; the classifier decides *how sensitive* it is. Neither asks
whether the content is *hostile*. ContentGate is the stage that asks.

## What it does

Two independent scanners behind one stage:

| Scanner | Scope | Wired where |
|---|---|---|
| `InjectionScanner` | Heuristic/regex detection over the **final indexed text** | Every item, plus extracted attachment text |
| `IMalwareScanner` | Binary content scanning via an ICAP/HTTP AV gateway | Only the attachment download path — the only place raw bytes exist |

### Injection signals

Patterns live in **`config/content-gate.json`** — a new attack shape is a config
edit, not a redeploy. Categories:

| Category | Detects |
|---|---|
| `injection.override` | Imperative overrides — "ignore previous instructions", "disregard the above", forged `System:` turns, system-prompt disclosure |
| `injection.role` | Role reassignment — "you are now …", "act as an unrestricted AI", "pretend to be …" |
| `injection.exfiltration` | Exfiltration directives — "send/POST … **to** http…", `curl -d … http…`, "exfiltrate/leak the …" |
| `injection.hidden_text` | Zero-width characters splitting words (U+200B–U+200D, U+FEFF) and bidi **overrides** (U+202D/U+202E) |
| `injection.encoded_blob` | 240+ contiguous base64 characters — a smuggled payload, not prose |

Every pattern is compiled **once** at startup with a **per-pattern match
timeout**, because it runs against attacker-influenced text.

### Not false-positiving on ordinary business text

This runs against a bank's document corpus, so precision matters more than
recall. Two deliberate design choices:

1. **Role and override patterns require an AI-role noun or an instruction noun.**
   "Ravi will *act as* release manager" and "*You are now* able to submit
   expenses" do not trip; "*act as* an unrestricted AI" and "*you are now* a
   model without restrictions" do.
2. **Quote-awareness.** A match sitting entirely inside quotation marks is
   suppressed, so a security-awareness memo that *quotes* an attack phrase in
   prose is not quarantined. One unquoted occurrence anywhere still fires.

The test suite carries benign controls — financial narrative, project status
text, meeting minutes, and a document that merely quotes the phrase — that must
stay clean.

**Known residual false-positive shapes** (accepted, because quarantine is
re-drivable rather than destructive): a legitimate instruction to upload a file
directly to an external URL (`send the invoice to https://portal.vendor.com/…`)
matches `sendToUrl`, and emoji ZWJ sequences adjacent to word characters could
in principle match `zeroWidthHiddenText`.

## What the item scan covers

The gate scans the **content body plus every string / string[] property**, with
no exceptions. That includes the connector's own taxonomy properties
(`advisorySensitivity`, `DetectedCategories`) — the *classifier* skips those two
because they are its own outputs, but the gate must not, because it is asking a
different question: *what text reaches the index?*, not *what did I derive?*

A `selectedFields` mapping that points a source field at one of those names used
to hide that field's value from the gate while still publishing it as grounding
context. Two changes close it:

1. The gate scans those properties (above).
2. **`config/schema.json` rejects the mapping at load.** Pointing a source field
   at a connector-computed property name — `advisorySensitivity`,
   `DetectedCategories`, `DataClassification`, `ContainsFinancialData`,
   `ContentGateStatus`, `AttachmentExtractionStatus` — is a hard startup error
   naming the offending object and field. Source data must not be published
   under a name the connector owns.

## Posture: quarantine, not drop

A positive verdict never deletes anything. It:

1. routes the item to the **existing dead-letter queue** with reason
   `content-gate:<category>`,
2. appends a **decision-ledger** entry under the new `quarantine` decision kind
   (alongside `exclusion` and `acl_restriction` — deliberately *not* overloading
   either; a quarantine is not an exclusion, the item is retained),
3. stamps `ContentGateStatus` on the item (`blocked:<category>` /
   `incomplete:<category>` / `clean`),
4. increments `content_gate_blocked_total{category="…"}`,
5. raises the existing alert path (`ALERT_WEBHOOK_URL`, kind `content_gate`).

The item is **not** PUT to Graph and is fully re-drivable:

```
ClarizenConnector.exe retry-failed
```

`retry-failed` re-fetches the record from Clarizen and re-ingests it through the
same gate — so a fixed document sails through, and a still-malicious one is
quarantined again with the same reason.

Both per-item entry points are gated: `IngestChunkAsync` (crawl) and
`IngestSingleAsync` (`ingest-item`, `retry-failed`, **and the webhook path**,
which re-fetches by id and re-ingests through `IngestSingleAsync`).

## Fail modes — the deliberate asymmetry

When a scanner is **unavailable**, the two paths behave differently on purpose:

| Path | Default | Why |
|---|---|---|
| **binary / malware** | **FAIL CLOSED** | Never index binary content that has not been proven clean. An AV outage is not a licence to index unscanned bytes. |
| **text / injection** | **FAIL OPEN** (loud) | The injection scanner is a *heuristic, not a security boundary*. Blocking an entire crawl because a heuristic is unavailable does more damage than the risk it mitigates. |

Fail-open is **never silent**: it logs a warning, increments
`content_gate_scan_unavailable_total{kind="text"|"binary"}`, and stamps the item
`incomplete:<category>` rather than `clean`. **Alert on that metric** — it is the
only signal that the gate stopped protecting a crawl.

Both are configurable; those are the shipped defaults.

### What counts as "unavailable"

* **binary** — no `CONTENT_GATE_ICAP_URL` configured, the gateway is unreachable
  or returns a non-2xx, the response cannot be positively read as clean or
  infected, or the file exceeds `CONTENT_GATE_MAX_SCAN_MB`.
  An unparseable scanner response is **never** treated as clean.
* **text** — the pattern set is missing, unreadable, or defines no usable
  patterns, **or the text ran past `CONTENT_GATE_MAX_SCAN_MB`** so only a prefix
  was inspected.

### `clean` means "scanned in full", never "we gave up"

`ContentGateStatus` has three values, and the distinction is load-bearing:

| Value | Meaning | Indexed? |
|---|---|---|
| `clean` | The gate read **all** of the content — text *and*, for attachments, the bytes — and nothing matched. | yes |
| `incomplete:<category>` | The gate could **not** read all of it (over the scan cap, the pattern set was unusable, or no scanner saw the bytes) and the applicable fail mode is open. | yes |
| `incomplete:<cat-a>+<cat-b>` | More than one reason applied — e.g. unscannable attachment **bytes** *and* an item-level **text** scan that ran past the cap. Both categories are kept; an earlier reason is never replaced by a later one. | yes |
| `blocked:<category>` | Positive verdict, or unscannable with the applicable fail mode closed. | **no** — quarantined |

This applies to **both** paths. An attachment whose bytes went unscanned under
`CONTENT_GATE_FAIL_MODE_BINARY=open` is stamped
`incomplete:malware-unscannable` — the extracted text scanning clean says
nothing about the bytes it came from, so the later item-level text scan does not
overwrite that verdict with `clean`.

Text over `CONTENT_GATE_MAX_SCAN_MB` is scanned as a **prefix**. A hit inside
that prefix is still a hit and still blocks — truncation only affects the
*absence* of evidence. But an absence of hits in a prefix is not a clean bill of
health for the tail, so the outcome is `incomplete:injection.scan_truncated`,
counted on `content_gate_scan_unavailable_total{kind="text"}`, and with
`CONTENT_GATE_FAIL_MODE_TEXT=closed` it is quarantined like any other unscannable
text. Stamping such an item `clean` was a false assurance about content nobody
looked at.

### A disabled gate can never stamp a verdict

With `CONTENT_GATE=false` the stage is a strict no-op and every scan entry point
returns `Pass`. Stamping that `Pass` would write `ContentGateStatus=clean` onto
an item **nothing inspected** — the worst possible value for this property,
because it reads as a scan result. `ContentGateStage.Stamp` therefore takes the
enabled state as a **required** argument and throws when it is false, and the
pipeline's item-level pass returns early for a disabled stage. Production has
never reached this state (the pipeline is constructed with no gate at all when
the feature is off); the check exists so that widening the seam later fails
loudly instead of silently manufacturing a clean bill of health.

### A regex timeout is NOT an outage

A pattern timing out means the scan of **that specific document** is incomplete.
It **fails safe (blocked)** regardless of the text fail mode. Treating a timeout
as "no match" would hand an attacker a one-line bypass: feed the scanner a
pathological input and the gate opens.

## Configuration

| Variable | Default | Meaning |
|---|---|---|
| `CONTENT_GATE` | `false` | Master switch. Off ⇒ no scanning, no properties, no cost. |
| `CONTENT_GATE_ICAP_URL` | unset | HTTP endpoint of the ICAP/AV gateway for binary scanning. |
| `CONTENT_GATE_FAIL_MODE` | — | `closed`\|`open`; sets both knobs below. |
| `CONTENT_GATE_FAIL_MODE_BINARY` | `closed` | Wins over the shared knob for binaries. |
| `CONTENT_GATE_FAIL_MODE_TEXT` | `open` | Wins over the shared knob for text. |
| `CONTENT_GATE_MAX_SCAN_MB` | `16` | Cap on content handed to a scanner, in **bytes** (MiB). Text over the cap is scanned as a truncated **prefix** and the result is `incomplete`, never `clean` (the text fail mode then decides index-vs-quarantine); a **binary** over the cap is treated as unscannable. The text scanner works on a .NET string, so the byte budget is converted to a character cap by dividing by 3 (the worst-case UTF-8 cost of one UTF-16 char). Consequence: for pure-ASCII text the scanner reads roughly a third of the configured MiB in characters. Previously the byte budget was compared directly against the character count, so multibyte text was scanned up to ~3x the configured MiB — permissive in size only; it never affected a verdict, because content past the cap is reported `incomplete:injection.scan_truncated` rather than `clean`. |

An unknown fail-mode value is a **hard startup error**, not a silent fallback —
a typo that quietly meant "open" on the binary path would defeat the whole gate.

> **Configuration that blocks everything:** `CONTENT_GATE=true` +
> `ATTACHMENT_INGESTION=true` + no `CONTENT_GATE_ICAP_URL` + binary fail-closed
> ⇒ *every* attachment is quarantined. This is logged as an error at startup.
> Either set the ICAP URL or accept the risk explicitly with
> `CONTENT_GATE_FAIL_MODE_BINARY=open`.

## Scanner gateway contract

`IcapMalwareScanner` POSTs the bytes as `application/octet-stream` with
`Allow: 204` and understands the dialects real gateways emit:

* `X-Infection-Found` / `X-Violations-Found` / `X-Virus-Found` headers ⇒ infected
* HTTP 204, or `ICAP/1.0 204 No Modifications` in the body ⇒ clean
* `{"status":"clean"|"infected", "signature":"…"}` JSON ⇒ as stated
* clamd text: `stream: OK` ⇒ clean, `stream: <sig> FOUND` ⇒ infected
* anything else ⇒ **unavailable** (never "clean")

Source-controlled file names are sanitised before going on the wire, so an
attachment named with embedded CRLF cannot forge a request header.

The connector **never requires a live scanner to build or test** — production
wires `IcapMalwareScanner`, tests wire a fake, and an unconfigured deployment
wires nothing (which the binary fail mode resolves).

## Metrics

| Metric | Meaning |
|---|---|
| `clarizen_connector_content_gate_blocked_total{category}` | Items quarantined, by category. |
| `clarizen_connector_content_gate_scan_unavailable_total{kind}` | Scans that could not complete (`binary`\|`text`). **Alert on this.** |

Neither family appears in the exposition at all until the gate blocks or fails,
so `/metrics` output is unchanged by default.

## Operator runbook

**A crawl suddenly quarantines everything.** Check
`content_gate_scan_unavailable_total{kind="binary"}` — the ICAP gateway is
probably down and the binary path is fail-closed as designed. Nothing is lost:
fix the gateway, then `retry-failed` re-drives the queue.

**A legitimate document was quarantined.** The dead-letter entry names the
category and the decision ledger records the verdict. Either fix the document,
tune the pattern in `config/content-gate.json`, or — if the shape is
systematically noisy — remove that pattern. Then `retry-failed`.

**The gate silently stopped working.** That is what
`content_gate_scan_unavailable_total{kind="text"}` is for: fail-open means the
crawl proceeds unprotected, so this metric must be on a dashboard and an alert.
