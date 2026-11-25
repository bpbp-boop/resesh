using Resesh.Core.Models;
using Resesh.Core.Serial;

namespace Resesh.Core.Tests;

public sealed class SerialTargetTests
{
    [Theory]
    [InlineData("COM5", "COM5")]
    [InlineData(" com12 ", "COM12")]
    [InlineData(@"\\.\COM3", "COM3")]
    [InlineData("COM256", "COM256")]
    [InlineData("COM5\0", "COM5")]
    [InlineData("COM5\0garbage", "COM5")]
    [InlineData("\0COM5", null)]
    [InlineData("COM0", null)]
    [InlineData("COM257", null)]
    [InlineData("COM", null)]
    [InlineData("LPT1", null)]
    [InlineData("COM+1", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void NormalizePortName(string? input, string? expected) =>
        Assert.Equal(expected, SerialTarget.NormalizePortName(input));

    [Fact]
    public void Describe_UsesTheShortForm()
    {
        Assert.Equal("COM5 9600 8N1", new SerialTarget { PortName = "COM5" }.Describe());
        Assert.Equal("COM1 115200 7E2 RTS/CTS", new SerialTarget
        {
            PortName = "COM1",
            BaudRate = 115200,
            DataBits = 7,
            Parity = SerialParity.Even,
            StopBits = SerialStopBits.Two,
            FlowControl = SerialFlowControl.RtsCts,
        }.Describe());
    }

    [Fact]
    public void Capabilities_OfferBreakButNoFilesOrHostKeys()
    {
        var capabilities = SessionCapabilities.For(new Session { Kind = SessionKind.Serial });
        Assert.True(capabilities.SendBreak);
        Assert.False(capabilities.FilePane);
        Assert.False(capabilities.HostKeys);
        Assert.False(capabilities.RemoteSession);
    }

    [Fact]
    public void Open_BadPortName_FailsWithoutTouchingHardware()
    {
        using var serial = new SerialTerminalSession();
        var error = Assert.Throws<SerialSessionException>(() => serial.Open(new SerialTarget { PortName = "printer" }));
        Assert.Contains("COM3", error.Message);
    }

    [Theory]
    [InlineData(5, SerialStopBits.One, true)]
    [InlineData(5, SerialStopBits.OnePointFive, true)]
    [InlineData(5, SerialStopBits.Two, false)]
    [InlineData(6, SerialStopBits.One, true)]
    [InlineData(6, SerialStopBits.OnePointFive, false)]
    [InlineData(7, SerialStopBits.Two, true)]
    [InlineData(8, SerialStopBits.One, true)]
    [InlineData(8, SerialStopBits.OnePointFive, false)]
    [InlineData(8, SerialStopBits.Two, true)]
    [InlineData(4, SerialStopBits.One, false)]
    [InlineData(9, SerialStopBits.One, false)]
    public void ValidateFraming_EnforcesWindowsPortRules(int dataBits, SerialStopBits stopBits, bool valid)
    {
        var error = SerialTarget.ValidateFraming(dataBits, stopBits);
        Assert.Equal(valid, error is null);
    }

    [Theory]
    [InlineData(0, 8, SerialStopBits.One)]
    [InlineData(49, 8, SerialStopBits.One)]
    [InlineData(4_000_001, 8, SerialStopBits.One)]
    [InlineData(9600, 8, SerialStopBits.OnePointFive)]
    [InlineData(9600, 5, SerialStopBits.Two)]
    [InlineData(9600, 4, SerialStopBits.One)]
    public void Open_InvalidLineSettings_ThrowsSerialSessionException(int baud, int dataBits, SerialStopBits stopBits)
    {
        using var serial = new SerialTerminalSession();
        var error = Assert.Throws<SerialSessionException>(() => serial.Open(new SerialTarget
        {
            PortName = "COM3",
            BaudRate = baud,
            DataBits = dataBits,
            StopBits = stopBits,
        }));
        Assert.False(error.PortMissing);
        Assert.Contains("does not accept", error.Message);
    }

    [Fact]
    public void Open_MissingPort_SaysItIsNotConnected()
    {
        // COM250 is free on any machine a test runs on.
        using var serial = new SerialTerminalSession();
        var error = Assert.Throws<SerialSessionException>(() => serial.Open(new SerialTarget { PortName = "COM250" }));
        Assert.True(error.PortMissing);
        Assert.Contains("not connected", error.Message);
    }

    [Fact]
    public void PortList_ReturnsNormalizedSortedNames()
    {
        var ports = SerialPortList.List();
        Assert.All(ports, port => Assert.Equal(port.PortName, SerialTarget.NormalizePortName(port.PortName)));
        Assert.Equal(ports.OrderBy(port => int.Parse(port.PortName[3..])).Select(p => p.PortName), ports.Select(p => p.PortName));
    }
}
