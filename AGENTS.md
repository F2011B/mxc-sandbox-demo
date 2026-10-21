# MXC Sandbox Demo

Small .NET/Avalonia demo using the official Microsoft.Mxc.Sdk. Keep the scope minimal.

- The user authorized GitHub publication of anonymized source under MIT.
- Publish only the main branch after scripts/check-publication.py passes. Never push old local branches, tags, raw logs or build artifacts.
- Use Dark Light <darklight@noreply.com> for author/committer metadata. Never add personal names, account paths, machine identifiers or credentials.
- Do not weaken deny-by-default filesystem/network policy or silently fall back to uncontained execution.
- Keep Windows proxy disabled until an identity-bound peer is implemented and verified on Windows.
- No VM backend, alternate user account, or Docker runtime dependency. Docker is test infrastructure only.
- Update docs/security.md and docs/validation.md when containment behavior changes.
- Run scripts/test.sh for native host checks; Linux integration uses scripts/test-linux.sh.
- Commit through the installed git-commit-policy skill.
