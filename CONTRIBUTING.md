# Contributing

Use English for code, UI, documentation, issues, and pull requests. Multilingual
ASR and Unicode test fixtures are welcome when their purpose is explicit.

For a focused change, describe the problem and resulting behavior, run the relevant
checks in `docs/development.md`, and open a pull request. Discuss larger architecture
changes in an issue first. Contributions are under the repository's MIT license.

Do not commit credentials, endpoints tied to a private deployment, recordings,
model weights, generated binaries, or private workspace instructions. Test model
adapters with synthetic or explicitly authorized audio. Keep cloud tests opt-in.

Never simulate Ctrl/Alt/Win presses or releases to hide system shortcuts. Preserve
passive hotkey observation, focus guards, cancellation, and complete-audio fallback.
Changes affecting text insertion need a controlled manual test in an isolated
window before a real target application. Avoid changing the user's running install
as a side effect of builds or automated tests.


## Private workspace material

Keep personal agent instructions (`AGENTS.md`, `CLAUDE.md`), agent state,
memories, deployment settings, and recordings outside this public repository.
These common paths are ignored as a safeguard, including `.claude/`, `.codex/`,
`memory/`, and root `private/`. Use `*.local.json`, `*.local.cmd`,
`*.local.ps1`, or `*.local.sh` for private configuration and launchers.

A separate private workspace can own research notes and deployment instructions
while this repository remains independently cloneable. No extra repository is
required just to run or contribute to the app. Git ignore rules are not a secret
scanner: they do not untrack existing files, prevent forced adds, or decide whether
arbitrarily named content is safe. Review staged changes before committing.
