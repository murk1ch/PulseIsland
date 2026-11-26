using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PulseIsland.Core;
using PulseIsland.Interop;

namespace PulseIsland.Services;

/// <summary>
/// Event-driven clipboard history monitor stored strictly inside <portable-root>\config\clipboard_history.json,
/// plus bounded in-RAM screenshot/image history (up to 8 images) with Vision AI handoff.
/// Supports keyword search, pinned entries, and safe self-copy suppression.
/// </summary>
public sealed class SmartClipboardService : IDisposable
{
    public const int WM_CLIPBOARDUPDATE = 0x031D;
    private const int MaxUnpinnedEntries = 50;
    public const int MaxImageEntries = 8;
    private const int MaxStoredImageSide = 1600;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _sync = new();
    private readonly List<ClipboardEntry> _entries = new();
    private readonly List<ClipboardImageEntry> _imageEntries = new();
    private IntPtr _hwnd;
    private bool _listening;
    private string? _suppressText;
    private ulong _suppressImageHash;
    private long _suppressUntilTick;

    public event Action? HistoryChanged;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    public SmartClipboardService()
    {
        LoadFromDisk();
    }

    public IReadOnlyList<ClipboardEntry> GetSnapshot()
    {
        lock (_sync)
        {
            return _entries
                .OrderByDescending(e => e.IsPinned)
                .ThenByDescending(e => e.Timestamp)
                .ToList();
        }
    }

