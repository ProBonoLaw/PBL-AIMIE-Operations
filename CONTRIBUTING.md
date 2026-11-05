# Contributing

Open branches and pull requests in [ProBonoLaw/PBL-AIMIE-Operations](https://github.com/ProBonoLaw/PBL-AIMIE-Operations).
Use a GitHub noreply email if you do not want your email published in commit metadata.

Run the .NET solution tests and `python3 -m unittest discover -s tests/scripts -v`.
CI also builds both containers and scans for secrets and deployment identifiers.
Keep tenant IDs, hostnames, credentials, generated settings and customer data out of
source, commit messages, issues and logs. Use reserved example values in tests.
See the README for package authentication and configuration.

Submit shared changes through a pull request into `main`. Contributions are
provided under the repository's Apache 2.0 license.
