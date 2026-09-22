# Changelog

Versions follow Semantic Versioning. Prereleases may change configuration and APIs.

## 0.3.0-beta.2 — 2026-09-23

- Add a self-contained graphical Windows installer with autostart selection and Installed apps removal.
- Report extremely quiet input after ASR failure instead of sending unusable audio to a cold fallback.
- Reject empty gateway transcripts and try the configured file ASR once.
- Show a persistent error window for failed dictations, including empty ASR results.
- Detect missing microphone samples and digital silence before file fallback;
  record sample count, peak, and RMS diagnostics without logging speech content.
- Treat empty section-ASR results with detected speech as failures; use the
  complete streaming transcript or request file fallback.
- Add regression cases for empty results and silent microphone input.

## 0.3.0-beta.1 — 2026-09-14

- English first-run settings, tray UI, recording tools, and documentation.
- OpenVoxKeys executable name and user-supplied app/tray icon.
- User-bound Windows DPAPI encryption for saved keys, including migration.
- Direct file-ASR mode for local, network, and compatible cloud endpoints.
- Streaming gateway with pause/punctuation sections and complete-result fallback.
- Reproducible dependency lock, release packaging, checksums, CI, and contributor guidance.

## 0.2.1 — 2026-09-13 (private preview)

- App/tray icon deployed; microphone animation drawn in front of live text.
- Verified guarded Unicode insertion, cancellation, fallback, and per-user installation.

## 0.2.0 — 2026-09-13 (private preview)

- Streaming capture, section transcription, tray settings, and portable packaging.

## Earlier prototypes

Experimental hold-to-dictate and recording comparison tools. Not published releases.
