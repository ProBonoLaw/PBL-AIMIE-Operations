# Open-source release review

The release uses a new public repository with a sanitized initial commit, Apache
2.0 licensing, and public-appropriate commit metadata. Original history, old
branches, deployment settings, logs and artifacts are retained privately and are
not transferred. Rebuilt artifacts must use the public source.

## Release controls

- No credential files, personal tool configuration or generated settings are
  tracked. Developer-only values live in ignored `.local/` files.
- SharePoint tenant, client, hostname and site prefix come from the private GitHub
  environment variables described in [deployment configuration](test-configuration-review.md).
  The client secret remains in Key Vault.
- Azure resource identifiers and hostnames are explicit configuration inputs.
  Public workflows test and build without receiving private deployment values.
- `DaatPhoenix.HttpOrchestration` 1.0.2 is rebuilt from public Apache-2.0 source and
  hosted publicly in ProBonoLaw GitHub Packages. Its original package ID and legal
  attribution are retained. See the [package guide](http-orchestration-package.md).
- Required public checks cover unit tests, coverage, configuration, container
  builds, source identifiers and secret scanning. Branch protection requires PRs
  and blocks force pushes and deletion.
- The [development workflow](development-workflow.md) explains public push defaults
  and private, one-way synchronization of accepted changes.

## Private operational follow-up

The owner will rotate the previously committed credential after the transition
work. Removing it from current source does not revoke it; archived private history
continues to contain earlier configuration. No earlier Git history is published.

Keep Azure deployment disabled until the private environment variables and OIDC
identities have been checked. Setting GitHub variables alone does not update
running pods: publish appsettings and validate the Test environment before a
production rollout. Live SharePoint writes and Azure deployment require separate
integration verification; passing unit tests does not verify those services.

Public NuGet downloads still require GitHub authentication. A contributor can use
a token with `read:packages`; workflows use their repository token. A successful
same-repository build does not by itself prove a fork's token has package access.
