# Backend integration

The Windows client uses a configurable streaming gateway plus an optional,
independent file-transcription fallback. Private endpoints and keys belong in
`%LOCALAPPDATA%\OpenVoxKeys\settings.json`, never in this repository.
Use the tray **Settings** menu to edit them; changes apply to the next dictation.

## Streaming path

`GatewayUrl` is an HTTP(S) base URL. The client connects to `/ws` via WS(S),
with `Authorization: Bearer ...` when `GatewayApiKey` is set. `/health` reports
stream/batch readiness and whether the single-session gateway is busy.

1. JSON `start`: language, segmentation options, timeouts, `stream_fallback`.
2. Binary frames: continuous mono **16 kHz PCM16 little endian**, no WAV header.
3. Text `stop`: finish capture and drain remaining processing.
4. Server events: `ready`, `delta`, `segment`, `section_result`, `warning`, then
   exactly one `result` or `error`. `result` contains complete text and source.

The optional reference gateway lives in `backend/`; it is independent of the
Windows client and has no dependency on the research repository. It uses:

- Voxtral Realtime via audio.cpp's HTTP streaming/SSE API for live text.
- Silero v6 ONNX on CPU for acoustic pauses.
- VAD **AND** punctuation, 0.5 s pause and 1.2 s lookahead by default. A 20 s
  duration bound is an additional fallback. Natural cuts lie inside observed
  silence; forced duration cuts can bisect a word and remain marked estimated.
- A persistent Whisper server receiving contiguous audio sections as WAV.
  Sections run serially while microphone capture and Voxtral continue.
- No synthesis. No partial transcription is inserted while shortcut keys are held.

The segmenter was carried forward from the project's previously tested voice
laboratory implementation, including abbreviation/ordinal lookahead and exact
sample coverage tests. Sample coverage does not prove perfect acoustic boundaries.

## Failure handling

If Voxtral fails, segmentation continues with VAD pauses and the duration limit;
Whisper can still produce the final transcript. If any Whisper section fails,
the **complete, successfully finalized Voxtral transcript** replaces the entire
Whisper result when `StreamFallback` is enabled. We deliberately do not splice
Voxtral words into audio sections using approximate text arrival timestamps.

If the gateway is unavailable, times out, is busy, or has no complete result,
the Windows client sends its retained **whole recording** to `FallbackUrl`.
This is a full OpenAI-compatible transcription URL, such as
`http://127.0.0.1:8961/v1/audio/transcriptions`. Multipart fields: `file`, `model`,
`language`, `response_format=json`; response: `{"text":"..."}`. Optional
`FallbackApiKey` is sent as a bearer token. It can also be an explicitly configured
cloud endpoint. No cloud endpoint or private machine address is built in.

The current order is primary sections → complete stream → file fallback.
The stream fallback can be disabled; endpoints and timeouts are configurable.
Arbitrary drag-and-drop fallback ordering is not implemented yet.

Cancellation closes the stream and cancels client requests. Remote in-flight
inference may finish despite disconnection, but cannot insert a late result.
Only one complete result is selected and then passed through existing Windows
focus/modifier/input guards. Audio stays in memory until processing ends and is
not routinely written to disk. If all backends fail, the Windows client saves a
recovery WAV under `%LOCALAPPDATA%\OpenVoxKeys\Recovery`; manual retry is possible.
Cancelled recordings are discarded. Recovery files remain until you delete them.

## Optional local CUDA adapter

`backend/local_parakeet.py` exposes a loopback OpenAI-compatible endpoint using
an existing NeMo environment and a local Parakeet `.nemo` file:

```sh
/path/to/nemo-env/bin/python backend/local_parakeet.py \
  --model /path/to/parakeet-tdt-0.6b-v3.nemo --port 8961
```

It loads on demand and releases model tensors after five idle minutes.
For loopback adapters, `KeepFallbackWarm=true` calls `/warm` to retain the model
instead. Generic remote/cloud providers receive no automatic warm-policy POST.
The Python process remains alive; CUDA context/framework overhead can remain
after tensors are released. Loading after a cold fallback takes considerably
longer than warm inference. Temporary WAV files are deleted after inference.
This adapter was tested on Windows through WSL/CUDA, not on a Surface NPU.
Other compatible adapters can use the same client configuration.

## Gateway setup

Use Python 3.12+ in a dedicated environment; install `backend/requirements.txt`.
Supply the existing faster-whisper-format Silero v6 ONNX model (`input,h,c`),
a persistent audio.cpp Voxtral Realtime service, and a persistent whisper.cpp
Vulkan service. `backend/config.example.json` documents their URLs.

```sh
python -m venv .venv
.venv/bin/pip install -r backend/requirements.txt
.venv/bin/python backend/server.py --config /path/to/private-config.json
```

