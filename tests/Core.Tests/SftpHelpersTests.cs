using Resesh.Core.Sftp;
using Resesh.Core.Ssh;

namespace Resesh.Core.Tests;

public sealed class RemotePathTests
{
    [Theory]
    [InlineData("", "/")]
    [InlineData("   ", "/")]
    [InlineData("/", "/")]
    [InlineData("//", "/")]
    [InlineData("/home//user/", "/home/user")]
    [InlineData("home/user", "/home/user")]
    [InlineData("/var/log", "/var/log")]
    [InlineData("/var/log/..", "/var")]
    [InlineData("/var/./log/", "/var/log")]
    [InlineData("/var/log/../../..", "/")]
    [InlineData("/..", "/")]
    [InlineData("/a/.../b", "/a/.../b")]
    public void Normalize_collapses_and_roots(string input, string expected) =>
        Assert.Equal(expected, RemotePath.Normalize(input));

    [Theory]
    [InlineData("/", "etc", "/etc")]
    [InlineData("/home/user", "file.txt", "/home/user/file.txt")]
    [InlineData("/home/user/", "file.txt", "/home/user/file.txt")]
    public void Join_handles_root_and_trailing_slash(string dir, string name, string expected) =>
        Assert.Equal(expected, RemotePath.Join(dir, name));

    [Theory]
    [InlineData("/", "/")]
    [InlineData("/etc", "/")]
    [InlineData("/home/user/docs", "/home/user")]
    [InlineData("/var/log/..", "/")]
    public void Parent_of_root_is_root(string path, string expected) =>
        Assert.Equal(expected, RemotePath.Parent(path));

    [Theory]
    [InlineData("/home/user/file.txt", "file.txt")]
    [InlineData("/etc", "etc")]
    [InlineData("/", "/")]
    public void FileName_returns_last_segment(string path, string expected) =>
        Assert.Equal(expected, RemotePath.FileName(path));

    [Theory]
    [InlineData(null, "/home/boden", "/home/boden")]
    [InlineData("~", "/home/boden", "/home/boden")]
    [InlineData("~/work", "/home/boden", "/home/boden/work")]
    [InlineData("/var/log", "/home/boden", "/var/log")]
    [InlineData("~/work/../src", "/home/boden", "/home/boden/src")]
    [InlineData("relative/path", "/home/boden", "/home/boden/relative/path")]
    [InlineData("~alice/work", "/home/boden", null)]
    public void ResolveShellPath_resolves_home_absolute_and_relative_paths(string? path, string home, string? expected) =>
        Assert.Equal(expected, RemotePath.ResolveShellPath(path, home));

    [Theory]
    [InlineData("logs", "/var", "/var/logs")]
    [InlineData("..", "/var/log", "/var")]
    [InlineData("../..", "/var/log", "/")]
    [InlineData("./x/../y", "/srv", "/srv/y")]
    [InlineData("logs", null, "/home/boden/logs")]
    public void ResolveShellPath_takes_relative_paths_from_the_current_folder(string path, string? current, string expected) =>
        Assert.Equal(expected, RemotePath.ResolveShellPath(path, "/home/boden", current));

    [Theory]
    [InlineData("/var/log", "/var/log")]
    [InlineData("/srv/app/../web/", "/srv/web")]
    [InlineData("relative", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("/tmp/a\u0007b", null)]
    [InlineData("/tmp/a\u009bb", null)] // C1 CSI: a terminal control as much as ESC [
    [InlineData("/tmp/a\u007fb", null)]
    public void FromUntrusted_accepts_only_absolute_paths_without_controls(string? path, string? expected) =>
        Assert.Equal(expected, RemotePath.FromUntrusted(path));

    [Theory]
    [InlineData("notes.txt", true)]
    [InlineData("...", true)]
    [InlineData("back\\slash", true)]
    [InlineData("", false)]
    [InlineData(".", false)]
    [InlineData("..", false)]
    [InlineData("a/b", false)]
    public void IsValidName_refuses_dot_names_and_slashes(string name, bool valid) =>
        Assert.Equal(valid, RemotePath.IsValidName(name));

    [Fact]
    public void UniqueName_returns_original_when_free()
    {
        Assert.Equal("log.txt", RemotePath.UniqueName("log.txt", _ => false));
    }

    [Fact]
    public void UniqueName_appends_counter_preserving_extension()
    {
        var taken = new HashSet<string> { "log.txt", "log (2).txt" };
        Assert.Equal("log (3).txt", RemotePath.UniqueName("log.txt", taken.Contains));
    }

    [Fact]
    public void UniqueName_handles_names_without_extension()
    {
        var taken = new HashSet<string> { "backup" };
        Assert.Equal("backup (2)", RemotePath.UniqueName("backup", taken.Contains));
    }
}

public sealed class WindowsDownloadPathTests
{
    [Fact]
    public void Combine_accepts_a_normal_remote_leaf_name()
    {
        var root = Path.Combine(Path.GetTempPath(), "resesh-download-test");

        var result = WindowsDownloadPath.Combine(root, root, "report.txt");

        Assert.Equal(Path.GetFullPath(Path.Combine(root, "report.txt")), result);
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("..\\outside.txt")]
    [InlineData("folder\\file.txt")]
    [InlineData("C:\\outside.txt")]
    [InlineData("\\\\server\\share.txt")]
    [InlineData("bad:name.txt")]
    [InlineData("CON")]
    [InlineData("con.log")]
    [InlineData("CON .txt")]
    [InlineData("COM1.log")]
    [InlineData("LPT9")]
    [InlineData("file.")]
    [InlineData("file ")]
    public void ValidateName_rejects_names_that_are_unsafe_on_Windows(string name)
    {
        Assert.Throws<InvalidDataException>(() => WindowsDownloadPath.ValidateName(name));
        Assert.False(WindowsDownloadPath.IsValidName(name));
    }

