# Development and prototype verification

The current Windows implementation uses C#/.NET 10 and WinForms. Build from the
repository root with `dotnet build -c Release`. No other repository is required.

Existing switches: `--preview-overlay`, `--transcribe-file C:\path\clip.wav`,
`--selftest`, `--test-insertion`. Read their implementations before automating
interactive tests. The insertion test uses an isolated test window.

Previously verified: Windows Release build, real microphone-to-ASR path,
user-confirmed direct insertion, four shortcut press/release-order cases,
Windows INPUT ABI, Unicode including umlauts/surrogate pairs, and per-monitor
DPI overlay positioning without focus theft. These are historical prototype
checks, not a complete release acceptance suite.

An early synthetic-hotkey experiment triggered Windows shortcut side effects
and repeated text. The corrected implementation uses passive key observation
and Unicode packets only. Do not reintroduce synthetic Ctrl/Alt/Win presses or
releases in input tests. Test shortcut coexistence, focus changes and cancellation
before declaring a release usable.

State log: `%LOCALAPPDATA%\VoicePoc.log`. Build outputs and local configuration
are untracked. Keep private workspace instructions in the owning workspace;
this standalone repository does not depend on them to build or run.

## Streaming integration — 2026-09-13

Implemented: NAudio PCM capture, gateway WebSocket upload, live text and microphone
level overlay, per-user tray settings, delayed VAD+punctuation sections, whole-stream
fallback and independent local file fallback. Keyboard observation and Unicode
insertion guards are unchanged. Insertion remains one operation after release.

Verified on the development Windows PC against the Tailscale gateway:

- Release build: zero warnings/errors; shortcut/Unicode ABI self-test passed.
- Eleven Python tests: six boundary integrity/lookahead tests plus five gateway
  success/failure/disconnection tests, including no partial text after both failures.
- Paced 26-second recording through the actual Windows executable: four Whisper
  sections processed during capture; complete final result, exit 0.
- Real Whisper service stopped: paced 7-second recording selected
  `voxtral-fallback`, exit 0. Service restored afterward.
- Gateway pointed at an unreachable address: same 7-second recording selected
  `local-fallback` through the loopback WSL/CUDA Parakeet server, exit 0.
  Approximately 42 seconds after capture for this cold import/model startup;
  this is a functional smoke test, not a new latency benchmark.
- Normal gateway settings restored; both remote backends healthy. Local fallback
  defaults to load on demand with five-minute idle tensor release.

The user has now confirmed the live microphone/hotkey path and is using the
streaming release productively. Subsequent client protocol hardening and overlay
foreground changes are independently tested before updating the running build.
The historical user-confirmed input tests above apply to the unchanged insertion
implementation, not proof that every application accepts this new build.
Current gateway permits one active dictation; simultaneous clients fall back.
The guided model-comparison window remains separate from the typing client.

## Client review and portable package

Seven loopback Windows protocol tests (`--test-session`) verify complete-primary
selection, wrong sample counts, premature finalization, disconnection, explicit
backend failure, cancellation without fallback, exact original fallback audio,
direct file-ASR operation, and harmless late audio callbacks after disposal. Result JSON is saved under
`%LOCALAPPDATA%\OpenVoxKeys\session-tests.json`; no real ASR, microphone or
keyboard input is used by these tests.

Overlay preview confirms unchanged foreground focus and draws the animation
after the live-text box. Live-preview latency remains unchanged by user request.
Microphone selection and optional per-user Windows autostart are in settings.
Self-contained Windows x64 publishing and per-user installation scripts are
under `tools/`; no .NET runtime or SDK is needed on a destination PC.


## Public candidate checks

`tools/Test.ps1` runs `--selftest` (shortcut transitions, Unicode ABI, icon,
DPAPI roundtrip, plaintext migration, corrupt-data preservation, and invalid URL
rejection) and `--test-session` (seven loopback protocol/fault cases). These do
not record, call models, or inject keyboard input. `tools/Publish.ps1` produces a
self-contained ZIP and SHA-256 file. CI runs these checks on Windows and the
Python boundary/gateway suite on Linux without private infrastructure.

The browser recorder writes to `%LOCALAPPDATA%/OpenVoxKeys/Tests` on Windows,
`~/.local/share/OpenVoxKeys/Tests` elsewhere, or `OVK_RECORDINGS` if explicitly
set. English reading scripts are suggestions, not verified ground truth.
German strings in Unicode and segmentation tests are intentional multilingual
fixtures. `VOICE_POC_*` environment names and the old mutex/log names remain for
compatibility with private previews; they do not select a private endpoint.


## 0.3.0-beta.2 reliability and setup checks

The client protocol suite now contains eleven cases, including empty primary
results, both backends returning empty text, digital silence and unusably quiet
input. The backend suite contains thirteen tests. Empty primary output triggers
a configured fallback; exhausted failures produce a persistent error window.
Extremely quiet input is rejected only after the primary fails.

`tools/BuildSetup.ps1` embeds the portable package into a self-contained GUI
installer and executes its payload checks. Windows CI additionally exercises
`tools/TestInstall.ps1` on a disposable runner: installation, autostart on/off,
configuration preservation, removal and personal-data retention. That destructive
lifecycle test refuses to run over an existing installation/settings file.

The beta.2 GUI was used to upgrade the development workstation; installed
version, unchanged settings, Start menu shortcut, Run key and Installed apps
registration were verified. An ensuing dictation logged a complete primary
result and input submission. This does not claim a reboot test, universal input
compatibility, or a completed GUI-uninstall test on that workstation.
