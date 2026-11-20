using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using PulseIsland.Core;

namespace PulseIsland.Services;

/// <summary>
/// Event-driven clipboard history monitor stored strictly inside <portable-root>\config\clipboard_history.json.
/// Supports keyword search, pinned entries, and safe self-copy suppression.
/// </summary>
public sealed class SmartClipboardService : IDisposable
{
    public const int WM_CLIPBOARDUPDATE = 0x031D;
    private const int MaxUnpinnedEntries = 50;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _sync = new();
    private readonly List<ClipboardEntry> _entries = new();
    private IntPtr _hwnd;
    private bool _listening;
    private string? _suppressText;
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

    public void HandleWndProc(int msg, bool enabled)
    {
        if (msg != WM_CLIPBOARDUPDATE || !enabled)
            return;

        try
        {
            if (!Clipboard.ContainsText())
                return;

            string text = Clipboard.GetText();
            if (string.IsNullOrWhiteSpace(text))
                return;

            if (_suppressText == text && Environment.TickCount64 < _suppressUntilTick)
            {
                _suppressText = null;
                return;
            }

            AddText(text);
        }
        catch
        {
            // Clipboard may be temporarily locked by another application
        }
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
            int removed = _entries.RemoveAll(e => !e.IsPinned);
            if (removed > 0)
            {
                SaveToDiskLocked();
                changed = true;
            }
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
            _suppressText = text;
            _suppressUntilTick = Environment.TickCount64 + 1200;
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
