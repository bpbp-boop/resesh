using Sessions.Core.Sftp;

namespace Sessions.Core.Tests;

public sealed class Osc3008ContextTests
{
    [Fact]
    public void Parser_accepts_the_stock_systemd_shell_context()
    {
        const string payload = "start=2c103ad4-2fa5-4ec7-86e6-c644485ed292;type=shell;" +
            "machineid=e8bdb957c6b942a784c4f11b8f8d3eae;user=boden;hostname=vm-1;" +
            "bootid=40b62a45-39d8-4ec8-a66b-0cd219f98b00;pid=1234;cwd=/home/boden/work";

        Assert.True(Osc3008ContextParser.TryParse(payload, out var context));
        Assert.Equal(Osc3008ContextAction.Start, context!.Action);
        Assert.Equal("shell", context.Type);
        Assert.Equal("vm-1", context.Hostname);
        Assert.Equal("/home/boden/work", context.WorkingDirectory);
    }

    [Theory]
    [InlineData("end=cmd-1;exit=success", "success", null)]
    [InlineData("end=cmd-1;exit=failure;status=127", "failure", 127)]
    [InlineData("end=cmd-1;exit=failure;status=130;signal=SIGINT", "failure", 130)]
    public void Parser_accepts_command_results(string payload, string exit, int? status)
    {
        Assert.True(Osc3008ContextParser.TryParse(payload, out var context));
        Assert.Equal(Osc3008ContextAction.End, context!.Action);
        Assert.Equal(exit, context.Exit);
        Assert.Equal(status, context.Status);
    }

    [Fact]
    public void Parser_decodes_only_the_two_specified_escapes()
    {
        Assert.True(Osc3008ContextParser.TryParse(
            @"start=id;type=command;hostname=vm;cwd=/srv/a\x3bb\x5cc;cmdline=printf\x3bnext",
            out var context));

        Assert.Equal(@"/srv/a;b\c", context!.WorkingDirectory);
        Assert.Equal("printf;next", context.CommandLine);
    }

    [Theory]
    [InlineData("")]
    [InlineData("other=id;type=shell")]
    [InlineData("start=;type=shell")]
    [InlineData("start=bad\\id;type=shell")]
    [InlineData("start=na\u00efve;type=shell")]
    [InlineData("start=id;type=shell;cwd=relative")]
    public void Parser_rejects_an_invalid_command_or_ignores_an_invalid_field(string payload)
    {
        var parsed = Osc3008ContextParser.TryParse(payload, out var context);
        if (payload.EndsWith("cwd=relative", StringComparison.Ordinal))
        {
            Assert.True(parsed);
            Assert.Null(context!.WorkingDirectory);
        }
        else
        {
            Assert.False(parsed);
        }
    }

    [Fact]
    public void Parser_rejects_an_oversize_payload()
    {
        var payload = "start=id;type=shell;unknown=" +
            new string('a', Osc3008ContextParser.MaxPayloadLength);
        Assert.False(Osc3008ContextParser.TryParse(payload, out _));
    }
}
