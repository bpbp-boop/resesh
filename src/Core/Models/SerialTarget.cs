namespace Resesh.Core.Models;

public enum SerialParity
{
    None,
    Odd,
    Even,
    Mark,
    Space,
}

public enum SerialStopBits
{
    One,
    OnePointFive,
    Two,
}

/// <summary>Flow control the port can do. DSR/DTR is left out: System.IO.Ports has no
/// handshake for it, and console ports almost never use it.</summary>
public enum SerialFlowControl
{
    None,
    XonXoff,
    RtsCts,
}

/// <summary>
/// A serial console line: which port and how the line is framed. Defaults are 9600 8N1 with
/// no flow control, what Cisco, Juniper, Arista and most other console ports expect.
/// </summary>
public sealed record SerialTarget
{
    /// <summary>Minimum and maximum accepted baud rates for validation.</summary>
    public const int MinBaudRate = 50;
    public const int MaxBaudRate = 4_000_000;

    /// <summary>Common console speeds, slowest first, for the editor's picker.</summary>
    public static readonly IReadOnlyList<int> CommonBaudRates =
        [1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200, 230400, 460800, 921600];

    /// <summary>Windows port name, e.g. "COM5".</summary>
    public string PortName { get; set; } = "";

    public int BaudRate { get; set; } = 9600;

    /// <summary>5 to 8.</summary>
    public int DataBits { get; set; } = 8;

    public SerialParity Parity { get; set; } = SerialParity.None;

    public SerialStopBits StopBits { get; set; } = SerialStopBits.One;

    public SerialFlowControl FlowControl { get; set; } = SerialFlowControl.None;

    /// <summary>Line settings in the usual short form: "9600 8N1", "115200 7E2".</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Framing
    {
        get
        {
            var parity = Parity switch
            {
                SerialParity.Odd => 'O',
                SerialParity.Even => 'E',
                SerialParity.Mark => 'M',
                SerialParity.Space => 'S',
                _ => 'N',
            };
            var stop = StopBits switch
            {
                SerialStopBits.OnePointFive => "1.5",
                SerialStopBits.Two => "2",
                _ => "1",
            };
            return $"{BaudRate} {DataBits}{parity}{stop}";
        }
    }

    /// <summary>"COM5 9600 8N1", with the flow control when there is any.</summary>
    public string Describe() => $"{PortName} {Framing}" + FlowControl switch
    {
        SerialFlowControl.XonXoff => " XON/XOFF",
        SerialFlowControl.RtsCts => " RTS/CTS",
        _ => "",
    };

    /// <summary>An error explaining why Windows rejects <paramref name="dataBits"/> with
    /// <paramref name="stopBits"/>, or null when the combination is valid.</summary>
    public static string? ValidateFraming(int dataBits, SerialStopBits stopBits) => (dataBits, stopBits) switch
    {
        (< 5 or > 8, _) => "Data bits must be between 5 and 8.",
        (not 5, SerialStopBits.OnePointFive) => "1.5 stop bits is only valid with 5 data bits.",
        (5, SerialStopBits.Two) => "2 stop bits is not valid with 5 data bits.",
        _ => null,
    };

    /// <summary>An error describing an invalid speed or framing, or null when valid.</summary>
    public string? ValidateLineSettings()
    {
        if (BaudRate is < MinBaudRate or > MaxBaudRate)
            return $"Speed must be between {MinBaudRate} and {MaxBaudRate} baud.";
        return ValidateFraming(DataBits, StopBits);
    }

    /// <summary>"COM5" from "com5", " COM5 " or "\\.\COM5"; null when it does not look like a
    /// port name. Windows names ports COM1 to COM256.</summary>
    public static string? NormalizePortName(string? text)
    {
        if (text is null)
            return null;
        // Buggy USB-serial drivers occasionally leave trailing NULs in SERIALCOMM values.
        var nul = text.IndexOf('\0');
        var name = (nul >= 0 ? text[..nul] : text).Trim();
        if (name.StartsWith(@"\\.\", StringComparison.Ordinal))
            name = name[4..];
        if (name.Length < 4 || !name.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(name.AsSpan(3), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var number)
            || number is < 1 or > 256)
            return null;
        return $"COM{number}";
    }
}
