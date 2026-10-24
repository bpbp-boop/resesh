using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Resesh.Core.Storage;
using Windows.ApplicationModel.DataTransfer;

namespace Resesh.App.Controls;

/// <summary>An open-source project resesh is built on, with its license.</summary>
public sealed record AboutCredit(string Name, string Purpose, string License, string Url)
{
    public Uri Uri => new(Url);
}

/// <summary>The About section of Settings: version, system details, links, and credits.</summary>
public sealed partial class AboutView : UserControl
{
    private static readonly AboutCredit[] Credits =
    [
        new("Ghostty (libghostty-vt)", "Terminal emulation", "MIT", "https://github.com/ghostty-org/ghostty"),
        new("SSH.NET", "SSH and SFTP", "MIT", "https://github.com/sshnet/SSH.NET"),
        new("zmx", "Persistent remote sessions", "MIT", "https://github.com/neurosnap/zmx"),
        new("Windows App SDK and WinUI", "App framework", "MIT", "https://github.com/microsoft/WindowsAppSDK"),
        new("Windows Community Toolkit", "Settings controls, MVVM", "MIT", "https://github.com/CommunityToolkit/Windows"),
        new("Vortice.Windows", "Direct2D and Direct3D bindings", "MIT", "https://github.com/amerkoleci/Vortice.Windows"),
        new("Microsoft.Data.Sqlite", "Command history storage", "MIT", "https://github.com/dotnet/efcore"),
        new("Lucide", "Session tree icons", "ISC", "https://lucide.dev/"),
        new("Simple Icons", "Brand icons", "CC0", "https://simpleicons.org/"),
    ];

    private readonly string _dataFolder = Path.GetDirectoryName(
        Program.StorePath("sessions.json", SessionStore.DefaultPath))!;
    private readonly string _logsFolder = AppDataPaths.Local();
    private int _copyFeedbackVersion;

    public AboutView()
    {
        InitializeComponent();
        AppIcon.Source = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico")))
        {
            DecodePixelWidth = 64,
        };

        var version = AppBuildInfo.VersionText;
        if (AppBuildInfo.Commit is { } commit)
            version += $" · {commit}";
        VersionLine.Text = $"{version} · {AppBuildInfo.Architecture}";

        WindowsValue.Text = AppBuildInfo.WindowsText;
        RuntimeValue.Text = AppBuildInfo.DotNetText;
        WindowsAppSdkValue.Text = AppBuildInfo.WindowsAppSdkVersion ?? "";
        WindowsAppSdkCard.Visibility = PresentationValues.Visible(AppBuildInfo.WindowsAppSdkVersion is not null);
        GhosttyValue.Text = AppBuildInfo.GhosttyVersion is { } ghostty ? $"libghostty-vt {ghostty}" : "";
        GhosttyCard.Visibility = PresentationValues.Visible(AppBuildInfo.GhosttyVersion is not null);

        DataFolderCard.Description = _dataFolder;
        LogsFolderCard.Description = _logsFolder;
        CreditsList.ItemsSource = Credits;
    }

    private void CopyDetails_Click(object sender, RoutedEventArgs e)
    {
        var package = new DataPackage();
        package.SetText(AppBuildInfo.DetailsText());
        Clipboard.SetContent(package);

        // Brief confirmation in place, then back to the normal label.
        var version = ++_copyFeedbackVersion;
        CopyDetailsIcon.Glyph = "";
        CopyDetailsText.Text = "Copied";
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(2);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            if (version != _copyFeedbackVersion)
                return;
            CopyDetailsIcon.Glyph = "";
            CopyDetailsText.Text = "Copy details";
        };
        timer.Start();
    }

    private void Link_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string url })
            Launch(url);
    }

    private void DataFolder_Click(object sender, RoutedEventArgs e) => OpenFolder(_dataFolder);

    private void LogsFolder_Click(object sender, RoutedEventArgs e) => OpenFolder(_logsFolder);

    private static void Launch(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            App.ReportRecoverableError(exception);
        }
    }

    private static void OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var startInfo = new ProcessStartInfo { FileName = "explorer.exe", UseShellExecute = false };
            startInfo.ArgumentList.Add(folder);
            Process.Start(startInfo);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception)
        {
            App.ReportRecoverableError(exception);
        }
    }
}
