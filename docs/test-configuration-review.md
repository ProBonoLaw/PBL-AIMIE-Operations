# Deployment configuration

Keep deployment values in the private repository's GitHub environments. Variables are not secret storage and are not automatically masked in logs. Public Actions must not receive private deployment values, Azure credentials, private package credentials, or synchronization credentials. Public package restores use a short-lived `GITHUB_TOKEN` with `packages: read`. The Azure workflows require a private repository on `main` and repository variable `ENABLE_AZURE_DEPLOYMENTS=true`. This opt-in remains disabled during the source transition.

## SharePoint

The following four variables are required separately in `aks-test` and `aks-production`:

| GitHub environment variable | Meaning |
| --- | --- |
| `SHAREPOINT_TENANT_ID` | Tenant of the Graph application |
| `SHAREPOINT_CLIENT_ID` | Graph application's client ID |
| `SHAREPOINT_HOST` | SharePoint hostname only, without a scheme or path |
| `SHAREPOINT_SITE_PREFIX` | Site path prefix, typically `/sites` or `/teams` |

[Publish application settings](../.github/workflows/configure-appsettings.yml) explicitly maps `${{ vars.SHAREPOINT_* }}` into the generator's environment. Both generators take these values only from that environment and write them into `aimie-operations-appsettings` in Key Vault. Workers read that JSON at `/mnt/keyvault/appsettings.json`; setting a GitHub variable alone does not update a running pod. Publish the settings and restart the worker deployments after changing them.

`SHAREPOINT_CLIENT_SECRET` stays in Key Vault, never in GitHub Variables. Test reads it from `file-operations-appsettings`. Production reads `GRAPH_KEYVAULT_URI` and `GRAPH_SECRET_NAME` from `account-operations-appsettings`, then retrieves the referenced secret. Ensure that this credential belongs to the tenant/client configured in the four variables. If replacing this with GitHub credential storage later, use a GitHub Secret.

For local generation, export the same four variables in the shell. Test fixtures use `sharepoint.example.test`; they contain no deployment hostname. SharePoint type names and Microsoft Graph protocol URLs describe the integration and remain in the source.

## Other variables

| Scope | Variables |
| --- | --- |
| Package builds | ProBonoLaw feed and `GITHUB_TOKEN` by default; optional overrides `PACKAGES_SOURCE_URL`, `PACKAGES_READ_USERNAME`, and secret `PACKAGES_READ_TOKEN` |
| `test` image publishing | `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`, `ACR_NAME`, `ACR_LOGIN_SERVER` |
| Each AKS environment | `AKS_DEPLOY_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_WORKLOAD_IDENTITY_CLIENT_ID`, `AZURE_CONVERTER_WORKLOAD_IDENTITY_CLIENT_ID`, `AKS_RESOURCE_GROUP`, `AKS_CLUSTER_NAME`, `KEY_VAULT_NAME`, `ACR_LOGIN_SERVER`, `MARKDOWN_CONVERTER_INGRESS_HOST`, `MARKDOWN_CONVERTER_TLS_CERT_KEYVAULT_URI` |
| `aks-test` subscription | `AZURE_SUBSCRIPTION_ID` |
| `aks-production` build and subscription | `AZURE_PRODUCTION_SUBSCRIPTION_ID`, `ACR_NAME`; package build variables and secret above |
| Appsettings publisher in each AKS environment | `APPSETTINGS_CLIENT_ID`, `STORAGE_RESOURCE_GROUP`, `STORAGE_ACCOUNT`, plus subscription, tenant, vault, and SharePoint variables above |
| Test settings generation | `COSMOS_ENDPOINT`, `COSMOS_DATABASE`, `EVENTHUB_NAMESPACE` (fully qualified namespace), `CORS_ALLOWED_ORIGINS` |
| Production settings generation | `COSMOS_RESOURCE_GROUP`, `COSMOS_ACCOUNT`, `PORTAL_RESOURCE_GROUP`, `PORTAL_WEB_APP` |
| Shell bootstrap scripts | `DEPLOYMENT_REPOSITORY`, `IDENTITY_RESOURCE_GROUP`, `AKS_RESOURCE_GROUP`, `AKS_CLUSTER_NAME`, `ACR_RESOURCE_GROUP`, `ACR_NAME`, `KEY_VAULT_NAME`, `STORAGE_RESOURCE_GROUP`, `STORAGE_ACCOUNT`, `COSMOS_RESOURCE_GROUP`, `COSMOS_ACCOUNT`, `EVENTHUB_RESOURCE_GROUP`, `EVENTHUB_NAMESPACE_NAME` (short resource name), and the appropriate subscription variable |

Only export the inputs used by the script being run. GitHub variables do not become local shell variables automatically. Production bootstrap scripts currently use `IDENTITY_RESOURCE_GROUP` for the production cluster resource group as well, so those resources must share a group. Direct deployment scripts require `AKS_SUBSCRIPTION_ID` to select the Azure subscription; workflows supply it explicitly. Local AIMIE and converter ingress URLs can be selected with `AIMIE_SERVICE_BASE_URL` and `MARKDOWN_CONVERTER_PUBLIC_BASE_URL` in the test generator.

Production obtains Cosmos/Event Hubs/notification settings from Azure and its source appsettings secret, and discovers CORS origins from the configured portal. Test seeds contain `${VARIABLE_NAME}` placeholders for environment-specific values; the generator resolves them before writing JSON. Internal Kubernetes service names, routes, queue names, and worker container names remain application defaults.

## Setup sequence

1. Populate the variables above in the private repository. Configure protected environments and repository/branch-specific Azure OIDC trust.
2. Provision an appsettings publisher identity with access to read the source secrets and resources and write the two target secrets. Set `APPSETTINGS_CLIENT_ID`. The bootstrap scripts do not create this identity.
3. Generate and publish worker and converter appsettings through the manual workflow or the local scripts. Local output is mode `0600` under ignored `.local/`; the workflow deletes generated files on exit and does not upload them as artifacts.
4. Run the AKS workload and deployment identity bootstrap scripts with explicit resource variables. Assign the resulting IDs to the appropriate environment. The converter identity should only read its own appsettings secret.
5. Build and deploy, then verify connectivity and Graph write access with an isolated test account.

Local publishing scripts accept `--file`, `--vault`, and `--subscription`; `KEY_VAULT_NAME` and `AZURE_SUBSCRIPTION_ID` supply the latter defaults. They only upload with `--apply`. For local production publication, pass the production subscription explicitly. The production generator uses `AZURE_PRODUCTION_SUBSCRIPTION_ID` or its `--subscription` argument.

Package credentials default to `GITHUB_TOKEN` even when a legacy organization secret exists. To explicitly select the `PACKAGES_READ_TOKEN` secret, set `PACKAGES_USE_READ_TOKEN=true` and `PACKAGES_READ_USERNAME` in the consuming repository.
