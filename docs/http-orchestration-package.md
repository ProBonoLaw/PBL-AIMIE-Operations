# HttpOrchestration package

`DaatPhoenix.HttpOrchestration` is built from the Apache-2.0-licensed source in
[ProBonoLaw/pbl-file-operations](https://github.com/ProBonoLaw/pbl-file-operations/tree/main/src/HttpOrchestration)
and version `1.0.2` is published to ProBonoLaw's GitHub Packages NuGet feed:

```text
https://nuget.pkg.github.com/probonolaw/index.json
```

The package ID and namespace remain unchanged. Operations now references
version `1.0.2`, the first version published from the public source. The earlier
cached `1.0.1` package contains private source URLs in its nuspec and assembly;
it must not be copied into the public registry. A new version also avoids
reusing earlier package contents from NuGet caches.

Publication was completed by [File Operations PR #3](https://github.com/ProBonoLaw/pbl-file-operations/pull/3)
and [workflow run 42084025311](https://github.com/ProBonoLaw/pbl-file-operations/actions/runs/42084025311).
The workflow passed package tests and inspection, published the package, and
verified an isolated restore from the registry against the original archive.
The [package page](https://github.com/orgs/ProBonoLaw/packages/nuget/package/DaatPhoenix.HttpOrchestration)
was verified without authentication on November 5, 2026: version `1.0.2` is
**Public**. The [initial AIMIE CI run](https://github.com/ProBonoLaw/PBL-AIMIE-Operations/actions/runs/42088263386)
restored it with this repository's `GITHUB_TOKEN` and successfully built both containers.
[Fork PR #1](https://github.com/ProBonoLaw/PBL-AIMIE-Operations/pull/1) also passed
package restore, all tests and both container builds with read-only workflow
permissions. Private deployment builds passed with their built-in token after
making legacy credential overrides an explicit opt-in.

## Publishing

The **Publish HttpOrchestration** workflow belongs in File Operations, alongside
the library source. It tests and packs the library, checks the public repository
URL and build commit, includes LICENSE and NOTICE, and rejects unexpected source
repository URLs in the package contents. Only a manual run from that repository's
`main` may publish, using `GITHUB_TOKEN` with `packages: write`.

After publishing, the workflow restores the package from the ProBonoLaw feed
into an empty package cache and compares the downloaded archive with the build
artifact. The public source repository's visibility does not automatically make
a new GitHub package public; an organization package administrator completed
that step for this package. Configure consuming repositories' Actions read
access in package settings and verify it from their workflows.

## Consuming

Operations workflows default `PACKAGES_SOURCE_URL` to the ProBonoLaw feed and
authenticate with their short-lived `GITHUB_TOKEN` and `packages: read`.
`PACKAGES_READ_USERNAME` and the secret `PACKAGES_READ_TOKEN` remain optional
overrides for an approved deployment credential. Do not copy private deployment
credentials into the public repository.

For a local restore, set `PACKAGES_SOURCE_URL` to the URL above and provide
`NuGetPackageSourceCredentials_github` using a personal access token (classic)
with `read:packages`. Keep the token in local secret storage, not in repository
files or command history. The username/password credential format is documented
in the root README. Docker uses the same URL and credential via BuildKit secrets.

GitHub requires authentication even for public NuGet packages. Publishing under
ProBonoLaw removes the need for access to the original private organization; it
does not enable anonymous NuGet downloads. Fork contributor access was verified by the PR linked above.

See [GitHub's NuGet registry documentation](https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-nuget-registry)
for supported credentials and package access configuration.

Package credentials default to `GITHUB_TOKEN` even when a legacy organization secret exists. To explicitly select the `PACKAGES_READ_TOKEN` secret, set `PACKAGES_USE_READ_TOKEN=true` and `PACKAGES_READ_USERNAME` in the consuming repository.
