#!/usr/bin/env bash
# Optional reference stack. No service installation or public listener changes.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")" && pwd)"
DEST="${1:?Usage: bash backend/setup-linux.sh /absolute/empty/directory}"
mkdir -p "$DEST"
DEST="$(cd "$DEST" && pwd)"
if [ -n "$(ls -A "$DEST")" ]; then echo 'Destination must be empty.' >&2; exit 1; fi
for command in git cmake make c++ python3 curl; do command -v "$command" >/dev/null; done
python3 -m venv "$DEST/venv"
"$DEST/venv/bin/pip" install -r "$ROOT/requirements.txt"
git clone https://github.com/0xShug0/audio.cpp.git "$DEST/audio.cpp"
git -C "$DEST/audio.cpp" checkout --detach 5bea9c726881f6a7ce3e9adf18c060b5a6a8eb8e
git -C "$DEST/audio.cpp" submodule update --init --recursive
git -C "$DEST/audio.cpp" apply --check "$ROOT/patches/audio-cpp-stream-finalize.patch"
git -C "$DEST/audio.cpp" apply "$ROOT/patches/audio-cpp-stream-finalize.patch"
(cd "$DEST/audio.cpp"; bash scripts/build_linux.sh --backend vulkan --build-type Release --build-dir "$DEST/audio-build" --jobs "${OVK_BUILD_JOBS:-4}")
git clone https://github.com/ggml-org/whisper.cpp.git "$DEST/whisper.cpp"
git -C "$DEST/whisper.cpp" checkout --detach 1da4dc82fa7996d4edda05890dca65aeceaafd6d
git -C "$DEST/whisper.cpp" submodule update --init --recursive
cmake -S "$DEST/whisper.cpp" -B "$DEST/whisper-build" -DCMAKE_BUILD_TYPE=Release -DGGML_VULKAN=ON -DWHISPER_BUILD_SERVER=ON
cmake --build "$DEST/whisper-build" --parallel "${OVK_BUILD_JOBS:-4}" --target whisper-server
mkdir "$DEST/models"
curl --fail --location 'https://raw.githubusercontent.com/SYSTRAN/faster-whisper/ed9a06cd89a93e47838f564998a6c09b655d7f43/faster_whisper/assets/silero_vad_v6.onnx' -o "$DEST/models/silero_vad_v6.onnx"
echo "914fd98ac0a73d69ba1e70c9b1d66acb740eff90500dfde08b89a961b168a6a9  $DEST/models/silero_vad_v6.onnx" | sha256sum --check
printf '%s\n' 'Runtimes built. Follow docs/backend.md to download ASR weights, configure, and start services.'
