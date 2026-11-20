using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace PulseIsland.Services;

public readonly record struct SystemLoadSnapshot(
    int CpuPercent,
    int RamPercent,
    double UsedRamGb,
    double TotalRamGb,
    double DownloadKBps,
    double UploadKBps,
    bool HasBattery,
    int BatteryPercent,
    bool IsCharging,
    string UptimeText);

/// <summary>
/// Lightweight zero-NuGet PC telemetry monitor (CPU %, RAM %, Network ↓/↑, Battery, Uptime).
/// </summary>
public sealed class SystemMonitorService : IDisposable
{
    private readonly System.Threading.Timer _timer;
    private ulong _prevIdle;
    private ulong _prevKernel;
    private ulong _prevUser;
    private bool _hasCpuBaseline;

    private long _prevNetRxBytes;
    private long _prevNetTxBytes;
    private long _prevSampleTicks;
    private bool _hasNetBaseline;
    private bool _disposed;

    public event Action<SystemLoadSnapshot>? StatsUpdated;

    public SystemMonitorService()
    {
        SampleInternal(raiseEvent: false);
        _timer = new System.Threading.Timer(_ => SampleInternal(raiseEvent: true), null, 1500, 1500);
    }

    public SystemLoadSnapshot SampleNow() => SampleInternal(raiseEvent: false);

    private SystemLoadSnapshot SampleInternal(bool raiseEvent)
    {
        int cpuPct = 0;
        if (GetSystemTimes(out var idleFt, out var kernelFt, out var userFt))
        {
            ulong idle = FileTimeToUInt64(idleFt);
            ulong kernel = FileTimeToUInt64(kernelFt);
            ulong user = FileTimeToUInt64(userFt);

            if (_hasCpuBaseline)
            {
                ulong idleDelta = idle - _prevIdle;
                ulong totalDelta = (kernel - _prevKernel) + (user - _prevUser);
                if (totalDelta > 0)
                {
                    double busy = 1.0 - (double)idleDelta / totalDelta;
                    cpuPct = (int)Math.Round(Math.Clamp(busy * 100.0, 0.0, 100.0));
                }
            }

            _prevIdle = idle;
            _prevKernel = kernel;
            _prevUser = user;
            _hasCpuBaseline = true;
        }

        int ramPct = 0;
        double usedGb = 0;
        double totalGb = 0;
        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref mem) && mem.ullTotalPhys > 0)
        {
            const double bytesPerGb = 1024.0 * 1024.0 * 1024.0;
            totalGb = mem.ullTotalPhys / bytesPerGb;
            ulong usedBytes = mem.ullTotalPhys > mem.ullAvailPhys ? mem.ullTotalPhys - mem.ullAvailPhys : 0;
            usedGb = usedBytes / bytesPerGb;
            ramPct = (int)Math.Clamp(mem.dwMemoryLoad, 0U, 100U);
        }

        (double dlKBps, double ulKBps) = SampleNetworkSpeed();
        (bool hasBattery, int batteryPct, bool isCharging) = SampleBattery();

        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        string hUnit = LocalizationService.Pick("h", "ч");
        string mUnit = LocalizationService.Pick("m", "м");
        string uptimeText = uptime.TotalHours >= 1.0
            ? $"{(int)uptime.TotalHours}{hUnit} {uptime.Minutes:00}{mUnit}"
            : $"{uptime.Minutes}{mUnit}";

        var snapshot = new SystemLoadSnapshot(
            cpuPct,
            ramPct,
            usedGb,
            totalGb,
            dlKBps,
            ulKBps,
            hasBattery,
            batteryPct,
            isCharging,
            uptimeText);

        if (raiseEvent && !_disposed)
            StatsUpdated?.Invoke(snapshot);

        return snapshot;
    }

    private (double DownloadKBps, double UploadKBps) SampleNetworkSpeed()
    {
        try
        {
            long rx = 0;
            long tx = 0;
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up ||
                    ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;

                var stats = ni.GetIPStatistics();
                rx += stats.BytesReceived;
                tx += stats.BytesSent;
            }

            long nowTicks = Environment.TickCount64;
            double dl = 0;
            double ul = 0;
            if (_hasNetBaseline && nowTicks > _prevSampleTicks)
            {
                double sec = (nowTicks - _prevSampleTicks) / 1000.0;
                if (rx >= _prevNetRxBytes)
                    dl = (rx - _prevNetRxBytes) / 1024.0 / sec;
                if (tx >= _prevNetTxBytes)
                    ul = (tx - _prevNetTxBytes) / 1024.0 / sec;
            }

            _prevNetRxBytes = rx;
            _prevNetTxBytes = tx;
            _prevSampleTicks = nowTicks;
            _hasNetBaseline = true;
            return (Math.Max(0, dl), Math.Max(0, ul));
        }
        catch
        {
            return (0, 0);
        }
    }

    private static (bool HasBattery, int Percent, bool IsCharging) SampleBattery()
    {
        try
        {
            if (GetSystemPowerStatus(out var status))
            {
                bool noBattery = (status.BatteryFlag & 128) != 0 || status.BatteryLifePercent > 100;
                if (!noBattery)
                {
                    bool charging = status.ACLineStatus == 1 || (status.BatteryFlag & 8) != 0;
                    return (true, status.BatteryLifePercent, charging);
                }
            }
        }
        catch { }

        return (false, 0, false);
    }

    private static ulong FileTimeToUInt64(FILETIME ft) =>
        ((ulong)(uint)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(
        out FILETIME lpIdleTime,
        out FILETIME lpKernelTime,
        out FILETIME lpUserTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);
}
