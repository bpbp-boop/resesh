using Resesh.Core.Sftp;

namespace Resesh.Core.Tests;

public sealed class Osc7WorkingDirectoryTests
{
    [Theory]
    [InlineData("file://server/home/boden/work", "server", "/home/boden/work")]
    [InlineData("FILE://server/home/boden/My%20Files", "server", "/home/boden/My Files")]
    [InlineData("file:///var/log", "", "/var/log")]
    [InlineData("file://server/home/what?#now", "server", "/home/what?#now")]
    [InlineData("file://server//home///boden/", "server", "/home/boden")]
    [InlineData("file://server/home/boden/../work", "server", "/home/work")]
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
    [InlineData("file://server/home/%C2%9B31m")] // C1 CSI, which a terminal acts on like ESC [
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

    [Fact]
    public void Tracker_lets_the_sessions_own_host_take_over_from_a_nested_first_report()
    {
        var tracker = new Osc7WorkingDirectoryTracker();
        tracker.SetKnownHosts(["web01.example.com", null]);
        tracker.Observe(new("nested", "/root")); // reconnected straight into ssh-in-tmux
        Assert.Equal("/root", tracker.Path);

        tracker.Observe(new("web01", "/home/boden"));
        Assert.Equal("/home/boden", tracker.Path);
        Assert.False(tracker.HostMismatch);

        tracker.Observe(new("nested", "/root"));
        Assert.Null(tracker.Path);
        Assert.True(tracker.HostMismatch);
    }

    [Fact]
    public void Tracker_keeps_a_known_first_host_against_later_unknown_ones()
    {
        var tracker = new Osc7WorkingDirectoryTracker();
        tracker.SetKnownHosts(["web01"]);
        tracker.Observe(new("WEB01.example.com.", "/home/boden"));
        tracker.Observe(new("web01", "/srv")); // the same machine, named without its domain
        Assert.Equal("/srv", tracker.Path);

        tracker.Observe(new("db02", "/var"));
        Assert.True(tracker.HostMismatch);
        Assert.Null(tracker.Path);
    }

    [Fact]
    public void Tracker_known_hosts_survive_reset_and_do_not_match_across_addresses()
    {
        var tracker = new Osc7WorkingDirectoryTracker();
        tracker.SetKnownHosts(["10.0.0.5"]);
        tracker.Reset();
        tracker.Observe(new("10", "/a")); // not the first label of an address
        tracker.Observe(new("10.0.0.5", "/b"));
        Assert.Equal("/b", tracker.Path);
        tracker.Observe(new("10", "/c"));
        Assert.True(tracker.HostMismatch);
    }
}
