# Public release candidate audit

Candidate: **0.3.0-beta.1**. Visibility remains private until owner approval.

## Prepared

- English UI, README, backend setup, changelog, contribution/security guidance,
  issue templates, and public-facing issue descriptions.
- Product-named executable; SemVer prerelease; embedded app/tray icon.
- MIT project license and attribution for bundled third-party components and
  the separately licensed audio.cpp patch. Model weights are not bundled.
- Windows-user DPAPI storage, tested plaintext migration, atomic writes, and
  preservation of corrupt encrypted settings instead of overwriting them.
- First-run settings and direct file-ASR operation without a streaming gateway.
- Locked NuGet dependencies, pinned CI actions, Windows/backend tests, ZIP and
  SHA-256 packaging. Release publication remains manual.
- No built-in private endpoint; config examples contain loopback/placeholders only.
- Private archive of the original development history and issue descriptions.
  The public candidate starts from a clean root commit. The old private `main`
  must be replaced with this candidate before making the repository public.

## Validation and boundaries

See `release-validation.json` and the candidate's GitHub Actions run. Client
checks do not send microphone audio, call cloud models, or inject keyboard input.
Previous microphone/hotkey acceptance applies to the private preview; the
candidate keeps insertion and passive shortcut behavior, with translated UI.

Secret-pattern and private-path scans supplement manual review; they are not a
complete security audit. Replacing reachable Git history does not erase provider
caches or guarantee old object URLs disappear. Historical findings were private
machine addresses and development assumptions, not identified live credentials.

The package is unsigned, Windows x64 only, and a prerelease. Models/services are
external dependencies. Local Parakeet setup still requires an independently
installed compatible NeMo/CUDA environment. It is an optional adapter, not a
bundled Windows inference runtime. The reference gateway handles one session.

## Explicit final approval

Owner approval is required to replace old private `main` with the reviewed clean
candidate, change visibility to public, and publish the draft prerelease. MIT is
the proposed release license and is included in that approval. No CI workflow
performs any of these actions automatically.
