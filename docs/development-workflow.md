# Development and synchronization

[ProBonoLaw/PBL-AIMIE-Operations](https://github.com/ProBonoLaw/PBL-AIMIE-Operations)
is the source of truth. Its history starts with a sanitized snapshot; earlier
commits and deployment configuration stay in the private repository.

## Developer setup

Use a fresh public clone, without reusing the old private Git directory:

```bash
git clone https://github.com/ProBonoLaw/PBL-AIMIE-Operations.git
cd PBL-AIMIE-Operations
git config --local remote.pushDefault origin
git config --local push.default current
git config --local push.autoSetupRemote true
gh repo set-default ProBonoLaw/PBL-AIMIE-Operations
```

Set a GitHub noreply author email if you want to keep your personal address private.
`origin` must have only the public URL. Remove conflicting push URLs, URL rewrites,
and branch push overrides in existing configuration. These defaults are per clone.

Create feature branches and open pull requests against public `main`. The branch
requires tests, both container builds, and the secret/identifier scan. Force pushes
and branch deletion are blocked. Contributors use the usual fork and PR flow.

## Accepted changes

A private scheduled workflow checks public `main` every five minutes; GitHub may
delay scheduled runs. It also supports a manual run. It waits for the configured
public checks to pass, then merges the public commit into a private integration
branch. A private PR and explicitly dispatched CI validate that merge. On a later
run, successful private checks allow the PR to merge without bypassing branch rules.
Only accepted public `main` commits are imported; feature branches are not fetched.

The controller runs from private `main`. Git pushes use a deploy key scoped to
that private repository, stored in a protected private GitHub environment. API
calls and PR merges use the short-lived repository `GITHUB_TOKEN`. This allows
workflow files to be imported without granting any private write credential to
the public repository. The controller paths and state are private-only, and imports refuse a public tree
containing those paths. It fetches Git objects without executing public scripts.
Concurrent imports are serialized. A recorded public commit detects history
rewrites; merge conflicts stop the import without changing private `main`.
Private-only changes and both histories are retained. Already imported commits
produce no new update. No force push or reverse synchronization is used.

The controller explicitly dispatches CI on integration branches when no matching
run exists. PR merges made with `GITHUB_TOKEN` do not start normal push workflows,
so it also dispatches private CI after merging. Azure deployment additionally requires private repository
variable `ENABLE_AZURE_DEPLOYMENTS=true`; keep it disabled until deployment
variables and identities have been verified. Source synchronization itself does
not require an Azure identity or any private credentials in the public repository.

The initial connection is established privately by merging the new public root
with the retained private history. Developer clones never receive that history.
