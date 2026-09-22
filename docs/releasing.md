# Release process

1. Update the project `Version` and `CHANGELOG.md`. Use `MAJOR.MINOR.PATCH`;
   append a prerelease suffix while the application is a preview.
2. Review dependencies, licenses, configuration examples, recordings, and secrets.
   A source scan is not proof that every possible secret has been found.
3. Run Windows and backend CI from the exact release commit. Review changes to
   keyboard handling and manually exercise them in an isolated window.
4. Build with `tools/Publish.ps1` and `tools/BuildSetup.ps1` from a clean checkout. Verify package tests,
   installation, executable version, embedded icon, and SHA-256. The ZIP includes
   the runtime and third-party notices. Do not package local settings or models.
5. Create a **draft prerelease** with the version, exact commit, Setup EXE, ZIP, checksums,
   validation evidence, and known limitations. Review the draft before publishing.
6. Publish only after maintainer approval. Repository visibility changes are a
   separate deliberate operation; no workflow changes visibility or publishes releases.

The current package is unsigned. Do not imply Authenticode signing, reproducible
byte-identical ZIPs, an SBOM attestation, or independent security certification.
Dependency locks and commit-pinned actions improve traceability; they do not pin
the hosted runner image or every operating-system component.

## Initial public release

Review all reachable Git history, issue bodies/comments, branch names, tags, and
release assets for private deployment details. Keep a private archive of the
original development history. The public candidate may begin with a clean root
commit; do not claim that rewriting branches erases cached provider objects.
Never switch a repository public before the owner explicitly approves the exact
candidate and any required history replacement.
