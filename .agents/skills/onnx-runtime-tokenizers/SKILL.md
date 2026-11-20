---
name: onnx-runtime-tokenizers
description: >-
  Use this skill when implementing or debugging offline neural machine translation
  in PulseIsland using the zero-NuGet native OrtApi C-API wrapper (OrtNativeRuntime.cs)
  and pure C# SentencePiece Unigram + MarianMT tokenizer (SentencePieceMarianTokenizer.cs).
---

# Zero-NuGet ONNX Runtime & SentencePiece MarianMT Skill

## Architecture (Zero External NuGet Packages)
PulseIsland intentionally compiles with `FullSdk=false` (`net8.0-windows`) without `Microsoft.ML.OnnxRuntime` or `Microsoft.ML.Tokenizers` NuGet packages:
1. **Native Runtime (`Translation/OrtNativeRuntime.cs`):**
   - Loads `models\_runtime\onnxruntime.dll` (ORT **1.20.1**, ~11.5 MB) explicitly via `NativeLibrary.Load` — **never** rely on `System32\onnxruntime.dll` (Windows ships an incompatible legacy `1.0.191121` build in `System32`).
   - Uses `OrtGetApiBase()->GetApi(20)` and verified ORT 1.20.1 `OrtApi` vtable indices `[1..100]` via `[UnmanagedFunctionPointer(CallingConvention.StdCall)]` delegates.
   - `EnsureRuntimeAvailableAsync` auto-downloads the MIT-licensed `Microsoft.ML.OnnxRuntime` NuGet `.nupkg` into `cache\downloads\` and extracts `onnxruntime.dll` + `onnxruntime_providers_shared.dll` to `models\_runtime\` if missing.
2. **Pure C# Tokenizer (`Translation/SentencePieceMarianTokenizer.cs`):**
   - Parses `source.spm` protobuf (`ModelProto.pieces`) for Unigram log-probabilities and runs dynamic-programming Viterbi segmentation with single-char fallback.
   - Loads `vocab.json` (shared MarianMT 62,518-token dictionary mapping both EN source & RU target subwords to IDs `0..62517`; note `source.spm` alone only holds ~32k source tokens and does NOT contain target Cyrillic IDs).
   - Reads `config.json` for `decoder_start_token_id` (`62517`), `eos_token_id` (`0`), and `pad_token_id` (`62517`).
   - `PreprocessInputText`: normalizes typographic quotes/apostrophes (`’` → `'`, `“”` → `"`), collapses repeated `***` / `?!?!?`, and normalizes ALL-CAPS text so cased Opus-MT models do not fragment into single characters.

## Model Directory Requirements (`models\<model-id>\`)
Every complete Xenova/Opus-MT model directory requires all 5 files at root (no nested `onnx/` folder):
- `encoder_model.onnx` (`input_ids:Int64[-1,-1]`, `attention_mask:Int64[-1,-1]` → `last_hidden_state:Float[-1,-1,512]`)
- `decoder_model.onnx` (`encoder_attention_mask`, `input_ids`, `encoder_hidden_states` → `logits:Float[-1,-1,62518]`)
- `source.spm` (SentencePiece Unigram model)
- `vocab.json` (token ↔ ID dictionary)
- `config.json` (authoritative BOS/EOS/PAD token IDs)
- Plus `pulseisland_manifest.json` (`BosTokenId: 62517, EosTokenId: 0, PadTokenId: 62517`).

## Inference & Diagnostics (`Translation/OnnxOfflineTranslationService.cs`)
- Splits long text by newlines/word boundaries (`<= 240` chars per chunk) and runs `OrtSessionHolder.RunGreedySeq2Seq`.
- If `vocab.json` / `config.json` / `source.spm` are missing, `EnsureModelSupportFilesAsync` attempts to fetch only the missing small files from `huggingface.co/Xenova/<modelId>/resolve/main/`. If sockets are blocked (e.g. `WSAEACCES 10013`), throw a clear `InvalidOperationException` telling the user which dictionary files are missing rather than silently falling back to word-by-word dictionary translation.