    [Theory]
    [InlineData("notes.txt")]
    [InlineData(".config")]
    [InlineData("CONFIG")]
    public void IsValidName_accepts_ordinary_names(string name) => Assert.True(WindowsDownloadPath.IsValidName(name));

    [Fact]
    public void Combine_rejects_a_parent_outside_the_download_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "resesh-download-test", "root");
        var outside = Path.Combine(root, "..", "outside");

        Assert.Throws<InvalidDataException>(() =>
            WindowsDownloadPath.Combine(root, outside, "report.txt"));
    }
}

public sealed class UnixPermissionsTests
{
    [Theory]
    [InlineData(755, false, false, "-rwxr-xr-x")]
    [InlineData(644, false, false, "-rw-r--r--")]
    [InlineData(755, true, false, "drwxr-xr-x")]
    [InlineData(777, false, true, "lrwxrwxrwx")]
    [InlineData(0, false, false, "----------")]
    [InlineData(-1, false, false, "----------")]
    [InlineData(-1, true, false, "d---------")]
    public void Format_matches_ls_style(int mode, bool isDir, bool isLink, string expected) =>
        Assert.Equal(expected, UnixPermissions.Format((short)mode, isDir, isLink));

    [Theory]
    [InlineData("755", 755)]
    [InlineData("0644", 644)]
    [InlineData(" 700 ", 700)]
    [InlineData("0", 0)]
    public void TryParseOctal_accepts_valid_modes(string text, int expected)
    {
        Assert.True(UnixPermissions.TryParseOctal(text, out var mode));
        Assert.Equal((short)expected, mode);
    }

    [Theory]
    [InlineData("788")]  // 8 is not an octal digit
    [InlineData("7777")] // no setuid support — three digits only
    [InlineData("rwx")]
    [InlineData("")]
    public void TryParseOctal_rejects_invalid_modes(string text) =>
        Assert.False(UnixPermissions.TryParseOctal(text, out _));

    [Fact]
    public void Format_and_parse_round_trip()
    {
        Assert.True(UnixPermissions.TryParseOctal("640", out var mode));
        Assert.Equal("-rw-r-----", UnixPermissions.Format(mode, isDirectory: false, isSymlink: false));
    }
}

public sealed class RemoteFileEntryTests
{
    private static RemoteFileEntry Entry(string name, bool isDir) =>
        new(name, "/" + name, isDir, IsSymlink: false, Size: 0, Modified: DateTime.UnixEpoch, Mode: 644);

    [Fact]
    public void Sort_puts_directories_first_then_names_case_insensitively()
    {
        var sorted = RemoteFileEntry.Sort(new[]
        {
            Entry("zeta.txt", isDir: false),
            Entry("Alpha", isDir: true),
            Entry("beta", isDir: true),
            Entry("apple.txt", isDir: false),
        });
        Assert.Equal(new[] { "Alpha", "beta", "apple.txt", "zeta.txt" }, sorted.Select(e => e.Name));
    }

    [Fact]
    public void PermissionText_reflects_entry_kind()
    {
        Assert.Equal("drw-r--r--", Entry("d", isDir: true).PermissionText);
        Assert.Equal("-rw-r--r--", Entry("f", isDir: false).PermissionText);
    }
}

public sealed class TmuxCurrentPathTests
{
    private static readonly Guid Id = Guid.Parse("aabbccdd-eeff-0011-2233-445566778899");

    [Fact]
    public void CurrentPathCommand_lists_all_panes_on_the_private_socket()
    {
        Assert.Equal(
            "tmux -L resesh-app list-panes -a -F '#{session_name} #{pane_active} #{pane_current_path}'",
            TmuxPersistence.CurrentPathCommand());
    }

