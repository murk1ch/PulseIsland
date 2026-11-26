---
name: onnx-runtime-tokenizers
description: >-
  Use this skill when implementing or debugging PulseIsland's zero-NuGet native
  ONNX Runtime C-API wrapper (OrtNativeRuntime.cs) for both offline MarianMT
  translation (SentencePieceMarianTokenizer.cs) and PP-OCRv5 Mobile neural OCR
  (PaddleOcrOnnxService.cs, OcrImagePreprocessor.cs).
---

# Zero-NuGet ONNX Runtime, MarianMT & PP-OCRv5 Neural OCR Skill

## 1. Native ONNX Runtime Loader (`Translation/OrtNativeRuntime.cs`)
PulseIsland compiles with `FullSdk=false` (`net8.0-windows`) without requiring `Microsoft.ML.OnnxRuntime` or `Microsoft.ML.Tokenizers` NuGet packages:
- Loads `<portable-root>\models\_runtime\onnxruntime.dll` (ORT **1.20.1**) explicitly via `NativeLibrary.Load` — **never** rely on `System32\onnxruntime.dll` (Windows ships an incompatible legacy build in `System32`).
- Calls `OrtGetApiBase()->GetApi(17)` (falling back to `14`) and binds verified ORT 1.20.1 `OrtApi` vtable indices `[1..100]` using `[UnmanagedFunctionPointer(CallingConvention.Cdecl)]` delegates.
- `EnsureRuntimeAvailableAsync` bootstraps the MIT-licensed `Microsoft.ML.OnnxRuntime` `.nupkg` into `cache\downloads\` and extracts `runtimes/win-x64/native/*` into `models\_runtime\` on demand.
- Exposes two reusable session wrappers:
  - `OrtSessionHolder` — encoder/decoder greedy seq2seq inference (`RunGreedySeq2Seq`) for MarianMT translation.
  - `OrtFloatSession` — general-purpose single-model `float32` tensor inference (`RunFloat`) for PP-OCRv5 detection and recognition.

## 2. Offline MarianMT Translation (`Translation/SentencePieceMarianTokenizer.cs`, `OnnxOfflineTranslationService.cs`)
- **Tokenizer:** Parses `source.spm` protobuf (`ModelProto.pieces`) for Unigram Viterbi segmentation with single-character fallback; loads `vocab.json` (shared source + target subword dictionary) and `config.json` (`decoder_start_token_id`, `eos_token_id`, `pad_token_id`).
- `PreprocessInputText` normalizes typographic quotes/apostrophes, collapses repeated punctuation, and normalizes ALL-CAPS so cased Opus-MT models do not fragment words.
- **Required files in `models\<model-id>\`** (flat directory, no nested `onnx/`):
  `encoder_model.onnx`, `decoder_model.onnx`, `source.spm`, `vocab.json`, `config.json`, plus `pulseisland_manifest.json`.
- Splits long text into chunks (`<= 240` chars) and runs `OrtSessionHolder.RunGreedySeq2Seq`. If dictionary files are missing and network access fails, throw a clear `InvalidOperationException` rather than silently degrading.

## 3. PP-OCRv5 Mobile Neural OCR (`Translation/PaddleOcrOnnxService.cs`, `Translation/OcrImagePreprocessor.cs`)
- Runs 100% offline on `OrtNativeRuntime.OrtFloatSession` using official PaddleOCR **PP-OCRv5 Mobile** ONNX models stored in `<portable-root>\models\_ocr\`:
  - `ppocrv5_det.onnx` — DBNet++ text line detector
  - `ppocrv5_eslav_rec.onnx` + `ppocrv5_eslav_rec.yml` — SVTR East-Slavic Cyrillic/Latin/Math/Handwriting recognizer & YAML character dictionary
  - `ppocrv5_en_rec.onnx` + `ppocrv5_en_rec.yml` — SVTR English recognizer & YAML character dictionary
- `PaddleOcrOnnxService.DownloadModelsAsync` fetches models on demand via `HuggingFaceDownloader` into `models\_ocr\` (staging `.part` in `cache\downloads\_ocr\`).
- `OcrImagePreprocessor.BuildVariants` prepares adaptive pure-C# image variants (`norm-contrast`, `rgb-clean`, `fg-cluster`, `chroma-dist`, `adaptive-bin`, plus `downscale-giant` / `upscale-tiny` specialists) to handle dark/light themes, colored-on-colored text, tight lasso crops, and tiny 6pt or giant display glyphs.
