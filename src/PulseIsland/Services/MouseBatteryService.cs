using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using PulseIsland.Core;

namespace PulseIsland.Services;

/// <summary>
/// Lightweight zero-NuGet wireless mouse battery monitor supporting:
/// 1) Bluetooth LE / Classic (Windows PnP ContainerId-grouped DEVPKEY_Device_BatteryPlusCharging),
/// 2) 2.4 GHz USB HID dongles (Beken/Rongyuan VID 0x1D57 PID 0xFA60 Interrupt-IN + Attack Shark X3 0x4010 scaling,
///    Logitech HID++ 2.0, Razer HyperSpeed, HID Power/Battery Pages 0x84/0x85),
/// 3) Wired USB charging detection (Beken VID 0x1D57 PID 0xFA61 / 0xFA55 via zero-access CreateFileW).
/// Uses raw Win32 OVERLAPPED + CancelIoEx for all HID Interrupt-IN/OUT I/O so silent drivers never deadlock.
/// </summary>
public sealed class MouseBatteryService : IDisposable
{
    // DEVPKEY_Device_BatteryPlusCharging = {104EA319-6EE2-4701-BD47-8DDBF425BBE5}, pid 2 & pid 3 (charging/state)
    private static readonly DEVPROPKEY DevPkeyBatteryPercent = new(new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5"), 2);
    private static readonly DEVPROPKEY DevPkeyBatteryState = new(new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5"), 3);
    private static readonly DEVPROPKEY DevPkeyDeviceDesc = new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 2);
    private static readonly DEVPROPKEY DevPkeyDeviceClass = new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 9);
    private static readonly DEVPROPKEY DevPkeyFriendlyName = new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);
    private static readonly DEVPROPKEY DevPkeyEnumeratorName = new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 24);
    private static readonly DEVPROPKEY DevPkeyContainerId = new(new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), 2);

    private const int DIGCF_PRESENT = 0x00000002;
    private const int DIGCF_ALLCLASSES = 0x00000004;
    private const int DIGCF_DEVICEINTERFACE = 0x00000010;
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_OVERLAPPED = 0x40000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
        public DEVPROPKEY(Guid fmtid, uint pid) { this.fmtid = fmtid; this.pid = pid; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public uint cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDD_ATTRIBUTES
    {
        public int Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(
        IntPtr ClassGuid, string? Enumerator, IntPtr hwndParent, uint Flags);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(
        ref Guid ClassGuid, string? Enumerator, IntPtr hwndParent, uint Flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr DeviceInfoSet, uint MemberIndex, ref SP_DEVINFO_DATA DeviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstanceIdW(
        IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData, StringBuilder DeviceInstanceId, int DeviceInstanceIdSize, out uint RequiredSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDevicePropertyW(
        IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData, ref DEVPROPKEY PropertyKey,
        out uint PropertyType, byte[]? PropertyBuffer, uint PropertyBufferSize, out uint RequiredSize, uint Flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr DeviceInfoSet, IntPtr DeviceInfoData, ref Guid InterfaceClassGuid,
        uint MemberIndex, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(
        IntPtr DeviceInfoSet, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData,
        IntPtr DeviceInterfaceDetailData, uint DeviceInterfaceDetailDataSize,
        out uint RequiredSize, IntPtr DeviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

    [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid HidGuid);
    [DllImport("hid.dll")] private static extern bool HidD_GetAttributes(SafeFileHandle HidDeviceObject, ref HIDD_ATTRIBUTES Attributes);
    [DllImport("hid.dll")] private static extern bool HidD_GetPreparsedData(SafeFileHandle HidDeviceObject, out IntPtr PreparsedData);
    [DllImport("hid.dll")] private static extern bool HidD_FreePreparsedData(IntPtr PreparsedData);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr PreparsedData, out HIDP_CAPS Capabilities);
    [DllImport("hid.dll")] private static extern bool HidD_GetFeature(SafeFileHandle HidDeviceObject, byte[] ReportBuffer, int ReportBufferLength);
    [DllImport("hid.dll")] private static extern bool HidD_SetFeature(SafeFileHandle HidDeviceObject, byte[] ReportBuffer, int ReportBufferLength);
    [DllImport("hid.dll")] private static extern bool HidD_GetProductString(SafeFileHandle HidDeviceObject, byte[] Buffer, int BufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(
        SafeFileHandle hFile, IntPtr lpBuffer, uint nNumberOfBytesToRead,
        out uint lpNumberOfBytesRead, ref NativeOverlapped lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(
        SafeFileHandle hFile, IntPtr lpBuffer, uint nNumberOfBytesToWrite,
        out uint lpNumberOfBytesWritten, ref NativeOverlapped lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetOverlappedResult(
        SafeFileHandle hFile, ref NativeOverlapped lpOverlapped,
        out uint lpNumberOfBytesTransferred, bool bWait);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIoEx(SafeFileHandle hFile, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateEventW(
        IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    internal readonly record struct PnpBatteryNodeInfo(
        Guid ContainerId,
        string InstanceId,
        string DeviceClass,
        string Enumerator,
        string Name,
        int? BatteryPercent,
        bool IsCharging);

    private readonly Timer _timer;
    private readonly Func<MouseConnectionMode> _getMode;
    private readonly Func<bool> _isEnabled;
    private readonly object _listenerLock = new();
    private CancellationTokenSource? _listenerCts;
    private Task? _listenerTask;
    private string _listenerDevicePath = string.Empty;
    private MouseBatterySnapshot _cachedBekenSnapshot;
    private bool _wasWiredCharging;
    private int _isSampling;
    private bool _disposed;

    public MouseBatterySnapshot LastSnapshot { get; private set; }
    public event Action<MouseBatterySnapshot>? BatteryUpdated;

    public MouseBatteryService(Func<bool> isEnabled, Func<MouseConnectionMode> getMode)
    {
        _isEnabled = isEnabled;
        _getMode = getMode;
        _timer = new Timer(_ => PollNow(), null, 1200, 35000);
    }

    public void PollNow()
    {
        if (_disposed)
            return;

        if (!_isEnabled())
        {
            StopBekenListener();
            _cachedBekenSnapshot = default;
            _wasWiredCharging = false;
            return;
        }

        if (Interlocked.Exchange(ref _isSampling, 1) == 1)
            return;

        var mode = _getMode();
        _ = Task.Run(() =>
        {
            try
            {
                if (mode == MouseConnectionMode.Bluetooth)
                {
                    StopBekenListener();
                }
                else
                {
                    EnsureBekenListener();
                }

                var snap = SampleWithLiveCache(mode);
                LastSnapshot = snap;
                BatteryUpdated?.Invoke(snap);
            }
            catch (Exception ex)
            {
                LogService.Error("MouseBatteryService sample error.", ex);
            }
            finally
            {
                Volatile.Write(ref _isSampling, 0);
            }
        });
    }

    private MouseBatterySnapshot SampleWithLiveCache(MouseConnectionMode mode)
    {
        if (mode is MouseConnectionMode.Auto or MouseConnectionMode.Bluetooth)
        {
            var bt = ReadBluetoothPnpBattery();
            if (bt.HasDevice)
                return bt;
        }

        if (mode is MouseConnectionMode.Auto or MouseConnectionMode.Wireless24Ghz)
        {
            var beken = FindBekenInterfaces();
            if (beken.HasWiredChargingMouse)
            {
                _wasWiredCharging = true;
                return new MouseBatterySnapshot(
                    true,
                    0,
                    true,
                    string.IsNullOrWhiteSpace(beken.WiredProduct) ? "USB Gaming Mouse" : beken.WiredProduct,
                    LocalizationService.Pick("USB cable", "USB кабель"));
            }

            if (_wasWiredCharging)
            {
                _wasWiredCharging = false;
                _cachedBekenSnapshot = default;
            }

            if (!string.IsNullOrEmpty(beken.WirelessDonglePath))
            {
                if (_cachedBekenSnapshot.HasDevice)
                    return _cachedBekenSnapshot;

                // Only do a one-shot probe if the dedicated background listener is not already reading this handle
                if (!IsListenerActiveFor(beken.WirelessDonglePath))
                {
                    var rfBeken = ReadHid24GhzBattery(probeBekenInterrupt: true);
                    if (rfBeken.HasDevice)
                    {
                        _cachedBekenSnapshot = rfBeken;
                        return rfBeken;
                    }
                }
            }
            else
            {
                // Dongle physically disconnected -> discard stale 2.4 GHz cache immediately
                _cachedBekenSnapshot = default;
            }

            // Other 2.4 GHz mice (HID Power/Battery Page 0x84/0x85, Logitech HID++, Razer)
            var otherRf = ReadHid24GhzBattery(probeBekenInterrupt: false);
            if (otherRf.HasDevice)
                return otherRf;
        }

        return new MouseBatterySnapshot(false, 0, false, string.Empty, mode.ToString());
    }

    public static MouseBatterySnapshot Sample(MouseConnectionMode mode)
    {
        if (mode is MouseConnectionMode.Auto or MouseConnectionMode.Bluetooth)
        {
            var bt = ReadBluetoothPnpBattery();
            if (bt.HasDevice)
                return bt;
        }

        if (mode is MouseConnectionMode.Auto or MouseConnectionMode.Wireless24Ghz)
        {
            var beken = FindBekenInterfaces();
            if (beken.HasWiredChargingMouse)
            {
                return new MouseBatterySnapshot(
                    true,
                    0,
                    true,
                    string.IsNullOrWhiteSpace(beken.WiredProduct) ? "USB Gaming Mouse" : beken.WiredProduct,
                    LocalizationService.Pick("USB cable", "USB кабель"));
            }

            var rf = ReadHid24GhzBattery(probeBekenInterrupt: true);
            if (rf.HasDevice)
                return rf;
        }

        return new MouseBatterySnapshot(false, 0, false, string.Empty, mode.ToString());
    }

    /// <summary>
    /// Reads Bluetooth LE / Classic mouse battery from Windows PnP device properties (~3 ms).
    /// Groups nodes by DEVPKEY_Device_ContainerId so sibling GATT Battery Service nodes (0x180F)
    /// resolve cleanly to the mouse container and non-mouse BT audio/keyboard devices are never matched.
    /// </summary>
    private static MouseBatterySnapshot ReadBluetoothPnpBattery()
    {
        IntPtr hSet = SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
        if (hSet == IntPtr.Zero || hSet == new IntPtr(-1))
            return default;

        try
        {
            var nodes = new List<PnpBatteryNodeInfo>(64);
            var devInfo = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            var idBuilder = new StringBuilder(512);

            for (uint i = 0; SetupDiEnumDeviceInfo(hSet, i, ref devInfo); i++)
            {
                idBuilder.Clear();
                string instanceId = SetupDiGetDeviceInstanceIdW(hSet, ref devInfo, idBuilder, idBuilder.Capacity, out _)
                    ? idBuilder.ToString()
                    : string.Empty;

                Guid containerId = ReadDevPropGuid(hSet, ref devInfo, DevPkeyContainerId);
                string devClass = ReadDevPropString(hSet, ref devInfo, DevPkeyDeviceClass);
                string enumerator = ReadDevPropString(hSet, ref devInfo, DevPkeyEnumeratorName);

                string name = ReadDevPropString(hSet, ref devInfo, DevPkeyFriendlyName);
                if (string.IsNullOrWhiteSpace(name))
                    name = ReadDevPropString(hSet, ref devInfo, DevPkeyDeviceDesc);

                int? pct = null;
                bool isCharging = false;
                if (TryReadDevPropByte(hSet, ref devInfo, DevPkeyBatteryPercent, out int rawPct) &&
                    rawPct >= 0 && rawPct <= 100)
                {
                    pct = rawPct;
                    isCharging = TryReadDevPropInt(hSet, ref devInfo, DevPkeyBatteryState, out int stateVal) &&
                                 (stateVal == 1 || (stateVal & 0x02) != 0);
                }

                // Keep nodes that carry battery info OR belong to a Bluetooth/Mouse container
                if (pct is not null ||
                    containerId != Guid.Empty && (
                        devClass.Equals("Mouse", StringComparison.OrdinalIgnoreCase) ||
                        IsBluetoothTransportNode(instanceId, enumerator) ||
                        IsLikelyMouseName(name)))
                {
                    nodes.Add(new PnpBatteryNodeInfo(containerId, instanceId, devClass, enumerator, name, pct, isCharging));
                }
            }

            return SelectBestBluetoothMouseBattery(nodes);
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(hSet);
        }
    }

    internal static MouseBatterySnapshot SelectBestBluetoothMouseBattery(IEnumerable<PnpBatteryNodeInfo> nodes)
    {
        MouseBatterySnapshot best = default;
        int bestScore = -1;

        var grouped = nodes
            .Where(n => n.ContainerId != Guid.Empty)
            .GroupBy(n => n.ContainerId);

        foreach (var group in grouped)
        {
            var list = group.ToList();
            if (list.Any(n => IsAcpiBatteryName(n.Name)))
                continue;

            var batteryNode = list.FirstOrDefault(n => n.BatteryPercent is >= 0 and <= 100);
            if (batteryNode.BatteryPercent is null)
                continue;

            bool isBluetooth = list.Any(n => IsBluetoothTransportNode(n.InstanceId, n.Enumerator));
            if (!isBluetooth)
                continue;

            bool hasMouseClass = list.Any(n => n.DeviceClass.Equals("Mouse", StringComparison.OrdinalIgnoreCase));
            bool hasMouseName = list.Any(n => IsLikelyMouseName(n.Name));
            if (!hasMouseClass && !hasMouseName)
                continue;

            if (!hasMouseClass && list.Any(n => IsNonMousePeripheralName(n.Name)))
                continue;

            string displayName = PickBestBluetoothDeviceName(list);
            int score = (hasMouseClass ? 10 : 0) + (hasMouseName ? 5 : 0);
            if (score > bestScore)
            {
                bestScore = score;
                best = new MouseBatterySnapshot(
                    true,
                    Math.Clamp(batteryNode.BatteryPercent.Value, 0, 100),
                    batteryNode.IsCharging,
                    displayName,
                    "Bluetooth");
            }
        }

        if (best.HasDevice)
            return best;

        // Fallback for older Bluetooth stacks where ContainerId may be missing
        foreach (var node in nodes)
        {
            if (node.ContainerId != Guid.Empty || node.BatteryPercent is null)
                continue;
            if (IsAcpiBatteryName(node.Name) || !IsBluetoothTransportNode(node.InstanceId, node.Enumerator))
                continue;

            bool isMouse = node.DeviceClass.Equals("Mouse", StringComparison.OrdinalIgnoreCase) ||
                           (IsLikelyMouseName(node.Name) && !IsNonMousePeripheralName(node.Name));
            if (!isMouse)
                continue;

            return new MouseBatterySnapshot(
                true,
                Math.Clamp(node.BatteryPercent.Value, 0, 100),
                node.IsCharging,
                string.IsNullOrWhiteSpace(node.Name) ? LocalizationService.Pick("Bluetooth mouse", "Bluetooth мышь") : node.Name,
                "Bluetooth");
        }

        return default;
    }

    private static bool IsBluetoothTransportNode(string instanceId, string enumerator)
    {
        return enumerator.StartsWith("BTH", StringComparison.OrdinalIgnoreCase) ||
               instanceId.StartsWith("BTH", StringComparison.OrdinalIgnoreCase) ||
               instanceId.Contains("{0000180F-", StringComparison.OrdinalIgnoreCase) ||
               instanceId.Contains("{00001812-", StringComparison.OrdinalIgnoreCase);
    }

    private static string PickBestBluetoothDeviceName(List<PnpBatteryNodeInfo> nodes)
    {
        string? namedMouse = nodes
            .Select(n => n.Name)
            .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n) && !IsGenericHidOrBatteryName(n) && IsLikelyMouseName(n));
        if (!string.IsNullOrWhiteSpace(namedMouse))
            return namedMouse;

        string? nonGeneric = nodes
            .Select(n => n.Name)
            .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n) && !IsGenericHidOrBatteryName(n));
        if (!string.IsNullOrWhiteSpace(nonGeneric))
            return nonGeneric;

        return LocalizationService.Pick("Bluetooth mouse", "Bluetooth мышь");
    }

    private static bool IsGenericHidOrBatteryName(string name)
    {
        string lower = name.ToLowerInvariant();
        return lower.Contains("служб") ||
               lower.Contains("battery service") ||
               lower.Contains("gatt") ||
               lower.Contains("hid-совместим") ||
               lower.Contains("hid-compliant") ||
               lower.Contains("устройство ввода");
    }

    private static bool IsAcpiBatteryName(string name)
        => name.Contains("ACPI", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("Батарея с ACPI", StringComparison.OrdinalIgnoreCase);

    private static bool IsNonMousePeripheralName(string name)
    {
        string lower = name.ToLowerInvariant();
        return lower.Contains("headset") || lower.Contains("headphone") || lower.Contains("earbud") ||
               lower.Contains("airpods") || lower.Contains("audio") || lower.Contains("speaker") ||
               lower.Contains("наушник") || lower.Contains("гарнитур") || lower.Contains("колонк") ||
               lower.Contains("controller") || lower.Contains("xbox") || lower.Contains("dualsense") ||
               lower.Contains("геймпад") || lower.Contains("keyboard") || lower.Contains("клавиатур");
    }

    /// <summary>
    /// Enumerates 2.4 GHz USB HID receivers (vendor collections >= 0xFF00 or Power/Battery Pages 0x84/0x85)
    /// and queries battery status safely with non-blocking vendor reports.
    /// </summary>
    private static MouseBatterySnapshot ReadHid24GhzBattery(bool probeBekenInterrupt = true)
    {
        HidD_GetHidGuid(out Guid hidGuid);
        IntPtr hSet = SetupDiGetClassDevsW(ref hidGuid, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (hSet == IntPtr.Zero || hSet == new IntPtr(-1))
            return default;

        try
        {
            var ifData = new SP_DEVICE_INTERFACE_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInterfaces(hSet, IntPtr.Zero, ref hidGuid, i, ref ifData); i++)
            {
                SetupDiGetDeviceInterfaceDetailW(hSet, ref ifData, IntPtr.Zero, 0, out uint reqSize, IntPtr.Zero);
                if (reqSize == 0 || reqSize > 4096)
                    continue;

                IntPtr detailBuf = Marshal.AllocHGlobal((int)reqSize);
                try
                {
                    Marshal.WriteInt32(detailBuf, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetailW(hSet, ref ifData, detailBuf, reqSize, out _, IntPtr.Zero))
                        continue;

                    string? devPath = Marshal.PtrToStringUni(IntPtr.Add(detailBuf, 4));
                    if (string.IsNullOrWhiteSpace(devPath))
                        continue;

                    using var handle = CreateFileW(
                        devPath,
                        GENERIC_READ,
                        FILE_SHARE_READ | FILE_SHARE_WRITE,
                        IntPtr.Zero,
                        OPEN_EXISTING,
                        FILE_FLAG_OVERLAPPED,
                        IntPtr.Zero);

                    if (handle.IsInvalid)
                        continue;

                    var attrs = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
                    if (!HidD_GetAttributes(handle, ref attrs))
                        continue;

                    if (!HidD_GetPreparsedData(handle, out IntPtr ppd))
                        continue;

                    HIDP_CAPS caps;
                    try
                    {
                        if (HidP_GetCaps(ppd, out caps) < 0)
                            continue;
                    }
                    finally
                    {
                        HidD_FreePreparsedData(ppd);
                    }

                    // Never probe boot mouse/keyboard top-level interfaces directly (UsagePage 0x01)
                    if (caps.UsagePage == 0x01)
                        continue;

                    string product = ReadHidProductName(handle);

                    // Never call synchronous HidD_GetFeature on Beken VID 0x1D57 (blocks IRP on col04 / FA61)
                    if (attrs.VendorID == 0x1D57)
                    {
                        if (probeBekenInterrupt && IsBekenTelemetryCollection(attrs, caps))
                        {
                            var snap = TryReadBekenOneShot(devPath, caps.InputReportByteLength, product);
                            if (snap.HasDevice)
                                return snap;
                        }
                        continue;
                    }

                    // 1) Standard HID Battery / Power Device Page (0x84 or 0x85)
                    if (caps.UsagePage is 0x84 or 0x85 && caps.FeatureReportByteLength >= 2)
                    {
                        var snap = TryQueryHidPowerPage(handle, caps.FeatureReportByteLength, product);
                        if (snap.HasDevice)
                            return snap;
                    }

                    // 2) Logitech HID++ 2.0 receiver (0x046D)
                    if (attrs.VendorID == 0x046D && caps.UsagePage >= 0xFF00)
                    {
                        using var rwHandle = CreateFileW(
                            devPath, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                            IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
                        if (!rwHandle.IsInvalid)
                        {
                            var snap = TryQueryLogitechHidPlusPlus(rwHandle, caps, product);
                            if (snap.HasDevice)
                                return snap;
                        }
                    }

                    // 3) Razer HyperSpeed 2.4 GHz wireless dongle (0x1532)
                    if (attrs.VendorID == 0x1532 && caps.FeatureReportByteLength >= 90)
                    {
                        using var rwHandle = CreateFileW(
                            devPath, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                            IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
                        if (!rwHandle.IsInvalid)
                        {
                            var snap = TryQueryRazerDongle(rwHandle, caps.FeatureReportByteLength, product);
                            if (snap.HasDevice)
                                return snap;
                        }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(detailBuf);
                }
            }

            return default;
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(hSet);
        }
    }

    private static bool IsBekenTelemetryCollection(HIDD_ATTRIBUTES attrs, HIDP_CAPS caps)
        => attrs.VendorID == 0x1D57 &&
           attrs.ProductID != 0xFA61 &&
           attrs.ProductID != 0xFA55 &&
           caps.InputReportByteLength >= 5 &&
           (caps.UsagePage == 0x000A || caps.UsagePage >= 0xFF00);

    private static bool IsBekenWiredChargingMouse(HIDD_ATTRIBUTES attrs, string product)
        => attrs.VendorID == 0x1D57 &&
           (attrs.ProductID == 0xFA61 ||
            attrs.ProductID == 0xFA55 ||
            product.Contains("USB Gaming Mouse", StringComparison.OrdinalIgnoreCase));

    private readonly record struct BekenInterfaceInfo(
        string? WirelessDonglePath,
        int WirelessInputLen,
        string WirelessProduct,
        bool HasWiredChargingMouse,
        string WiredProduct);

    private static BekenInterfaceInfo FindBekenInterfaces()
    {
        HidD_GetHidGuid(out Guid hidGuid);
        IntPtr hSet = SetupDiGetClassDevsW(ref hidGuid, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (hSet == IntPtr.Zero || hSet == new IntPtr(-1))
            return default;

        string? donglePath = null;
        int dongleInLen = 0;
        string dongleProduct = string.Empty;
        bool hasWired = false;
        string wiredProduct = string.Empty;

        try
        {
            var ifData = new SP_DEVICE_INTERFACE_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInterfaces(hSet, IntPtr.Zero, ref hidGuid, i, ref ifData); i++)
            {
                SetupDiGetDeviceInterfaceDetailW(hSet, ref ifData, IntPtr.Zero, 0, out uint reqSize, IntPtr.Zero);
                if (reqSize == 0 || reqSize > 4096)
                    continue;

                IntPtr detailBuf = Marshal.AllocHGlobal((int)reqSize);
                try
                {
                    Marshal.WriteInt32(detailBuf, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetailW(hSet, ref ifData, detailBuf, reqSize, out _, IntPtr.Zero))
                        continue;

                    string? devPath = Marshal.PtrToStringUni(IntPtr.Add(detailBuf, 4));
                    if (string.IsNullOrWhiteSpace(devPath))
                        continue;

                    // Zero-access open for fast attribute/caps check (never stalls on wired FA61)
                    using var handle = CreateFileW(
                        devPath, 0, FILE_SHARE_READ | FILE_SHARE_WRITE,
                        IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                    if (handle.IsInvalid)
                        continue;

                    var attrs = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
                    if (!HidD_GetAttributes(handle, ref attrs) || attrs.VendorID != 0x1D57)
                        continue;

                    string product = ReadHidProductName(handle);

                    if (IsBekenWiredChargingMouse(attrs, product))
                    {
                        hasWired = true;
                        if (string.IsNullOrWhiteSpace(wiredProduct) && !string.IsNullOrWhiteSpace(product))
                            wiredProduct = product;
                        continue;
                    }

                    if (donglePath is null && HidD_GetPreparsedData(handle, out IntPtr ppd))
                    {
                        HIDP_CAPS caps;
                        try
                        {
                            if (HidP_GetCaps(ppd, out caps) >= 0 && IsBekenTelemetryCollection(attrs, caps))
                            {
                                donglePath = devPath;
                                dongleInLen = caps.InputReportByteLength;
                                dongleProduct = product;
                            }
                        }
                        finally
                        {
                            HidD_FreePreparsedData(ppd);
                        }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(detailBuf);
                }
            }

            return new BekenInterfaceInfo(donglePath, dongleInLen, dongleProduct, hasWired, wiredProduct);
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(hSet);
        }
    }

    private bool IsListenerActiveFor(string devPath)
    {
        lock (_listenerLock)
        {
            return _listenerTask is { IsCompleted: false } &&
                   string.Equals(_listenerDevicePath, devPath, StringComparison.OrdinalIgnoreCase);
        }
    }

    private void EnsureBekenListener()
    {
        var beken = FindBekenInterfaces();
        lock (_listenerLock)
        {
            if (beken.HasWiredChargingMouse || string.IsNullOrEmpty(beken.WirelessDonglePath))
            {
                StopBekenListenerUnsafe();
                return;
            }

            if (_listenerTask is { IsCompleted: false } &&
                string.Equals(_listenerDevicePath, beken.WirelessDonglePath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            StopBekenListenerUnsafe();
            _listenerDevicePath = beken.WirelessDonglePath;
            _listenerCts = new CancellationTokenSource();
            var token = _listenerCts.Token;
            string devPath = beken.WirelessDonglePath;
            int inLen = beken.WirelessInputLen;
            string product = beken.WirelessProduct;
            _listenerTask = Task.Run(() => RunBekenListenerLoop(devPath, inLen, product, token), token);
        }
    }

    private void StopBekenListener()
    {
        lock (_listenerLock)
        {
            StopBekenListenerUnsafe();
        }
    }

    private void StopBekenListenerUnsafe()
    {
        _listenerDevicePath = string.Empty;
        if (_listenerCts is not null)
        {
            try { _listenerCts.Cancel(); } catch { }
            _listenerCts.Dispose();
            _listenerCts = null;
        }
        _listenerTask = null;
    }

    private void RunBekenListenerLoop(string devPath, int inputLen, string product, CancellationToken ct)
    {
        int reportLen = Math.Max(inputLen, 5);
        byte[] managedBuf = new byte[reportLen];

        try
        {
            using var handle = CreateFileW(
                devPath,
                GENERIC_READ,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero,
                OPEN_EXISTING,
                FILE_FLAG_OVERLAPPED,
                IntPtr.Zero);

            if (handle.IsInvalid)
                return;

            while (!ct.IsCancellationRequested && !_disposed)
            {
                int read = ReadHidOverlappedWithCancel(handle, managedBuf, reportLen, timeoutMs: 2500, ct);
                if (read < 0)
                {
                    // Device unplugged or handle invalidated
                    if (!_wasWiredCharging)
                    {
                        _cachedBekenSnapshot = default;
                        var empty = new MouseBatterySnapshot(false, 0, false, string.Empty, LocalizationService.Pick("2.4 GHz", "2.4 ГГц"));
                        LastSnapshot = empty;
                        BatteryUpdated?.Invoke(empty);
                    }
                    return;
                }

                if (read == 0)
                    continue;

                if (TryParseBekenBatteryPacket(managedBuf, read, out int pct, out bool isCharging))
                {
                    string activeDisplayName = string.IsNullOrWhiteSpace(product)
                        ? LocalizationService.Pick("2.4 GHz mouse", "2.4 ГГц мышь")
                        : product;
                    var snap = new MouseBatterySnapshot(true, pct, isCharging, activeDisplayName, LocalizationService.Pick("2.4 GHz", "2.4 ГГц"));
                    bool changed = !_cachedBekenSnapshot.HasDevice ||
                                   _cachedBekenSnapshot.Percent != snap.Percent ||
                                   _cachedBekenSnapshot.IsCharging != snap.IsCharging ||
                                   !string.Equals(LastSnapshot.ConnectionLabel, snap.ConnectionLabel, StringComparison.Ordinal);
                    _cachedBekenSnapshot = snap;
                    LastSnapshot = snap;
                    if (changed)
                    {
                        BatteryUpdated?.Invoke(snap);
                    }
                }
            }
        }
        catch
        {
            // Device unplugged or sleep reset; timer / WM_DEVICECHANGE re-attaches when available
        }
        finally
        {
            lock (_listenerLock)
            {
                if (string.Equals(_listenerDevicePath, devPath, StringComparison.OrdinalIgnoreCase))
                {
                    _listenerDevicePath = string.Empty;
                }
            }
        }
    }

    private static MouseBatterySnapshot TryReadBekenOneShot(string devPath, int inputLen, string product)
    {
        try
        {
            using var handle = CreateFileW(
                devPath,
                GENERIC_READ,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero,
                OPEN_EXISTING,
                FILE_FLAG_OVERLAPPED,
                IntPtr.Zero);

            if (handle.IsInvalid)
                return default;

            int reportLen = Math.Max(inputLen, 5);
            byte[] buf = new byte[reportLen];
            // Beken 2.4 GHz receivers push 5-byte telemetry every ~2000 ms while mouse is awake
            int read = ReadHidOverlappedWithCancel(handle, buf, reportLen, timeoutMs: 2300, CancellationToken.None);
            if (read > 0 && TryParseBekenBatteryPacket(buf, read, out int pct, out bool isCharging))
            {
                return new MouseBatterySnapshot(
                    true,
                    pct,
                    isCharging,
                    string.IsNullOrWhiteSpace(product) ? LocalizationService.Pick("2.4 GHz mouse", "2.4 ГГц мышь") : product,
                    LocalizationService.Pick("2.4 GHz", "2.4 ГГц"));
            }
        }
        catch { }
        return default;
    }

    /// <summary>
    /// Reads a single HID Interrupt-IN report using raw Win32 OVERLAPPED + CancelIoEx so it NEVER deadlocks on timeout.
    /// Returns bytes read (>0), 0 on timeout, or -1 on fatal error.
    /// </summary>
    private static int ReadHidOverlappedWithCancel(
        SafeFileHandle handle,
        byte[] dest,
        int bytesToRead,
        uint timeoutMs,
        CancellationToken ct)
    {
        IntPtr hEvent = CreateEventW(IntPtr.Zero, bManualReset: true, bInitialState: false, lpName: null);
        if (hEvent == IntPtr.Zero)
            return -1;

        IntPtr nativeBuf = Marshal.AllocHGlobal(bytesToRead);
        try
        {
            var ov = new NativeOverlapped { EventHandle = hEvent };
            bool ok = ReadFile(handle, nativeBuf, (uint)bytesToRead, out uint bytesRead, ref ov);
            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();
                const int ERROR_IO_PENDING = 997;
                if (err != ERROR_IO_PENDING)
                    return -1;

                uint waited = 0;
                const uint stepMs = 200;
                while (waited < timeoutMs && !ct.IsCancellationRequested)
                {
                    uint slice = Math.Min(stepMs, timeoutMs - waited);
                    uint waitRes = WaitForSingleObject(hEvent, slice);
                    if (waitRes == 0) // WAIT_OBJECT_0
                    {
                        if (GetOverlappedResult(handle, ref ov, out bytesRead, bWait: false) && bytesRead > 0)
                        {
                            int copyLen = Math.Min((int)bytesRead, dest.Length);
                            Marshal.Copy(nativeBuf, dest, 0, copyLen);
                            return copyLen;
                        }
                        return 0;
                    }
                    waited += slice;
                }

                // Timed out or cancelled -> cancel kernel IRP before freeing buffer or closing handle
                CancelIoEx(handle, IntPtr.Zero);
                WaitForSingleObject(hEvent, 50);
                return 0;
            }

            if (bytesRead > 0)
            {
                int copyLen = Math.Min((int)bytesRead, dest.Length);
                Marshal.Copy(nativeBuf, dest, 0, copyLen);
                return copyLen;
            }
            return 0;
        }
        finally
        {
            Marshal.FreeHGlobal(nativeBuf);
            CloseHandle(hEvent);
        }
    }

    private static bool WriteHidOverlappedWithCancel(
        SafeFileHandle handle,
        byte[] src,
        uint timeoutMs)
    {
        IntPtr hEvent = CreateEventW(IntPtr.Zero, bManualReset: true, bInitialState: false, lpName: null);
        if (hEvent == IntPtr.Zero)
            return false;

        IntPtr nativeBuf = Marshal.AllocHGlobal(src.Length);
        try
        {
            Marshal.Copy(src, 0, nativeBuf, src.Length);
            var ov = new NativeOverlapped { EventHandle = hEvent };
            bool ok = WriteFile(handle, nativeBuf, (uint)src.Length, out _, ref ov);
            if (ok)
                return true;

            int err = Marshal.GetLastWin32Error();
            const int ERROR_IO_PENDING = 997;
            if (err != ERROR_IO_PENDING)
                return false;

            if (WaitForSingleObject(hEvent, timeoutMs) == 0 &&
                GetOverlappedResult(handle, ref ov, out _, bWait: false))
            {
                return true;
            }

            CancelIoEx(handle, IntPtr.Zero);
            WaitForSingleObject(hEvent, 50);
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(nativeBuf);
            CloseHandle(hEvent);
        }
    }

    internal static bool TryParseBekenBatteryPacket(byte[] data, int length, out int percent, out bool isCharging)
    {
        percent = 0;
        isCharging = false;
        if (length < 5 || data.Length < 5)
            return false;

        if (data[0] != 0x03)
            return false;

        if (data[2] is not (0x40 or 0x41))
            return false;

        int rawPct = data[4];
        if (rawPct < 0 || rawPct > 100)
            return false;

        // Attack Shark X3 (event 0x4010 -> data[1] == 0x10) encodes battery as a 1..10 step level (10%..100%).
        // Other Beken models (e.g. X6/X11/R1 with data[1] == 0x55/0x8E/0x02) encode 0..100 directly.
        percent = (data[1] == 0x10 && rawPct <= 10) ? rawPct * 10 : rawPct;
        isCharging = data[3] == 0x03;
        return true;
    }

    private static MouseBatterySnapshot TryQueryHidPowerPage(SafeFileHandle handle, int len, string product)
    {
        byte[] buf = new byte[Math.Max(4, len)];
        for (byte rid = 1; rid <= 10; rid++)
        {
            Array.Clear(buf, 0, buf.Length);
            buf[0] = rid;
            if (HidD_GetFeature(handle, buf, buf.Length) && buf[1] > 0 && buf[1] <= 100)
            {
                return new MouseBatterySnapshot(
                    true, buf[1], false,
                    string.IsNullOrWhiteSpace(product) ? LocalizationService.Pick("2.4 GHz mouse", "2.4 ГГц мышь") : product,
                    LocalizationService.Pick("2.4 GHz HID", "2.4 ГГц HID"));
            }
        }
        return default;
    }

    private static MouseBatterySnapshot TryQueryLogitechHidPlusPlus(SafeFileHandle handle, HIDP_CAPS caps, string product)
    {
        if (caps.OutputReportByteLength < 7 || caps.InputReportByteLength < 7)
            return default;

        try
        {
            byte[] outBuf = new byte[caps.OutputReportByteLength];
            byte[] inBuf = new byte[caps.InputReportByteLength];

            // HID++ 2.0 IRoot Ping (device index 1)
            outBuf[0] = caps.OutputReportByteLength >= 20 ? (byte)0x11 : (byte)0x10;
            outBuf[1] = 0x01;
            outBuf[2] = 0x00;
            outBuf[3] = 0x1A;
            outBuf[6] = 0xAA;

            if (!WriteHidOverlappedWithCancel(handle, outBuf, 120))
                return default;
            if (ReadHidOverlappedWithCancel(handle, inBuf, inBuf.Length, 140, CancellationToken.None) < 7)
                return default;

            // Query UnifiedBattery (0x1004) or BatteryLevelStatus (0x1000) via IRoot GetFeature
            foreach (ushort featId in new ushort[] { 0x1004, 0x1000 })
            {
                Array.Clear(outBuf, 0, outBuf.Length);
                outBuf[0] = caps.OutputReportByteLength >= 20 ? (byte)0x11 : (byte)0x10;
                outBuf[1] = 0x01;
                outBuf[2] = 0x00;
                outBuf[3] = 0x0B; // GetFeature
                outBuf[4] = (byte)(featId >> 8);
                outBuf[5] = (byte)(featId & 0xFF);

                if (!WriteHidOverlappedWithCancel(handle, outBuf, 120))
                    continue;
                if (ReadHidOverlappedWithCancel(handle, inBuf, inBuf.Length, 140, CancellationToken.None) < 7)
                    continue;

                byte featIdx = inBuf[4];
                if (featIdx == 0) continue;

                Array.Clear(outBuf, 0, outBuf.Length);
                outBuf[0] = caps.OutputReportByteLength >= 20 ? (byte)0x11 : (byte)0x10;
                outBuf[1] = 0x01;
                outBuf[2] = featIdx;
                outBuf[3] = featId == 0x1004 ? (byte)0x1B : (byte)0x0B;

                if (!WriteHidOverlappedWithCancel(handle, outBuf, 120))
                    continue;
                if (ReadHidOverlappedWithCancel(handle, inBuf, inBuf.Length, 140, CancellationToken.None) < 7)
                    continue;

                int pct = inBuf[4];
                if (pct is >= 1 and <= 100)
                {
                    bool charging = inBuf[6] is 1 or 2;
                    return new MouseBatterySnapshot(
                        true, pct, charging,
                        string.IsNullOrWhiteSpace(product) ? LocalizationService.Pick("Logitech 2.4 GHz", "Logitech 2.4 ГГц") : product,
                        LocalizationService.Pick("2.4 GHz (HID++)", "2.4 ГГц (HID++)"));
                }
            }
        }
        catch { }
        return default;
    }

    private static MouseBatterySnapshot TryQueryRazerDongle(SafeFileHandle handle, int len, string product)
    {
        try
        {
            byte[] buf = new byte[Math.Max(91, len)];
            // Razer report format: reportId(0) + status(0) + transId(0x1F) + remaining(0,0) + proto(0) + dataSize(2) + cmdClass(7) + cmdId(0x80)
            buf[0] = 0x00;
            buf[2] = 0x1F;
            buf[6] = 0x02;
            buf[7] = 0x07;
            buf[8] = 0x80;
            buf[89] = ComputeRazerCrc(buf);

            if (!HidD_SetFeature(handle, buf, buf.Length))
                return default;

            Thread.Sleep(25);
            Array.Clear(buf, 0, buf.Length);
            buf[0] = 0x00;
            if (!HidD_GetFeature(handle, buf, buf.Length) || buf[1] != 0x02)
                return default;

            int rawLevel = buf[10]; // 0..255
            int pct = Math.Clamp((int)Math.Round(rawLevel * 100.0 / 255.0), 0, 100);

            // Check charging status (cmdId = 0x84)
            Array.Clear(buf, 0, buf.Length);
            buf[0] = 0x00;
            buf[2] = 0x1F;
            buf[6] = 0x02;
            buf[7] = 0x07;
            buf[8] = 0x84;
            buf[89] = ComputeRazerCrc(buf);
            bool charging = false;
            if (HidD_SetFeature(handle, buf, buf.Length))
            {
                Thread.Sleep(20);
                Array.Clear(buf, 0, buf.Length);
                if (HidD_GetFeature(handle, buf, buf.Length) && buf[1] == 0x02)
                    charging = buf[10] != 0;
            }

            return new MouseBatterySnapshot(
                true, pct, charging,
                string.IsNullOrWhiteSpace(product) ? LocalizationService.Pick("Razer 2.4 GHz", "Razer 2.4 ГГц") : product,
                LocalizationService.Pick("2.4 GHz (Razer)", "2.4 ГГц (Razer)"));
        }
        catch { }
        return default;
    }

    private static byte ComputeRazerCrc(byte[] report)
    {
        byte crc = 0;
        for (int i = 3; i <= 88; i++)
            crc ^= report[i];
        return crc;
    }

    private static string ReadHidProductName(SafeFileHandle handle)
    {
        byte[] buf = new byte[256];
        if (!HidD_GetProductString(handle, buf, buf.Length))
            return string.Empty;
        return Encoding.Unicode.GetString(buf).TrimEnd('\0').Trim();
    }

    private static bool IsLikelyMouseName(string name)
    {
        string lower = name.ToLowerInvariant();
        return lower.Contains("mouse") || lower.Contains("мышь") ||
               lower.Contains("superlight") || lower.Contains("viper") ||
               lower.Contains("deathadder") || lower.Contains("basilisk") ||
               lower.Contains("aerox") || lower.Contains("lamzu") ||
               lower.Contains("vxe") || lower.Contains("attack") ||
               lower.Contains("kysona");
    }

    private static bool TryReadDevPropByte(IntPtr hSet, ref SP_DEVINFO_DATA devInfo, DEVPROPKEY key, out int value)
    {
        value = 0;
        byte[] buf = new byte[4];
        if (!SetupDiGetDevicePropertyW(hSet, ref devInfo, ref key, out _, buf, (uint)buf.Length, out uint req, 0))
            return false;
        if (req == 1) value = buf[0];
        else if (req >= 4) value = BitConverter.ToInt32(buf, 0);
        else return false;
        return true;
    }

    private static bool TryReadDevPropInt(IntPtr hSet, ref SP_DEVINFO_DATA devInfo, DEVPROPKEY key, out int value)
    {
        value = 0;
        byte[] buf = new byte[4];
        if (!SetupDiGetDevicePropertyW(hSet, ref devInfo, ref key, out _, buf, (uint)buf.Length, out uint req, 0) || req == 0)
            return false;
        value = req >= 4 ? BitConverter.ToInt32(buf, 0) : buf[0];
        return true;
    }

    private static Guid ReadDevPropGuid(IntPtr hSet, ref SP_DEVINFO_DATA devInfo, DEVPROPKEY key)
    {
        byte[] buf = new byte[16];
        if (!SetupDiGetDevicePropertyW(hSet, ref devInfo, ref key, out _, buf, (uint)buf.Length, out uint req, 0) || req != 16)
            return Guid.Empty;
        return new Guid(buf);
    }

    private static string ReadDevPropString(IntPtr hSet, ref SP_DEVINFO_DATA devInfo, DEVPROPKEY key)
    {
        byte[] buf = new byte[512];
        if (!SetupDiGetDevicePropertyW(hSet, ref devInfo, ref key, out _, buf, (uint)buf.Length, out uint req, 0) || req == 0)
            return string.Empty;
        int byteCount = (int)Math.Min(req, (uint)buf.Length);
        return Encoding.Unicode.GetString(buf, 0, byteCount).TrimEnd('\0').Trim();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Dispose();
        StopBekenListener();
    }
}
