using Sessions.Core.Sftp;

namespace Sessions.Core.Tests;

public sealed class Osc7WorkingDirectoryTests
{
    [Theory]
    [InlineData("file://server/home/boden/work", "server", "/home/boden/work")]
    [InlineData("FILE://server/home/boden/My%20Files", "server", "/home/boden/My Files")]
    [InlineData("file:///var/log", "", "/var/log")]
    [InlineData("file://server/home/what?#now", "server", "/home/what?#now")]
    [InlineData("file://server//home///boden/", "server", "/home/boden")]
    public void Parser_accepts_valid_file_uris(string payload, string host, string path)
    {
        Assert.True(Osc7WorkingDirectoryParser.TryParse(payload, out var result));
        Assert.Equal(host, result!.Host);
        Assert.Equal(path, result.Path);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://server/home/boden")]
    [InlineData("file://server")]
    [InlineData("file://user@server/home/boden")]
    [InlineData("file://server/home/%2")]
    [InlineData("file://server/home/%GG")]
    [InlineData("file://server/home/%00bad")]
    public void Parser_rejects_untrusted_payloads(string? payload) =>
        Assert.False(Osc7WorkingDirectoryParser.TryParse(payload, out _));

    [Fact]
    public void Parser_rejects_oversize_payload()
    {
        var payload = "file://server/" + new string('a', Osc7WorkingDirectoryParser.MaxPayloadLength);
        Assert.False(Osc7WorkingDirectoryParser.TryParse(payload, out _));
    }

    [Fact]
    public void Tracker_clears_a_nested_hosts_path_and_recovers_on_return()
    {
        var tracker = new Osc7WorkingDirectoryTracker();
        tracker.Observe(new("server", "/home/boden"));
        tracker.Observe(new("nested", "/root"));

        Assert.Null(tracker.Path);
        Assert.True(tracker.HostMismatch);

        tracker.Observe(new("server.", "/home/boden/work"));
        Assert.Equal("/home/boden/work", tracker.Path);
        Assert.False(tracker.HostMismatch);
    }

    [Fact]
    public void Tracker_reset_removes_the_previous_connections_report()
    {
        var tracker = new Osc7WorkingDirectoryTracker();
        tracker.Observe(new("server", "/home/boden"));
        tracker.Reset();
        Assert.Null(tracker.Path);
        Assert.False(tracker.HostMismatch);
    }
}
