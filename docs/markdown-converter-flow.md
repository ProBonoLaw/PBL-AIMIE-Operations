# Markdown Converter — swimlane process / data flow

[markdown-converter-flow.mmd](markdown-converter-flow.mmd) describes the converter's
request paths in Mermaid. Keep it aligned with `src/MarkdownConverter/`,
`src/Aimie.Operations/DeclarationOutputStore.cs`, `DocumentGenerationHandler.cs`,
and the parameterized Kubernetes manifests.

## Lanes

One lane per actor, not per phase.

| Lane | Holds | Why it is separate |
| --- | --- | --- |
| 1 — Callers · AIMIE Operations + Portal | declaration + QA workers, portal web app, data ingestion Logic App | Each owns a different trigger and a different downstream store |
| 2 — AKS edge · aimie-operations | ClusterIP Service, internal Ingress, Deployment and probes | In-cluster callers use the Service; the Ingress serves callers from outside the namespace |
| 3 — markdown-converter · API | shared request pipeline plus the three route chains | The only lane the converter itself owns |
| 4 — Outputs + stores | SharePoint, Blob, Cosmos, `aimie-response-queue`, portal answer, ingestion pipeline | Written by the **caller**, never by the converter |
| 5 — Config + identity | workload identity, Key Vault, CSI volume | Startup-time only; no runtime data store |

The converter persists nothing, so the DOCX bytes and indexed JSON return to lane 1 and the store writes
leave from there. Drawing the API straight to SharePoint would put the write on the wrong side of the
trust boundary, in this diagram.

## The three routes

**`POST /api/markdowntodocxconverter`** — `SharePointDeclarationOutputStore.ToWordAsync` posts
`{ markdown }` (`MARKDOWN_TO_DOCX_URL`, `CallBinaryAsync`, 3 attempts, 2 s backoff). Typography is
normalized, Markdig renders Markdown to HTML, HtmlToOpenXml builds a `WordprocessingDocument` in a
`MemoryStream`, and DOCX bytes come back. The worker then uploads `Declaration.docx` and each
`Exhibit N.docx` to the account's SharePoint folder.

**`POST /api/markdowndocumentexhibitindexer`** — called by `DocumentGenerationHandler`
(`EXHIBIT_INDEX_URL`), which handles the `declaration` and `qa` roles only, and by the portal
(`AIMIE_QA_FORMATTING_URL`) for both QA answers and Argument Builder drafts. The `argument-builder`
and `timeline-builder` workers run through `GenerationHandler` and never call the converter — the
Argument Builder's formatting happens portal-side, in
`app/api/accounts/cases/[caseId]/requests/[requestId]/route.ts`.
`DocumentIndexer` runs two passes. First it picks a matcher — the irregular `docId` regex when the
regular one finds nothing or the irregular one finds more — then walks every match, skipping any whose
`docId` is absent from `context` or whose `sourceUrl` is empty, and numbers the survivors by first
occurrence. The second pass rewrites every match: one that earned an exhibit index becomes a link
(parenthesized for the regular matcher), and one that did not becomes an empty string. Text the matcher
never matched is left untouched. It returns `{ document, exhibits[] }`, and callers then write Blob
`result.json`, the Cosmos `requests` record and the `aimie-response-queue` event.

The link text is the bare index — `[1](sourceUrl)` — unless the caller sets `useExhibitNomenclature`,
which prefixes it with `Exhibit` or with `indexNomenclature` when that is supplied. No caller sets
either today: neither payload in `DocumentGenerationHandler` passes them, and nor does the portal, so
every current consumer receives numbered links. The `Exhibit N` naming that does reach SharePoint comes
from `SharePointDeclarationOutputStore`, which builds the file names and exhibit headings separately.

A citation whose `docId` is missing from `context`, or whose `sourceUrl` is empty, is logged as a
warning and **replaced with an empty string**. The response is still 200, so a caller cannot tell from
the status code that references were dropped.

**`POST /api/htmltomarkdownconverter`** — legacy route preserved from the retired Azure Functions app
and used by the data ingestion Logic App.
ReverseMarkdown in GitHub-flavored mode returns a `text/markdown` file.

## Constraints the diagram carries

- Stateless: no Azure Storage account, no Function key, read-only root filesystem, non-root user.
- 25 MB cap on both sides — Kestrel `MaxRequestBodySize` and the Ingress `proxy-body-size: 25m`.
- Empty `Markdown` / `Html` / `Document` returns **400** with a plain-text message before any engine runs.
- `CorrelationLoggingMiddleware` accepts `X-Correlation-Id` / `X-Operation-Id` (sanitized to
  `^[a-zA-Z0-9._:/-]{1,128}$`), mints a GUID when absent, echoes both back, and scopes every log line.
- CORS is configured only when `CORS_ALLOWED_ORIGINS` is non-empty; each environment supplies its allowed portal origins through GitHub variables and Key Vault.
- `/healthz` backs the readiness (10 s) and liveness (20 s) probes and is exposed through the Ingress.

## Network configuration

A portal outside the cluster must use a reachable ingress URL configured for that
environment. Cluster-internal Service names are intended for callers inside AKS.
Verify routing and TLS trust when configuring an external caller.
