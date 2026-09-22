# Open Vox Keys

<p align="center"><img src="assets/app-icon.png" width="160" alt="Open Vox Keys: a speech bubble above a keyboard key"></p>

**Hold Ctrl+Win, speak, release both keys. Your words appear where you were typing.**

A configurable Windows dictation app for ASR running on your own PC, another
machine on your network, or a cloud provider. No account or subscription to this
project is required. You supply the transcription endpoint; model weights are
not included.

**0.3.0-beta.1 — Windows preview.** The preceding preview is in daily use.
This is an unsigned early release, not a claim of compatibility with every app.
macOS and Linux clients are planned; they are not implemented.

## Install

For builds offering `OpenVoxKeys-<version>-Setup.exe`, double-click the installer
and choose **Install**. It includes an autostart checkbox, a Start menu shortcut,
and an entry in Windows **Installed apps** for removal. No terminal commands are
needed. The setup is unsigned, like the portable app. The original beta.1 release
has only the portable ZIP; the graphical installer is new in beta.2.

Download the Windows x64 ZIP from [Releases](https://github.com/christianhaberl/open-vox-keys/releases),
verify its SHA-256 against the accompanying checksum file, and extract it.
Run `OpenVoxKeys.exe` directly, or install for your Windows user:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\Install.ps1
```

No administrator or separate .NET runtime is needed. The installer creates a
Start menu shortcut. Optional `-Autostart` enables launch at sign-in. Quit the
app from its system tray menu before updating. The package is not code-signed;
Windows may display an unknown-publisher warning.

## Configure your transcription provider

The first launch opens **Settings**. You can reopen them from the system tray.

| Setting | File transcription | Streaming with sections |
|---|---|---|
| `GatewayUrl` | Leave empty | Base URL of the [reference gateway](docs/backend.md) |
| `GatewayApiKey` | Leave empty | Gateway bearer token, if configured |
| `FallbackUrl` | Full transcription URL | Optional independent file-ASR fallback URL |
| `FallbackModel` | Provider's model ID | Fallback provider's model ID |
| `FallbackApiKey` | Provider's bearer token, if required | Fallback provider's bearer token |
| `Language` | Model-supported language code, e.g. `en` or `de` | Same; segmentation language must also be supported |

For example, a local file endpoint can be
`http://127.0.0.1:8961/v1/audio/transcriptions`. A remote or cloud endpoint uses
its provider's full HTTP(S) transcription URL. The client sends a WAV and the
fields `model`, `language`, and `response_format=json`, and expects `{"text":"..."}`.
OpenAI-compatible file transcription is supported; arbitrary provider-specific
APIs and realtime WebSocket protocols require an adapter. Use HTTPS or a trusted
private encrypted network for remote traffic. Cloud providers can charge for use.

**File mode** records the complete dictation and uploads it on release.
**Streaming mode** sends audio during capture, shows live Voxtral text, and runs
Whisper on sections selected by pauses plus punctuation. The final transcript is
inserted once. If section transcription fails, the gateway can use the complete
Voxtral transcript; if the gateway fails, the client can send the retained original
recording to its configured file endpoint. No LLM synthesis is involved.

## Daily use

- Hold **Ctrl+Win** to record; release **both** keys to finish. **Esc** cancels.
- The transparent, click-through overlay follows your microphone level. Live
  text is available with the streaming gateway and may lag on short sentences.
- Keep the destination focused. If focus or keyboard/mouse input changes, use
  **Last dictation → Copy** instead of automatic insertion.
- Choose the microphone in **Settings**; the tray **Microphones** list shows IDs.
- `KeepFallbackWarm` controls the optional loopback Parakeet adapter only.
- Uninstall with the installed `tools\Install.ps1 -Uninstall`. Settings and
  recordings are deliberately retained; delete them yourself if no longer needed.

Audio normally stays in memory. Failed dictations may be saved in
`%LOCALAPPDATA%\OpenVoxKeys\Recovery` for manual recovery. Test recordings remain
in `%LOCALAPPDATA%\OpenVoxKeys\Tests`. Saved API keys use Windows DPAPI, bound to
your Windows user; older plaintext keys migrate on load. This does not protect
against software already running as your user. Settings cannot be copied with
working keys to a different user or PC; enter keys again there.
The state/timing log is `%LOCALAPPDATA%\VoicePoc.log` (legacy compatibility name).
The selected backend receives audio and controls its own retention policy.

## Build and test

Requires Windows and the .NET 10 SDK. Clone this repository anywhere; no parent
repository, private infrastructure, recordings, or model weights are needed.

```powershell
dotnet restore --locked-mode
dotnet build -c Release --no-restore
powershell -File .\tools\Test.ps1
powershell -File .\tools\Publish.ps1
powershell -File .\tools\BuildSetup.ps1
```

Output: `dist\OpenVoxKeys-win-x64.zip`, `dist\OpenVoxKeys-<version>-Setup.exe`,
and SHA-256 checksum files. See
[development](docs/development.md), [backend setup](docs/backend.md),
[release process](docs/releasing.md), and [changelog](CHANGELOG.md).

## Model comparison tools

The tray **Test mode** opens a guided recording/comparison window with editable
English prompts and a language setting. It sends the same recording to selected
models **serially**, optionally warming each first. Keys stay in memory in this
window. HTTP response timing does not equal hotkey-release latency. Correct the
reference to what you actually said; this window does not calculate WER.

For capture only, run `python tools/recording-web/serve.py` and open
`http://localhost:8097`. Read all prompts once and press **Done**. The recording
is saved locally; comparisons are a separate step. Microphone access requires
localhost or HTTPS. See [development](docs/development.md) for storage details.

## Limits

Windows x64 is the validated client target. Insertion uses Unicode input and may
be rejected by elevated applications or custom text controls. Browser caret/DOM
changes cannot all be detected. The shortcut is currently fixed and passively
observed; overlapping shortcuts in other apps must be reconfigured there.
Recordings are limited to ten minutes. The reference gateway accepts one active
session; additional clients need their own fallback. Cancellation stops client
requests, but already-running inference can continue remotely. Duration-limited
audio cuts can split a word; sample coverage tests do not prove perfect boundaries.
Local inference speed and hardware requirements depend on the selected models.
There is no auto-updater, meeting recording, or cross-platform client yet.

## License and contributions

Project code and supplied artwork are offered under the [MIT license](LICENSE).
Third-party code, dependencies, runtimes, and model weights keep their own licenses;
see [third-party notices](THIRD-PARTY-NOTICES.md). The icon was supplied by the
maintainer and approved for this app; no exclusivity or trademark clearance is
claimed. See [contributing](CONTRIBUTING.md) and [security](SECURITY.md).
