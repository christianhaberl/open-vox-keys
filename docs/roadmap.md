# Windows release and later work

## Implemented and verified

- Hold Ctrl+Win, microphone capture, release-to-insert, Esc cancellation.
- Transparent per-monitor overlay, voice-reactive animation, live preview.
- Configurable gateway and OpenAI-compatible file ASR; optional bearer keys.
- Voxtral + Silero + punctuation sections, Whisper final text, stream/file fallbacks.
- Focus/modifier/input guards and complete-recording validation before insertion.
- Tray settings, microphone selection, optional per-user autostart.
- Self-contained Windows x64 package, per-user installer, uninstall retaining data.
- Recovery WAV after total backend failure; diagnostics avoid transcript contents.
- User-confirmed productive microphone/hotkey/insertion use on the development PC.

## Known boundaries / later enhancements

- Live text is noticeably delayed for short sentences; user explicitly accepts
  the current behavior and defers tuning.
- One active gateway dictation at a time; simultaneous clients use their fallbacks.
- The independent CUDA fallback can be kept warm, or load on demand with a slow
  cold start. Other PC/NPU adapters (including Surface) are later work.
- Unicode insertion is verified for the user's current application path and an
  isolated control. Applications with elevated permissions, unusual editors or
  custom input handling may require manual copying. No universal application claim.
- Hotkey remains Ctrl+Win; a generic shortcut editor is later work.
- Recovery retry is manual, and updates are manually installed. Signed installer,
  automatic updates and broader application/device qualification are later release work.

## Repository and publication

Product/repository name: Open Vox Keys / open-vox-keys. The public candidate uses
MIT; visibility changes and release publication require explicit owner approval.
Name selection is not trademark clearance. See `PUBLIC-READINESS.md` for the
candidate audit and `releasing.md` for the release process.

macOS/Linux clients, meeting detection/notetaking, local synthesis and Surface/NPU
backends remain outside this Windows iteration.
