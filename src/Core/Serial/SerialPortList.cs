using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Resesh.Core.Serial;

/// <summary>A serial port present now: "COM5" and what Windows calls the device
/// ("USB Serial Device", "Prolific PL2303GT USB Serial COM Port").</summary>
public sealed record SerialPortInfo(string PortName, string? Description)
{
    /// <summary>"COM5 — USB Serial Device", or just the port name.</summary>
    public string Label => string.IsNullOrEmpty(Description) ? PortName : $"{PortName} — {Description}";
}

/// <summary>
/// Lists the serial ports present now. Names come from the system's serial device map, the
/// same list <see cref="System.IO.Ports.SerialPort.GetPortNames"/> reads; descriptions come
/// from SetupAPI's device friendly names, because a bare "COM5" says nothing about which of
/// three USB adapters it is.
/// </summary>
public static class SerialPortList
{
    // GUID_DEVINTERFACE_COMPORT: every device that exposes a COM port, whatever its class
    // (ports, modems, Bluetooth links, multi-function USB devices).
    private static readonly Guid ComPortInterface = new("86E0D1E0-8089-11D0-9CE4-08003E301F73");

    public static IReadOnlyList<SerialPortInfo> List()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var name in System.IO.Ports.SerialPort.GetPortNames())
                if (Models.SerialTarget.NormalizePortName(name) is { } normalized)
                    names.Add(normalized);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or UnauthorizedAccessException or IOException)
        {
        }

        var descriptions = FriendlyNames();
        foreach (var name in descriptions.Keys)
            names.Add(name);
        return names
            .OrderBy(name => int.Parse(name.AsSpan(3), System.Globalization.CultureInfo.InvariantCulture))
            .Select(name => new SerialPortInfo(name, descriptions.GetValueOrDefault(name)))
            .ToList();
    }

    /// <summary>True when <paramref name="portName"/> is present now.</summary>
    public static bool Exists(string portName)
    {
        try
        {
            return System.IO.Ports.SerialPort.GetPortNames()
                .Any(name => string.Equals(Models.SerialTarget.NormalizePortName(name), portName, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>The device friendly name for each present port, without its "(COM5)" suffix.</summary>
    private static Dictionary<string, string> FriendlyNames()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!OperatingSystem.IsWindows())
            return result;
        var guid = ComPortInterface;
        var set = SetupDiGetClassDevsW(ref guid, IntPtr.Zero, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (set == InvalidHandle)
            return result;
        try
        {
            var buffer = new byte[1024];
            for (uint index = 0; ; index++)
            {
                var device = new SpDevInfoData { CbSize = (uint)Marshal.SizeOf<SpDevInfoData>() };
                if (!SetupDiEnumDeviceInfo(set, index, ref device))
                    break;

                var portName = DevicePortName(set, ref device);
                if (portName is null)
                    continue;
                if (!SetupDiGetDeviceRegistryPropertyW(set, ref device, SpdrpFriendlyName, out _, buffer,
                        (uint)buffer.Length, out var size) || size < 2)
                    continue;
                var friendly = System.Text.Encoding.Unicode.GetString(buffer, 0, (int)size).TrimEnd('\0').Trim();
                var suffix = $"({portName})";
                if (friendly.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    friendly = friendly[..^suffix.Length].TrimEnd();
                if (friendly.Length > 0)
                    result[portName] = friendly;
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
        return result;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? DevicePortName(IntPtr set, ref SpDevInfoData device)
    {
        var handle = SetupDiOpenDevRegKey(set, ref device, DicsFlagGlobal, 0, DiregDev, KeyRead);
        if (handle == InvalidHandle || handle == IntPtr.Zero)
            return null;
        using var key = RegistryKey.FromHandle(new SafeRegistryHandle(handle, ownsHandle: true));
        return Models.SerialTarget.NormalizePortName(key.GetValue("PortName") as string);
    }

    private const uint DigcfPresent = 0x2;
    private const uint DigcfDeviceInterface = 0x10;
    private const uint SpdrpFriendlyName = 0xC;
    private const uint DicsFlagGlobal = 1;
    private const uint DiregDev = 1;
    private const uint KeyRead = 0x20019;
    private static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevInfoData
    {
        public uint CbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr parent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SpDevInfoData data);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr set, ref SpDevInfoData data, uint property,
        out uint regType, byte[] buffer, uint bufferSize, out uint requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiOpenDevRegKey(IntPtr set, ref SpDevInfoData data, uint scope, uint profile,
        uint keyType, uint access);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
}