By default bind to loopback. To serve your own PCs over Tailscale, bind to the
server's Tailscale address and restrict access with your tailnet policy; clients
need the same gateway address and credentials. Do not expose an unauthenticated
listener publicly. TLS/WSS can be supplied by your existing private reverse proxy.

The gateway accepts **one active dictation at a time**; a second simultaneous PC
receives an error and can use its own configured fallback. Portable backend runtime packaging and multi-session scheduling remain separate work.


## Fresh Linux reference installation

The Windows app also works with a single file ASR endpoint; this larger stack is
optional. The documented reference uses Vulkan for both ASRs, not an NPU or ROCm.

On Ubuntu with a working Vulkan driver, install build prerequisites:

```sh
sudo apt install build-essential git cmake ninja-build curl python3-venv python3-dev libvulkan-dev glslc libssl-dev
bash backend/setup-linux.sh "$HOME/.local/share/ovk-backend"
```

The destination must be empty. The script pins audio.cpp and whisper.cpp source
revisions, uses a dedicated Python environment, and applies the included Voxtral
EOF patch. This patch is required for the tested runtime revision: without it,
final words can be missing despite a successful response. See its Apache-2.0
attribution in `THIRD-PARTY-NOTICES.md`. Build dependencies and GPU drivers remain
host responsibilities; the script neither installs services nor changes drivers.

Download model weights separately after reviewing their terms:

- Voxtral Q4_K: [Mistral's experimental AudioCPP GGUF repository](https://huggingface.co/mistral-experimental/AudioCPP-Voxtral-Mini-4B-Realtime-2602-GGUF/tree/4c3212f91349c32211172ce332f266a6e9501018),
  revision `4c3212f91349c32211172ce332f266a6e9501018`, file
  `voxtral-mini-4b-realtime-2602-q4_k.gguf`.
- Whisper: run `sh models/download-ggml-model.sh large-v3-turbo` in the pinned
  whisper.cpp checkout. Its upstream downloader manages the model URL; record
  the downloaded file hash for your deployment.
- Silero: the setup script downloads the pinned faster-whisper v6 asset and checks
  SHA-256. It requires the `input,h,c` interface, not the alternate `state` model.

Copy `backend/voxtral.example.json` and `backend/config.example.json` to private
configuration files outside your checkout. Set absolute model paths. Start the
three processes in separate terminals (replace the paths):

```sh
/path/to/ovk-backend/audio-build/bin/audiocpp_server --config /path/to/voxtral.json
/path/to/ovk-backend/whisper-build/bin/whisper-server -m /path/to/ggml-large-v3-turbo.bin -t 8 --host 127.0.0.1 --port 19115
/path/to/ovk-backend/venv/bin/python backend/server.py --config /path/to/gateway.json
```

Set the gateway `vad_model` to the downloaded Silero file. Check
`http://127.0.0.1:8960/health` before pointing the client at it. First inference
can take longer while models initialize. Service managers such as systemd are
optional and deployment-specific. Keep weights loaded for low latency, subject
to your available memory. The reference was exercised on an AMD Vulkan Linux
host; this is not a hardware-independent performance guarantee.

Client `Language` also selects the pySBD sentence rules. Unsupported sentence-rule
languages produce an error; use file mode or add/test suitable segmentation rules.
The English UI does not constrain transcription to English.

### Completeness and capture bounds

The reference gateway accepts a streaming final only after the client has stopped
and every queued audio frame has reached the streaming upload iterator's EOF.
A final emitted earlier is incomplete and cannot be used as a fallback.
Batch completion timeouts may use an already complete streaming transcript.

VAD controls section boundaries, not whether recorded audio may be discarded.
Every section containing nonzero PCM reaches batch ASR, even when VAD labels it
non-speech. Only digital-zero sections skip batch inference. This favors retaining
quiet speech; background noise can still cause ASR hallucinations and is not
claimed to be reliably distinguished from speech.

A capture releases its exclusive gateway slot after 15 seconds without an audio
or stop message, or after 660 seconds of wall time. Operators can set
`capture_idle_seconds` and `capture_max_seconds` in the gateway configuration.
The independent PCM limit remains ten minutes. These are capture limits; final
ASR processing uses the separate client-provided completion timeouts.

### Streaming-only output

Set `"batch_enabled": false` in the gateway configuration and restart the gateway
to use the complete Voxtral stream as the final output. The gateway sends no
audio to the batch backend in this mode; live preview remains available. The
Windows client does not need an update. The default is `true` (batch sections
with complete-stream fallback). A configured client file-ASR fallback still
applies if streaming fails. Remove its URL in client Settings if strict
single-model operation is required.
