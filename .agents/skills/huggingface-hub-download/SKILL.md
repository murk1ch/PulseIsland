---
name: huggingface-hub-download
description: >-
  Use this skill when implementing resumable HTTP Range downloads from Hugging Face
  Hub and GitHub Releases for ONNX translation, Local AI/Vision GGUF + mmproj,
  llama-server, PP-OCRv5, and whisper.cpp models strictly inside <portable-root>.
---

# Hugging Face Hub & Portable Model Download Skill

## Strict Portable Storage Layout (`PortablePaths`)
Never auto-download models without explicit user action, and **never** stage files in `Path.GetTempPath()` or system user directories.
- **Staging `.part` chunks:** `<portable-root>\cache\downloads\<model-id>\<filename>.part` (`PortablePaths.DownloadsCacheDirectory`)
- **Final destinations under `<portable-root>\models\` (`PortablePaths.ModelsDirectory`):**
  - Offline ONNX translation models (`OfflineModelCatalogService`): `models\<model-id>\`
  - Local AI & Vision GGUF + `--mmproj` (`LocalLlmCatalogService`): `models\<model-id>\`
  - `llama-server` Vulkan/CPU runtime (`LocalLlmCatalogService`): `models\_llama\`
  - PP-OCRv5 Mobile ONNX detector/recognizers + YAML dicts (`PaddleOcrOnnxService`): `models\_ocr\`
  - `whisper.cpp` CLI + `ggml-base.bin` (`WhisperVoiceService`): `models\_whisper\`
  - ONNX Runtime native DLLs (`OrtNativeRuntime`): `models\_runtime\`

## Reliable Downloader Rules (`Translation/HuggingFaceDownloader.cs`)
1. **Portable-Drive Free Space Check:**
   - Resolve the drive dynamically via `Path.GetPathRoot(PortablePaths.ModelsDirectory)` and inspect `new DriveInfo(root).AvailableFreeSpace` against remaining bytes **plus a 100 MB safety buffer** (never hardcode a drive letter).
   - Throw a clear localized `IOException` if free space is insufficient.
2. **HTTP Range Resume Support:**
   - If `<filename>.part` exists, send `Range: bytes=<existingBytes>-`.
   - Handle `206 Partial Content` (open with `FileMode.Append` and compute full total from `ContentRange.Length`), `200 OK` (restart cleanly with `FileMode.Create`), and `416 RequestedRangeNotSatisfiable` (for already-complete archive `.part` files before extraction).
   - Keep `AllowAutoRedirect = true` to follow Hugging Face CDN/LFS and GitHub Release redirects while honoring `CancellationToken`.
3. **Streaming Progress & Throttling:**
   - Stream in `81920`-byte async buffers and throttle `IProgress<DownloadProgressInfo>` reports to ~10 updates/sec (`Environment.TickCount64` delta >= `100 ms`) so WPF UI bindings remain responsive.
4. **Atomic Finalization:**
   - Flush and close the `.part` stream, then move atomically with `File.Move(partPath, finalPath, overwrite: true)`.
   - Keep partial `.part` files on cancellation/network interruption so the user can resume seamlessly.
