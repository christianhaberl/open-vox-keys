# Third-party notices

The project's MIT license does not replace third-party licenses.

| Component | Use | License / source |
|---|---|---|
| .NET 10 / Windows Desktop runtime | Bundled in self-contained Windows packages | MIT and third-party notices, [dotnet](https://github.com/dotnet/runtime) |
| NAudio 2.2.1 and its component packages | Windows audio capture | MIT, [NAudio](https://github.com/naudio/NAudio) |
| aiohttp | Optional Python gateway | Apache-2.0 and MIT, [aiohttp](https://github.com/aio-libs/aiohttp) |
| NumPy | Optional gateway audio processing | BSD-3-Clause, [NumPy](https://github.com/numpy/numpy) |
| ONNX Runtime | Optional VAD inference | MIT, [ONNX Runtime](https://github.com/microsoft/onnxruntime) |
| pySBD | Optional sentence segmentation | MIT, [pySBD](https://github.com/nipunsadvilkar/pySBD) |
| audio.cpp | Separately installed Voxtral server; patch included here | Apache-2.0, [audio.cpp](https://github.com/0xShug0/audio.cpp) |
| whisper.cpp | Separately installed section ASR server | MIT, [whisper.cpp](https://github.com/ggml-org/whisper.cpp) |
| Silero VAD through faster-whisper | Separately downloaded VAD model | MIT, [Silero](https://github.com/snakers4/silero-vad), [faster-whisper](https://github.com/SYSTRAN/faster-whisper) |

License texts for redistributed Windows components are in `licenses/`. The
`audio-cpp-stream-finalize.patch` modifies an Apache-2.0 source file from audio.cpp,
Copyright 2026 Shugo AI LLC. The modification adds finalization padding to drain
pending transcription tokens. Its Apache-2.0 license is included separately.

Whisper, Voxtral, Parakeet, NeMo, and any alternative model/provider have their
own model cards, licenses, and usage terms. No weights are distributed in this
repository or Windows ZIP. Download only from a source whose terms you accept;
check the exact revision and quantization you use. The app does not grant rights
to third-party trademarks or names.
