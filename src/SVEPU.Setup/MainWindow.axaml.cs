using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SVEPU.Core.Services;

namespace SVEPU.Setup;

public sealed partial class MainWindow : Window
{
    private readonly AppSetupService _setupService = new();
    private readonly bool _launchedAsUninstaller;
    private bool _isWorking;
    private bool _isFinished;
    private bool _didUninstall;

    public MainWindow() : this(false, null)
    {
    }

    public MainWindow(bool isUninstallMode, string? explicitInstallDir)
    {
        _launchedAsUninstaller = isUninstallMode;
        InitializeComponent();

        LoadWindowIcon();
        ConfigurePlatformText(explicitInstallDir);
    }

    private void LoadWindowIcon()
    {
        try
        {
            using var resStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("SVEPU.AppIcon.png");
            if (resStream != null)
            {
                var bmp = new Bitmap(resStream);
                SetupHeaderIcon.Source = bmp;
                Icon = new WindowIcon(bmp);
                return;
            }
        }
        catch
        {
            // Fall back to external asset locator
        }

        var path = AssetLocator.FindAssetFile("app-icon.png");
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            try
            {
                var bmp = new Bitmap(path);
                SetupHeaderIcon.Source = bmp;
                Icon = new WindowIcon(bmp);
            }
            catch
            {
                // Ignore icon errors
            }
        }
    }

    private void ConfigurePlatformText(string? explicitInstallDir)
    {
        string osLabel = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "Windows Setup"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? "macOS Setup"
                : "Linux Setup";
        OsPlatformBadgeText.Text = osLabel;

        string defaultDir = !string.IsNullOrWhiteSpace(explicitInstallDir)
            ? explicitInstallDir
            : AppSetupService.GetDefaultInstallDirectory();
        InstallDirTextBox.Text = defaultDir;

        if (_launchedAsUninstaller)
        {
            Title = "Uninstall SVEPU - Stardew Valley Easy Port Utility";
            SetupTitleText.Text = "Uninstall SVEPU";
            SetupSubtitleText.Text = "Remove Stardew Valley Easy Port Utility from your device";
            Step1HeadingText.Text = "Confirm SVEPU Uninstallation";
            Step1IntroText.Text = "Click 'Uninstall SVEPU' below to remove the application files, shortcuts, and system registration.";
            OptionsGroupBorder.IsVisible = false;
            BrowseInstallDirBtn.IsEnabled = false;
            InstallDirTextBox.IsReadOnly = true;
            PrimaryActionBtn.Content = "Uninstall SVEPU";
        }
        else if (AppSetupService.IsAlreadyInstalled(defaultDir))
        {
            UninstallExistingBtn.IsVisible = true;
            PrimaryActionBtn.Content = "Update / Reinstall →";
        }
    }

    private async void OnBrowseInstallDirClicked(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose SVEPU Installation Folder",
            AllowMultiple = false
        });

        if (folders.Count > 0)
        {
            var path = folders[0].TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(path))
            {
                InstallDirTextBox.Text = path;
                UninstallExistingBtn.IsVisible = AppSetupService.IsAlreadyInstalled(path);
            }
        }
    }

    private void OnCancelClicked(object? sender, RoutedEventArgs e)
    {
        if (!_isWorking)
        {
            Close();
        }
    }

    private async void OnUninstallClicked(object? sender, RoutedEventArgs e)
    {
        if (_isWorking)
        {
            return;
        }
        await ExecuteUninstallAsync();
    }

    private async void OnPrimaryActionClicked(object? sender, RoutedEventArgs e)
    {
        if (_isWorking)
        {
            return;
        }

        if (_isFinished)
        {
            if (!_didUninstall && LaunchSvepuOnExitCheck.IsChecked == true)
            {
                string exePath = AppSetupService.GetInstalledPackagerExecutablePath(InstallDirTextBox.Text ?? string.Empty);
                if (File.Exists(exePath))
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = exePath,
                            WorkingDirectory = Path.GetDirectoryName(exePath) ?? string.Empty,
                            UseShellExecute = true
                        });
                    }
                    catch
                    {
                        // Ignore launch error
                    }
                }
            }

            Close();
            return;
        }

        if (_launchedAsUninstaller)
        {
            await ExecuteUninstallAsync();
        }
        else
        {
            await ExecuteInstallAsync();
        }
    }

    private async Task ExecuteInstallAsync()
    {
        string installDir = InstallDirTextBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(installDir))
        {
            FooterStatusText.Text = "Please choose an installation folder.";
            return;
        }

        _isWorking = true;
        FooterStatusText.Text = string.Empty;
        Step1ConfigPanel.IsVisible = false;
        Step2ProgressPanel.IsVisible = true;
        CancelBtn.IsVisible = false;
        UninstallExistingBtn.IsVisible = false;
        PrimaryActionBtn.IsEnabled = false;
        ProgressHeadingText.Text = "Installing SVEPU...";

        var progress = new Progress<(double Percent, string Status)>(report =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                SetupProgressBar.Value = report.Percent;
                SetupProgressStatusText.Text = report.Status;
            });
        });

        try
        {
            await using var payloadStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("SVEPU.AppPayload.zip");
            var res = await _setupService.InstallAsync(
                payloadStream,
                installDir,
                createDesktopShortcut: CreateDesktopShortcutCheck.IsChecked == true,
                createMenuShortcut: CreateMenuShortcutCheck.IsChecked == true,
                registerSystemUninstaller: RegisterUninstallerCheck.IsChecked == true,
                progress: progress);

            if (!res.Success)
            {
                FooterStatusText.Text = res.Message;
                Step2ProgressPanel.IsVisible = false;
                Step1ConfigPanel.IsVisible = true;
                CancelBtn.IsVisible = true;
                PrimaryActionBtn.IsEnabled = true;
                _isWorking = false;
                return;
            }

            InstalledLocationSummaryText.Text = $"Installed Location: {installDir}";
            Step2ProgressPanel.IsVisible = false;
            Step3CompletePanel.IsVisible = true;
            PrimaryActionBtn.Content = "Finish";
            PrimaryActionBtn.IsEnabled = true;
            _isWorking = false;
            _isFinished = true;
            _didUninstall = false;
        }
        catch (Exception ex)
        {
            FooterStatusText.Text = $"Setup failed: {ex.Message}";
            Step2ProgressPanel.IsVisible = false;
            Step1ConfigPanel.IsVisible = true;
            CancelBtn.IsVisible = true;
            PrimaryActionBtn.IsEnabled = true;
            _isWorking = false;
        }
    }

    private async Task ExecuteUninstallAsync()
    {
        string installDir = InstallDirTextBox.Text?.Trim() ?? string.Empty;

        _isWorking = true;
        FooterStatusText.Text = string.Empty;
        Step1ConfigPanel.IsVisible = false;
        Step2ProgressPanel.IsVisible = true;
        CancelBtn.IsVisible = false;
        UninstallExistingBtn.IsVisible = false;
        PrimaryActionBtn.IsEnabled = false;
        ProgressHeadingText.Text = "Uninstalling SVEPU...";

        var progress = new Progress<(double Percent, string Status)>(report =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                SetupProgressBar.Value = report.Percent;
                SetupProgressStatusText.Text = report.Status;
            });
        });

        var res = await _setupService.UninstallAsync(installDir, progress);

        CompleteHeadingText.Text = "✓ SVEPU Uninstalled";
        CompleteDetailsText.Text = res.Message;
        InstalledLocationSummaryText.Text = $"Removed Location: {installDir}";
        LaunchSvepuOnExitCheck.IsVisible = false;

        Step2ProgressPanel.IsVisible = false;
        Step3CompletePanel.IsVisible = true;
        PrimaryActionBtn.Content = "Close";
        PrimaryActionBtn.IsEnabled = true;
        _isWorking = false;
        _isFinished = true;
        _didUninstall = true;
    }
}
