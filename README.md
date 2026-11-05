# AIMIE Operations

Licensed under [Apache 2.0](LICENSE). Development is hosted at [ProBonoLaw/PBL-AIMIE-Operations](https://github.com/ProBonoLaw/PBL-AIMIE-Operations).

ProBonoLaw is the default destination for development pushes and pull requests. Merged public `main` changes flow one way into the original private repository for deployment. See the [development and synchronization workflow](docs/development-workflow.md).

Four .NET 10 Event Hubs workers replace the generation paths in the Logic Apps workflows `Declaration-Generation`, `QA-Generator`, `Argument-Builder-Generator`, and `Timeline-Builder-Generator`. Each Kubernetes Deployment has its own consumer group on **`aimie-request-queue`**. The workers ignore unrelated event types and emit results to `aimie-response-queue`. The .NET 10 Markdown Converter API runs as a separate Kubernetes Deployment in the same namespace.

The application has one namespace, `Aimie.Operations`, and one Kubernetes namespace, `aimie-operations`. Email intake, Microsoft Graph, replies, the notification API, and the `Email-Message-Identification` schedule belong in the separate email operations repository. There is no CronJob in this repository.
The four Event Hubs workers are outbound-only and have no Service or ingress. The converter has a ClusterIP Service for the workers. Test also has a private, TLS-enabled Ingress for calls from the portal Web App server.

The existing `Declaration-Generation` Logic App is an email-to-request adapter; its actual writer is in `Generic-Declaration-Request-Processing`. This repository implements that writer path after receiving normalized input. The existing `QA-Generator` includes email lookup, HTML conversion, and reply delivery; this repository implements its Fast QA and Exhibit Index generation steps and hands off the result. The separate email repository implements the email-specific steps, including the internal notification API.

## Request contract

Publish JSON with a `contentData` object (a bare object is also accepted):

```json
{
  "contentData": {
    "type": "qa",
    "accountId": "case-account-id",
    "input": "What happened after the filing?",
    "sessionId": "optional-session-id",
    "messageId": "optional-email-message-id"
  }
}
```

`type` is one of `declaration`, `qa`, `argument-builder`, or `timeline-builder`. `accountId` and either `input` or `question` are required. The two document workers intentionally skip the original email notification shape (`accountId`, `messageId`, `type`) because the email repository must first fetch and normalize the message. Optional fields `correlationId`, `oid`, `conversationId`, `emailAddresses`, `emailSubject`, `maxDocCount`, and `provideThoroughResponse` are passed through or used where the source workflow used them.

| Role | Source Logic App function call | Result |
| --- | --- | --- |
| `declaration` | Generic Declaration's Declaration Writer (`azureFunctionOperation-4`), Exhibit Index (`-6`), Markdown-to-DOCX converter (`-5`) | `Declaration.md`/`.docx` and exhibit `.md`/`.docx` files in the account's SharePoint site, indexed JSON in Blob Storage, request record with SharePoint folder and attachment references in Cosmos DB |
| `qa` | Fast QA (`-19`), Exhibit Index (`-6`) | Indexed answer and exhibits in Blob Storage |
| `argument-builder` | Argument Builder (`-20`), Notification (`-17`) | `rawoutput.json` in the account's SharePoint site, request record in Cosmos DB |
| `timeline-builder` | Timeline Builder (`-21`), Notification (`-17`) | `rawoutput.json` in the account's SharePoint site, request record in Cosmos DB |

The Markdown Converter API preserves the three routes used by the former Azure Functions app: `POST /api/markdowntodocxconverter`, `POST /api/markdowndocumentexhibitindexer`, and `POST /api/htmltomarkdownconverter`. It is stateless and does not use Azure Storage accounts. Worker pods use its ClusterIP Service. Both environments render `markdown-converter-ingress.template.yaml` using `MARKDOWN_CONVERTER_INGRESS_HOST` and `MARKDOWN_CONVERTER_TLS_CERT_KEYVAULT_URI`. The portal must reach the internal load balancer and trust its certificate. Set the portal origins with `CORS_ALLOWED_ORIGINS`; no deployment hostnames are included in the template.

Converter requests accept `X-Correlation-Id` and `X-Operation-Id` headers from callers. If either is missing or invalid, the API generates a safe identifier; it returns both identifiers as response headers. Structured request, completion, cancellation, and failure logs include those IDs and the ASP.NET trace ID. The CORS policy exposes the response headers to the configured UI origin.

Calls to the AIMIE Service use the `DaatPhoenix.HttpOrchestration` GitHub NuGet package from the File Operations project. The service runs in AKS in Test and Production; worker settings point to its ClusterIP routes, with an Ingress base URL available for local runs. No AIMIE endpoint is embedded in source. Exhibit indexing and Markdown-to-DOCX conversion use the Kubernetes Markdown Converter API. Notifications are sent as JSON POSTs to the email repository's internal notification API.

Notice payloads follow the API's typed schemas. `ARGUMENT_BUILDER` and `TIMELINE_BUILDER` do not define `sourceUrl` and reject unmapped fields. `DECLARATION` uses `declarationUrl`, which must be an absolute URL for both successful and failed notices.

Successful response events have `contentData.status = "Generated"`, the source `type`, `accountId`, `responseId`, `operationId`, `correlationId`, and `resultReference` pointing to the configured Cosmos database and its `requests` container. Document events also have `outputUrl`, the Blob URI of `result.json`, whose `document` and `exhibits` fields come from the Exhibit Index route. Declaration events additionally carry `declarationMd` and `attachments` file references for the SharePoint artifacts. Declaration files and builder `rawoutput.json` files are saved under `/Shared Documents/_GeneratedDocuments/{sessionId}{timestamp}-{operationId}` on the account's SharePoint site. `sessionId` remains a caller/session value and is not treated as unique. The operation ID distinguishes declaration, timeline, argument-builder, and document outputs and is also part of each Blob or SharePoint result path, preventing one result from overwriting another when sessions repeat. The Cosmos request record carries `operationId`, `correlationId`, and the corresponding result fields. The email repository can use `messageId` to deliver the response. A terminal AIMIE Service result emits `status = "Failed"`. Consumers should deduplicate by `responseId` because Event Hubs, Cosmos DB, and the response hub do not share a transaction.

Each operation ID is deterministic from the worker role and Event Hubs source coordinates, so retries keep the same ID while different roles, hubs, partitions, or sequence numbers get different IDs. A safe caller-supplied `correlationId` is preserved; otherwise the operation ID is used. Both values appear in Cosmos request records and response content, are sent to the AIMIE Service and notifications as request metadata, and are attached to response Event Hubs events. Service logs include the IDs and Durable instance ID where returned; Blob metadata and SharePoint paths also carry operation identifiers.

The declaration worker preserves the Logic App's SharePoint result set: Markdown and DOCX files for the declaration and each exhibit, a Cosmos request record with the SharePoint folder and attachment references, and matching `declarationMd`/`attachments` fields on the response event. It also stores the indexed JSON in Blob Storage for the email repository's current PDF delivery path. The QA worker stores its indexed Markdown and exhibit metadata in Blob Storage. Blob reads use workload identity, so the response consumer needs Blob read permission. These worker result stores are separate from the stateless converter service.

## Local debugging in VS Code

The [launch configurations](.vscode/launch.json) run each of the four workers with `.local/appsettings.Test.json`. The [tasks](.vscode/tasks.json) build the application before launch and run the solution tests. The devcontainer installs the C# debugger extension.

1. Sign in with `az login` to the test subscription and ensure `.local/appsettings.Test.json` exists; regenerate it with `/opt/az/bin/python3 scripts/generate-test-appsettings.py` if needed. Set `AIMIE_SERVICE_BASE_URL` and `MARKDOWN_CONVERTER_PUBLIC_BASE_URL` to your environment's private ingress URLs before generating when local workers need the Test Ingress endpoints. The local machine must trust the Test Internal Root CA and reach the private AKS load balancer. Local launch profiles point notifications to `http://127.0.0.1:5080/api/sendNotice`; run the Notification Service API from the email repository when debugging a worker path that sends a notice. Restore NuGet packages using a GitHub Packages credential with `read:packages` (see below).
2. Run `bash scripts/provision-local-debug.sh` once to create four **local-only** consumer groups and the three Blob containers. The signed-in identity needs Event Hubs Data Receiver on `aimie-request-queue`, Data Sender on `aimie-response-queue`, Storage Blob Data Contributor on those containers, and Cosmos DB data-plane access to `requests`.
3. Choose a worker in **Run and Debug**, press F5, and enter the `accountId` of an isolated test case. The local worker skips every other account. Start the debugger before publishing a new request: a new consumer group begins at the latest event.

These profiles call the live **test** AIMIE Service and write to test Cosmos DB, Blob Storage, and `aimie-response-queue`. When notifications are sent, the local Notification Service API also writes test notices to Cosmos DB and Web PubSub. Use the **test AIMIE Operations** task or the VS Code Test Explorer for unit tests without those cloud side effects.

The unit tests cover routing, event retention and checkpoint decisions, HTTP requests, converter routes and file formats, the four operation paths, failures, and replay from saved records. CI publishes a Cobertura report and requires at least 65% overall line and branch coverage plus 95% line coverage for both operation handlers and event processing. Azure Event Hubs, Cosmos DB, and Blob Storage adapters still require a live test environment for integration verification.

## Configuration

See [Test configuration review](docs/test-configuration-review.md) for the generated local settings, their source mappings, and the publication sequence. The local files `.local/appsettings.Test.json` and `.local/markdown-converter-appsettings.Test.json` are excluded from Git and Docker.

Mount the worker Key Vault secret `aimie-operations-appsettings` as `/mnt/keyvault/appsettings.json`, following File Operations' Secrets Store CSI pattern. Worker settings, including the Cosmos endpoint and service URLs, come from this appsettings JSON. Kubernetes worker Deployments only set process-specific values such as `ROLE` and `EVENTHUB_CONSUMER_GROUP`; they do not import endpoint values from a ConfigMap. Each environment provides its own worker appsettings secret so production can use its Cosmos and service endpoints. The converter loads CORS settings from a separate Key Vault secret named `markdown-converter-appsettings`; its workload identity can read that secret without access to worker credentials.

| Setting | Purpose |
| --- | --- |
| `COSMOS_ENDPOINT` | Cosmos DB account URI; set in the environment's `aimie-operations-appsettings` JSON |
| `CHECKPOINT_BLOB_CONTAINER_URI` | Existing checkpoint container URI |
| `FAILED_EVENTS_BLOB_CONTAINER_URI` | Existing malformed-event container URI |
| `OUTPUT_BLOB_CONTAINER_URI` | Existing container URI for declaration and QA results |
| `*_FUNCTION_URL` | AIMIE Service route URLs in the environment's appsettings JSON; setting names retain the existing client contract |
| `NOTIFICATION_SERVICE_URL` | Email repository notification API endpoint in the environment's appsettings JSON |
| `EXHIBIT_INDEX_URL`, `MARKDOWN_TO_DOCX_URL` | Markdown Converter routes in the environment's appsettings JSON |
| `CORS_ALLOWED_ORIGINS` | UI origins allowed by the converter, in its separate Key Vault appsettings JSON |
| `SHAREPOINT_TENANT_ID`, `SHAREPOINT_CLIENT_ID`, `SHAREPOINT_HOST`, `SHAREPOINT_SITE_PREFIX` | Required GitHub environment variables, copied into the worker Key Vault JSON; no tenant-specific defaults |
| `SHAREPOINT_CLIENT_SECRET` | Credential read from Key Vault; never a GitHub variable |

The Markdown Converter API has no Function key setting. `CORS_ALLOWED_ORIGINS` is in the converter's mounted Key Vault appsettings JSON; set it to the allowed portal origins and, when needed, `http://localhost:3000`. `EVENTHUB_NAMESPACE`, `EVENTHUB_NAME`, `RESPONSE_EVENTHUB_NAME`, `COSMOS_DATABASE`, and the service endpoints are in the worker appsettings JSON. `EVENTHUB_TRANSPORT_TYPE=AmqpWebSockets` can be added to that JSON if the cluster requires WebSockets.

The worker identity needs **Azure Event Hubs Data Receiver** on `aimie-request-queue`, **Azure Event Hubs Data Sender** on `aimie-response-queue`, **Storage Blob Data Contributor** on all three containers, Cosmos DB data-plane read/write access to the `requests` container, and access to the worker appsettings Key Vault secret. The separate converter identity only needs access to its converter appsettings secret. Provision the four consumer groups with [provision-consumer-groups.sh](scripts/provision-consumer-groups.sh) before starting the Deployments. Use both workload identity client IDs, the tenant ID, and the environment's Key Vault name in the manifests.

## Build and rollout

The [Build and test workflow](.github/workflows/ci.yml) restores packages, builds, runs tests, and checks coverage. Azure image publishing and deployment run only in a private repository. When the private repository variable `ENABLE_AZURE_DEPLOYMENTS` is `true`, CI on private `main` pushes immutable `sha-<commit>` images and calls [Deploy AKS test](.github/workflows/deploy-aks-test.yml). [Deploy AKS production](.github/workflows/deploy-aks-production.yml) validates a selected commit on `main`, builds both images, and deploys them using the `aks-production` environment. Set the deployment variables listed in [configuration review](docs/test-configuration-review.md) before running either workflow; there are no defaults pointing at live resources.

The worker uses `DaatPhoenix.HttpOrchestration` version `1.0.2` from the public File Operations source. Workflows default `PACKAGES_SOURCE_URL` to `https://nuget.pkg.github.com/probonolaw/index.json` and use `GITHUB_TOKEN` with package read permission; grant the consuming repository Actions access to the package. `PACKAGES_READ_USERNAME` and the secret `PACKAGES_READ_TOKEN` are optional credential overrides. Local restore uses `PACKAGES_SOURCE_URL` and `NuGetPackageSourceCredentials_github`; GitHub requires authentication even for public NuGet packages. See the [package publication and access guide](docs/http-orchestration-package.md) and [open-source readiness](docs/open-source-readiness.md).

`Dockerfile` builds all four workers. It requires BuildKit secrets named `github_packages_source` (the feed URL) and `github_packages_credentials` (`Username=<github-user>;Password=<token>;ValidAuthenticationTypes=Basic`). Keep both files outside the repository:

```bash
docker build \
  --secret id=github_packages_source,src=/path/outside/repo/package-source \
  --secret id=github_packages_credentials,src=/path/outside/repo/package-credentials \
  -t aimie-operations:local .
```

`Dockerfile.markdown-converter` builds the stateless API using NuGet.org dependencies only. Do not publish existing binaries, images, coverage reports, or release assets from the private repository; rebuild release artifacts from the sanitized public source.

To provision Azure resources, export the environment-specific variables before running the bootstrap scripts. Use `DEPLOYMENT_REPOSITORY` for the private deployment repository. Configure protected GitHub environments and the corresponding OIDC trust; the appsettings publisher uses a separate `APPSETTINGS_CLIENT_ID` with source-secret read access, resource read access, and permission to write the two target appsettings secrets. Bootstrap scripts do not provision this publisher identity. Run [Publish application settings](.github/workflows/configure-appsettings.yml) in the private repository to copy SharePoint variables into Key Vault alongside its existing client secret. Run it again when variables change, then restart the worker deployments to reload their settings.

Local appsettings generation and publication remain available through `scripts/generate-test-appsettings.py`, `scripts/generate-production-appsettings.py`, and the two `publish-*-appsettings.py` scripts. Generated JSON is private and excluded from Git and Docker. The publishers review by default and upload with `--apply`; specify the subscription and vault through their documented options or environment variables.

The four worker consumer groups start from the latest Event Hubs position without a checkpoint. Keep existing Logic App routes available until producers and response consumers are switched; disable overlapping routes during cutover to avoid duplicate generation. The separate email repository publishes normalized declaration and QA requests and consumes this repository's response contract.

Malformed events are saved to the failed-events Blob container before checkpointing. Transient infrastructure errors stop the worker without checkpointing, so Kubernetes restarts it and Event Hubs redelivers the event. New Cosmos request IDs use the deterministic operation ID; the workers also look up legacy partition-sequence IDs to resume existing records. Results and notifications can still be delivered more than once after a crash, so downstream consumers must deduplicate by response ID.