    [Fact]
    public void ParseCurrentPath_matches_the_slot_session_and_active_pane()
    {
        var output =
            "sother000000 1 /etc\n" +
            "saabbccddeeff 0 /var/inactive\n" +
            "saabbccddeeff 1 /var/log/my app\n"; // paths may contain spaces
        Assert.Equal("/var/log/my app", TmuxPersistence.ParseCurrentPath(output, Id, 0));
    }

    [Fact]
    public void ParseCurrentPath_distinguishes_clone_slots()
    {
        var output =
            "saabbccddeeff 1 /home/a\n" +
            "saabbccddeeff-2 1 /home/b\n";
        Assert.Equal("/home/a", TmuxPersistence.ParseCurrentPath(output, Id, 0));
        Assert.Equal("/home/b", TmuxPersistence.ParseCurrentPath(output, Id, 1));
    }

    [Fact]
    public void ParseCurrentPath_keeps_a_trailing_space_in_the_folder_name()
    {
        Assert.Equal("/srv/old ", TmuxPersistence.ParseCurrentPath("saabbccddeeff 1 /srv/old \r\n", Id, 0));
        Assert.Null(TmuxPersistence.ParseCurrentPath("saabbccddeeff 1 /srv/\u009b31m\n", Id, 0));
    }

    [Theory]
    [InlineData("")]
    [InlineData("sother000000 1 /etc")]                // wrong session
    [InlineData("saabbccddeeff 0 /etc")]               // no active pane line
    [InlineData("saabbccddeeff 1 relative/not/rooted")] // not an absolute path
    [InlineData("no session option: resesh-app")]     // tmux error text on stdout
    public void ParseCurrentPath_returns_null_when_absent_or_malformed(string output) =>
        Assert.Null(TmuxPersistence.ParseCurrentPath(output, Id, 0));

    [Fact]
    public void DiscoveryCommand_lists_tmux_metadata_on_the_private_socket()
    {
        Assert.Equal(
            "tmux -L resesh-app list-panes -a -F '#{session_name}|#{pane_active}|#{session_attached}|#{pane_current_path}'",
            TmuxPersistence.DiscoveryCommand());
    }

    [Fact]
    public void ParseSessions_returns_only_matching_active_sessions_in_slot_order()
    {
        var output =
            "saabbccddeeff-3|1|2|/srv/app|with-pipe\n" +
            "saabbccddeeff|1|0|/home/a\n" +
            "saabbccddeeff-2|0|0|/inactive\n" +
            "sother000000|1|0|/other\n";

        var sessions = TmuxPersistence.ParseSessions(output, Id);

        Assert.Collection(sessions,
            primary =>
            {
                Assert.Equal(0, primary.Slot);
                Assert.Equal("/home/a", primary.CurrentPath);
                Assert.Equal(0, primary.AttachedClients);
            },
            third =>
            {
                Assert.Equal(2, third.Slot);
                Assert.Equal("/srv/app|with-pipe", third.CurrentPath);
                Assert.Equal(2, third.AttachedClients);
            });
    }

    [Fact]
    public void NextAvailableSlot_accounts_for_remote_and_open_tabs()
    {
        Assert.Equal(3, TmuxPersistence.NextAvailableSlot(new[] { 0, 2, 1 }));
        Assert.Equal(0, TmuxPersistence.NextAvailableSlot(Array.Empty<int>()));
    }
}

/// <summary>When an upload over an existing file may go to a temporary file renamed into place,
/// and when it must write through the existing one instead.</summary>
public sealed class SftpOverwriteTests
{
    [Fact]
    public void ARegularFileWhoseNameResolvesToItselfIsReplacedByRename() =>
        Assert.True(SftpSession.ReplacesByRename(true, false, "/home/u/notes.txt", "/home/u/notes.txt"));

    [Fact]
    public void ASymlinkIsWrittenThrough()
    {
        // SSH.NET's Get resolves the link on the server and lstats the target, so a link to a
        // regular file reports a regular file at the target's path.
        Assert.False(SftpSession.ReplacesByRename(true, false, "/home/u/dotfiles/bashrc", "/home/u/.bashrc"));
        Assert.False(SftpSession.ReplacesByRename(true, true, "/home/u/.bashrc", "/home/u/.bashrc"));
    }

    [Fact]
    public void AnythingElseOrAnUnresolvedFolderIsWrittenInPlace()
    {
        Assert.False(SftpSession.ReplacesByRename(false, false, "/dev/null", "/dev/null"));
        Assert.False(SftpSession.ReplacesByRename(true, false, "/home/u/notes.txt", null));
    }

    [Fact]
    public void OnlyAFileTheLoginWouldOwnTheSameWayIsReplacedByRename()
    {
        Assert.True(SftpSession.KeepsOwnership(1000, 1000, 1000, 1000));
        Assert.False(SftpSession.KeepsOwnership(0, 0, 1000, 1000));
        Assert.False(SftpSession.KeepsOwnership(1000, 50, 1000, 1000));
    }
}
