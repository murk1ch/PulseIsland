---
name: huggingface-hub-download
description: >-
  Use this skill when implementing HTTP model downloading from Hugging Face Hub
  with resume support (Range requests), progress reporting, SHA256 verification,
  and strict storage inside <project-root>\models\ and <project-root>\cache\.
---

# Hugging Face Hub Download Skill

## Strict Storage Paths
- **Temporary chunks:** `<project-root>\cache\downloads\<model-id>\<filename>.part`
- **Final destination:** `<project-root>\models\<model-id>\<filename>`
- **Never** use `Path.GetTempPath()` or `C:` for staging downloads.

## Reliable Downloader Implementation
1. **Free Space Check:** Before starting a download, check `new DriveInfo("G").AvailableFreeSpace` against the `Content-Length` header. Abort cleanly with a clear message if space on `G:` is insufficient.
2. **Resume Support (`HttpRange`):**
   - Check if `.part` exists; if so, send `Range: bytes=<existingLength>-` header.
   - Handle `206 Partial Content` (append to `.part`) and `200 OK` (overwrite `.part`).
   - Follow redirects (`301/302/307/308` to Hugging Face CDN/LFS) while preserving cancellation tokens (`CancellationToken`).
3. **Progress & Throttling:**
   - Stream in `81920`-byte buffers and report `IProgress<DownloadProgress>` (bytes downloaded, total bytes, speed MB/s) throttled to ~10 updates/sec so the WPF UI stays responsive.
4. **Atomic Finalization:**
   - Once complete (and optionally SHA256-verified via `ETag`/`X-Linked-Etag`), `File.Move(partPath, finalPath, overwrite: true)`.