    public IReadOnlyList<ClipboardEntry> Search(string? query, bool pinnedOnly = false)
    {
        var snapshot = GetSnapshot();
        IEnumerable<ClipboardEntry> seq = snapshot;

        if (pinnedOnly)
            seq = seq.Where(e => e.IsPinned);

        if (!string.IsNullOrWhiteSpace(query))
        {
            var q = query.Trim();
            seq = seq.Where(e => e.Text.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        return seq.ToList();
    }

    public void Attach(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || _listening)
            return;

        _hwnd = hwnd;
        _listening = AddClipboardFormatListener(_hwnd);
        if (!_listening)
        {
            LogService.Warn($"AddClipboardFormatListener failed (Win32 error {Marshal.GetLastWin32Error()}).");
        }
    }

    public IReadOnlyList<ClipboardImageEntry> GetImageSnapshot()
    {
        lock (_sync)
        {
            return _imageEntries
                .OrderByDescending(e => e.Timestamp)
                .ToList();
        }
    }

    public void HandleWndProc(int msg, bool enabled)
    {
        if (msg != WM_CLIPBOARDUPDATE || !enabled)
            return;

        try
        {
            if (Clipboard.ContainsText())
            {
                string text = Clipboard.GetText();
                if (string.IsNullOrWhiteSpace(text))
                    return;

                lock (_sync)
                {
                    if (_suppressText == text && Environment.TickCount64 < _suppressUntilTick)
                        return;
                }

                AddText(text);
                return;
            }

            if (Clipboard.ContainsImage())
            {
                var img = Clipboard.GetImage();
                if (img is not null)
                    AddBitmapSource(img);
            }
        }
        catch
        {
            // Clipboard may be temporarily locked by another application
        }
    }

    public void AddBitmapSource(BitmapSource bitmap)
    {
        if (bitmap is null || bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0)
            return;

        // CRITICAL: Normalize CF_DIB all-zero alpha (0 -> 255) BEFORE downscaling via TransformedBitmap,
        // otherwise >1600px PrintScreen screenshots premultiply RGB by 0 and turn 100% solid black!
        var region = ScreenCaptureService.FromBitmapSource(bitmap);
        if (region is null)
            return;

        int maxSide = Math.Max(region.Width, region.Height);
        if (maxSide > MaxStoredImageSide)
        {
            double scale = (double)MaxStoredImageSide / maxSide;
            var opaqueBmp = ScreenCaptureService.CreateBitmapSource(region);
            var scaled = new TransformedBitmap(opaqueBmp, new ScaleTransform(scale, scale));
            scaled.Freeze();
            region = ScreenCaptureService.FromBitmapSource(scaled);
            if (region is null)
                return;
        }

        ulong hash = ComputeSampledPixelHash(region.Bgra32Pixels, region.Width, region.Height);
        lock (_sync)
        {
            if (_suppressImageHash != 0 && _suppressImageHash == hash && Environment.TickCount64 < _suppressUntilTick)
                return;
        }

        AddImagePixels(region.Bgra32Pixels, region.Width, region.Height);
    }

    public void AddImagePixels(byte[] bgra32Pixels, int width, int height)
    {
        if (bgra32Pixels is null || width <= 0 || height <= 0 || bgra32Pixels.Length < width * height * 4)
            return;

        ulong hash = ComputeSampledPixelHash(bgra32Pixels, width, height);
        bool changed = false;

        lock (_sync)
        {
            int existingIdx = _imageEntries.FindIndex(e => e.PixelHash == hash && e.Width == width && e.Height == height);
            if (existingIdx >= 0)
            {
                var existing = _imageEntries[existingIdx];
                _imageEntries.RemoveAt(existingIdx);
                _imageEntries.Insert(0, existing with { Timestamp = DateTime.Now });
                changed = true;
            }
            else
            {
                _imageEntries.Insert(0, new ClipboardImageEntry(
                    Id: Guid.NewGuid().ToString("N"),
                    Width: width,
                    Height: height,
                    Timestamp: DateTime.Now,
                    Bgra32Pixels: bgra32Pixels,
                    PixelHash: hash));
                while (_imageEntries.Count > MaxImageEntries)
                    _imageEntries.RemoveAt(_imageEntries.Count - 1);
                changed = true;
            }
        }

        if (changed)
            HistoryChanged?.Invoke();
    }

    public bool TryGetImageRegion(string id, out CapturedScreenRegion? region)
    {
        region = null;
        ClipboardImageEntry? entry;
        lock (_sync)
        {
            entry = _imageEntries.FirstOrDefault(e => e.Id == id);
        }
        if (entry is null)
            return false;

        region = new CapturedScreenRegion(entry.Width, entry.Height, entry.Bgra32Pixels);
        return true;
    }

    public bool CopyImageToClipboard(string id)
    {
        if (!TryGetImageRegion(id, out var region) || region is null)
            return false;

        try
        {
            byte[] opaque = ScreenCaptureService.CompositeBgraOverWhiteIfNeeded(region.Bgra32Pixels);
            ulong hash = ComputeSampledPixelHash(opaque, region.Width, region.Height);
            lock (_sync)
            {
                _suppressImageHash = hash;
                _suppressUntilTick = Environment.TickCount64 + 1500;
            }
            var bmp = BitmapSource.Create(
                region.Width,
                region.Height,
                96.0,
                96.0,
                PixelFormats.Bgra32,
                null,
                opaque,
                region.Width * 4);
            bmp.Freeze();
            Clipboard.SetImage(bmp);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void RemoveImage(string id)
    {
        bool changed = false;
        lock (_sync)
        {
            if (_imageEntries.RemoveAll(e => e.Id == id) > 0)
                changed = true;
        }
        if (changed)
            HistoryChanged?.Invoke();
    }

    internal static ulong ComputeSampledPixelHash(byte[] bgra, int width, int height)
    {
        ulong hash = 14695981039346656037UL;
        hash = (hash ^ (uint)width) * 1099511628211UL;
        hash = (hash ^ (uint)height) * 1099511628211UL;

        if (bgra.Length == 0)
            return hash;

        int step = Math.Max(1, bgra.Length / 2048);
        for (int i = 0; i < bgra.Length; i += step)
        {
            hash = (hash ^ bgra[i]) * 1099511628211UL;
        }
        return hash;
    }

    public void AddText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        string normalized = text.Length > 8000 ? text[..8000] : text;
        bool changed = false;

        lock (_sync)
        {
            var existingIdx = _entries.FindIndex(e => string.Equals(e.Text, normalized, StringComparison.Ordinal));
            if (existingIdx >= 0)
            {
                var existing = _entries[existingIdx];
                _entries[existingIdx] = existing with { Timestamp = DateTime.Now };
                changed = true;
            }
            else
            {
                _entries.Insert(0, new ClipboardEntry(
                    Id: Guid.NewGuid().ToString("N"),
                    Text: normalized,
                    Timestamp: DateTime.Now,
                    IsPinned: false));
                TrimUnpinnedLocked();
                changed = true;
            }

            if (changed)
                SaveToDiskLocked();
        }

        if (changed)
            HistoryChanged?.Invoke();
    }

    public void TogglePin(string id)
    {
        bool changed = false;
        lock (_sync)
        {
            int idx = _entries.FindIndex(e => e.Id == id);
            if (idx >= 0)
            {
                _entries[idx] = _entries[idx] with { IsPinned = !_entries[idx].IsPinned };
                SaveToDiskLocked();
                changed = true;
            }
        }

        if (changed)
            HistoryChanged?.Invoke();
    }

    public void Remove(string id)
    {
        bool changed = false;
        lock (_sync)
        {
            int removed = _entries.RemoveAll(e => e.Id == id);
            if (removed > 0)
            {
                SaveToDiskLocked();
                changed = true;
            }
        }

        if (changed)
            HistoryChanged?.Invoke();
    }

    public void ClearUnpinned()
    {
        bool changed = false;
        lock (_sync)
        {
            int removedText = _entries.RemoveAll(e => !e.IsPinned);
            int removedImg = _imageEntries.RemoveAll(e => !e.IsPinned);
            if (removedText > 0)
            {
                SaveToDiskLocked();
                changed = true;
            }
            if (removedImg > 0)
                changed = true;
        }

        if (changed)
            HistoryChanged?.Invoke();
    }

    public bool CopyToClipboard(string text)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        try
        {
            lock (_sync)
            {
                _suppressText = text;
                _suppressUntilTick = Environment.TickCount64 + 1200;
            }
            Clipboard.SetDataObject(text, true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void TrimUnpinnedLocked()
    {
        var unpinned = _entries.Where(e => !e.IsPinned).OrderByDescending(e => e.Timestamp).ToList();
        if (unpinned.Count <= MaxUnpinnedEntries)
            return;

        var toRemove = unpinned.Skip(MaxUnpinnedEntries).Select(e => e.Id).ToHashSet();
        _entries.RemoveAll(e => toRemove.Contains(e.Id));
    }

    private void LoadFromDisk()
    {
        try
        {
            PortablePaths.EnsureDirectoriesExist();
            if (!File.Exists(PortablePaths.ClipboardHistoryFilePath))
                return;

            string json = File.ReadAllText(PortablePaths.ClipboardHistoryFilePath);
            var loaded = JsonSerializer.Deserialize<List<ClipboardEntry>>(json, JsonOptions);
            if (loaded is not null)
            {
                lock (_sync)
                {
                    _entries.Clear();
                    _entries.AddRange(loaded);
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Warn($"Failed to load clipboard history: {ex.Message}");
        }
    }

    private void SaveToDiskLocked()
    {
        try
        {
            PortablePaths.EnsureDirectoriesExist();
            string json = JsonSerializer.Serialize(_entries, JsonOptions);
            File.WriteAllText(PortablePaths.ClipboardHistoryFilePath, json);
        }
        catch (Exception ex)
        {
            LogService.Warn($"Failed to save clipboard history: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_listening && _hwnd != IntPtr.Zero)
        {
            RemoveClipboardFormatListener(_hwnd);
            _listening = false;
        }
    }
}
