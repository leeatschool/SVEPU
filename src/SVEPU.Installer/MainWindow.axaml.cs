using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SVEPU.Core.Models;
using SVEPU.Core.Services;

namespace SVEPU.Installer;

public sealed partial class MainWindow : Window
{
    private readonly GamePathDetectorService _pathDetector = new();
    private readonly SmapiInstallerService _smapiInstaller = new();
    private readonly PackageExtractorService _extractor = new();
    private readonly SteamLaunchOptionsService _steamLaunchService;

    private readonly PackageMetadata _metadata;
    private readonly string? _payloadDir;

    private InstallMode _installMode = InstallMode.Recommended;
    private GameServiceType _selectedService = GameServiceType.Steam;
    private int _currentStep = 1; // 1: Welcome/Mode, 2: Service/Path, 3: Custom Options, 4: Progress, 5: Done
    private bool _isInstalling;
    private string _smapiFinalSummary = "SMAPI Status: Verified";
    private string _steamLaunchFinalSummary = "Steam Launch Options: Not applicable";

    public MainWindow() : this(null)
    {
    }

    public MainWindow(string? explicitPackageRoot)
    {
        _steamLaunchService = new SteamLaunchOptionsService(_pathDetector);
        InitializeComponent();

        var (meta, customIconPath, payloadDir) = PackageExtractorService.LoadPackagePayload(explicitPackageRoot);
        _metadata = meta;
        _payloadDir = payloadDir;

        LoadIcons(customIconPath);
        PopulatePackageHeaderAndDetails();
        SelectService(GameServiceType.Steam);
        UpdateStepUi();
    }

    private void LoadIcons(string? customIconPath)
    {
        string? iconToLoad = customIconPath;
        if (string.IsNullOrWhiteSpace(iconToLoad) || !File.Exists(iconToLoad))
        {
            iconToLoad = AssetLocator.FindAssetFile("app-icon.png");
        }

        if (!string.IsNullOrWhiteSpace(iconToLoad) && File.Exists(iconToLoad))
        {
            try
            {
                var bitmap = new Bitmap(iconToLoad);
                HeaderIconImage.Source = bitmap;
                Icon = new WindowIcon(bitmap);
            }
            catch
            {
                // Ignore icon load errors
            }
        }

        TryLoadOptionalServiceIcon(SteamServiceIcon, "steam.png");
        TryLoadOptionalServiceIcon(GogServiceIcon, "gog.png");
        TryLoadOptionalServiceIcon(XboxServiceIcon, "xbox.png");
        TryLoadOptionalServiceIcon(FolderServiceIcon, "folder.png");
    }

    private static void TryLoadOptionalServiceIcon(Image imageControl, string fileName)
    {
        var path = AssetLocator.FindAssetFile(fileName);
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            try
            {
                imageControl.Source = new Bitmap(path);
                imageControl.IsVisible = true;
            }
            catch
            {
                imageControl.IsVisible = false;
            }
        }
    }

    private void PopulatePackageHeaderAndDetails()
    {
        Title = $"{_metadata.PackName} - SVEPU Mod Installer";
        HeaderTitleText.Text = $"Welcome to the {_metadata.PackName} Installer";
        HeaderSubtitleText.Text = $"By {_metadata.Author} • Version {_metadata.Version}";

        PackDescriptionText.Text = string.IsNullOrWhiteSpace(_metadata.Description)
            ? "This wizard will automatically detect your Stardew Valley installation and extract the packaged mods into your Mods folder."
            : _metadata.Description;

        PackModCountText.Text = $"Included Mods in Package: {_metadata.Mods.Count}";
        CustomModsItemsControl.ItemsSource = _metadata.Mods;
    }

    private void OnRecommendedModeClicked(object? sender, RoutedEventArgs e)
    {
        _installMode = InstallMode.Recommended;
        RecommendedModeCard.Classes.Set("selected", true);
        CustomModeCard.Classes.Set("selected", false);
        UpdateStepUi();
    }

    private void OnCustomModeClicked(object? sender, RoutedEventArgs e)
    {
        _installMode = InstallMode.Custom;
        RecommendedModeCard.Classes.Set("selected", false);
        CustomModeCard.Classes.Set("selected", true);
        UpdateStepUi();
    }

    private void OnServiceSteamClicked(object? sender, RoutedEventArgs e) => SelectService(GameServiceType.Steam);
    private void OnServiceGogClicked(object? sender, RoutedEventArgs e) => SelectService(GameServiceType.GogGalaxy);
    private void OnServiceXboxClicked(object? sender, RoutedEventArgs e) => SelectService(GameServiceType.XboxApp);
    private void OnServiceCustomClicked(object? sender, RoutedEventArgs e) => SelectService(GameServiceType.Custom);

    private void SelectService(GameServiceType service)
    {
        _selectedService = service;
        ServiceSteamBtn.Classes.Set("selected", service == GameServiceType.Steam);
        ServiceGogBtn.Classes.Set("selected", service == GameServiceType.GogGalaxy);
        ServiceXboxBtn.Classes.Set("selected", service == GameServiceType.XboxApp);
        ServiceCustomBtn.Classes.Set("selected", service == GameServiceType.Custom);

        if (service != GameServiceType.Custom)
        {
            var detected = _pathDetector.DetectGamePath(service);
            if (!string.IsNullOrWhiteSpace(detected))
            {
                GamePathTextBox.Text = detected;
            }
            else if (string.IsNullOrWhiteSpace(GamePathTextBox.Text))
            {
                UpdatePathStatusDisplay(string.Empty);
            }
        }
    }

    private void OnGamePathTextChanged(object? sender, TextChangedEventArgs e)
    {
        UpdatePathStatusDisplay(GamePathTextBox.Text ?? string.Empty);
    }

    private void UpdatePathStatusDisplay(string rawPath)
    {
        FooterErrorText.Text = string.Empty;

        if (string.IsNullOrWhiteSpace(rawPath))
        {
            DetectedModsFolderText.Text = "Target Mods Folder: Not detected yet";
            DetectionStatusText.Text = $"Could not auto-detect Stardew Valley for {_selectedService}. Click 'Browse...' to select your Stardew Valley folder.";
            return;
        }

        string gameDir = GamePathDetectorService.NormalizeGamePath(rawPath);
        string modsDir = GamePathDetectorService.GetModsPath(rawPath);
        DetectedModsFolderText.Text = $"Target Mods Folder: {modsDir}";

        if (Directory.Exists(gameDir))
        {
            bool validSdv = GamePathDetectorService.IsValidStardewDirectory(gameDir);
            bool smapiInstalled = GamePathDetectorService.IsSmapiInstalled(gameDir);
            string smapiText = smapiInstalled
                ? "SMAPI is already installed."
                : "SMAPI was not detected (will be checked during install).";

            DetectionStatusText.Text = validSdv
                ? $"✓ Stardew Valley installation verified. {smapiText}"
                : $"Folder exists ({smapiText})";
        }
        else
        {
            DetectionStatusText.Text = "The specified directory does not exist yet. Click 'Browse...' to choose your Stardew Valley folder.";
        }
    }

    private async void OnBrowseGamePathClicked(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select your Stardew Valley Folder (or Mods Folder)",
            AllowMultiple = false
        });

        if (folders.Count > 0)
        {
            var path = folders[0].TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(path))
            {
                GamePathTextBox.Text = GamePathDetectorService.NormalizeGamePath(path);
            }
        }
    }

    private void OnSelectAllModsClicked(object? sender, RoutedEventArgs e)
    {
        foreach (var mod in _metadata.Mods)
        {
            mod.IsSelected = true;
        }
    }

    private void OnDeselectAllModsClicked(object? sender, RoutedEventArgs e)
    {
        foreach (var mod in _metadata.Mods)
        {
            mod.IsSelected = false;
        }
    }

    private void OnSmapiPromptYesClicked(object? sender, RoutedEventArgs e)
    {
        SmapiPromptYesCheck.IsChecked = true;
        SmapiPromptNoCheck.IsChecked = false;
    }

    private void OnSmapiPromptNoClicked(object? sender, RoutedEventArgs e)
    {
        SmapiPromptNoCheck.IsChecked = true;
        SmapiPromptYesCheck.IsChecked = false;
    }

    private async void OnSmapiPromptConfirmClicked(object? sender, RoutedEventArgs e)
    {
        SmapiPromptBox.IsVisible = false;
        bool downloadSmapi = SmapiPromptYesCheck.IsChecked == true;
        await ExecuteInstallationPipelineAsync(downloadSmapi);
    }

    private void OnBackClicked(object? sender, RoutedEventArgs e)
    {
        if (_isInstalling || _currentStep <= 1)
        {
            return;
        }

        FooterErrorText.Text = string.Empty;
        if (_currentStep == 4)
        {
            _currentStep = _installMode == InstallMode.Custom ? 3 : 2;
        }
        else
        {
            _currentStep--;
        }

        UpdateStepUi();
    }

    private async void OnNextOrInstallClicked(object? sender, RoutedEventArgs e)
    {
        if (_isInstalling)
        {
            return;
        }

        FooterErrorText.Text = string.Empty;

        if (_currentStep == 1)
        {
            _currentStep = 2;
            UpdateStepUi();
            return;
        }

        if (_currentStep == 2)
        {
            string gamePath = GamePathDetectorService.NormalizeGamePath(GamePathTextBox.Text ?? string.Empty);
            if (string.IsNullOrWhiteSpace(gamePath) || !Directory.Exists(gamePath))
            {
                FooterErrorText.Text = "Please select a valid Stardew Valley folder before continuing.";
                return;
            }

            if (_installMode == InstallMode.Custom)
            {
                _currentStep = 3;
                UpdateStepUi();
            }
            else
            {
                await BeginStep4InstallationAsync();
            }
            return;
        }

        if (_currentStep == 3)
        {
            if (_metadata.Mods.Count > 0 && !_metadata.Mods.Any(m => m.IsSelected))
            {
                FooterErrorText.Text = "Please select at least one mod to install.";
                return;
            }

            await BeginStep4InstallationAsync();
            return;
        }

        if (_currentStep == 5)
        {
            Close();
        }
    }

    private async Task BeginStep4InstallationAsync()
    {
        _currentStep = 4;
        UpdateStepUi();

        string gamePath = GamePathDetectorService.NormalizeGamePath(GamePathTextBox.Text ?? string.Empty);
        bool smapiAlreadyInstalled = GamePathDetectorService.IsSmapiInstalled(gamePath);
        bool checkSmapiEnabled = _installMode == InstallMode.Recommended || (CustomInstallSmapiCheck.IsChecked == true);

        if (!smapiAlreadyInstalled && checkSmapiEnabled)
        {
            // Show required disclosure popup asking if user wants SVEPU to download & install SMAPI
            SmapiPromptYesCheck.IsChecked = true;
            SmapiPromptNoCheck.IsChecked = false;
            SmapiPromptBox.IsVisible = true;
            SmapiProgressSection.IsVisible = false;
            ModsProgressSection.IsVisible = false;
            return;
        }

        _smapiFinalSummary = smapiAlreadyInstalled
            ? "• SMAPI Status: Already installed and verified"
            : "• SMAPI Status: Skipped by Custom Install settings";

        await ExecuteInstallationPipelineAsync(downloadAndInstallSmapi: false);
    }

    private async Task ExecuteInstallationPipelineAsync(bool downloadAndInstallSmapi)
    {
        _isInstalling = true;
        BackBtn.IsVisible = false;
        NextOrInstallBtn.IsEnabled = false;
        FooterErrorText.Text = string.Empty;

        string gamePath = GamePathDetectorService.NormalizeGamePath(GamePathTextBox.Text ?? string.Empty);

        try
        {
            if (downloadAndInstallSmapi)
            {
                SmapiProgressSection.IsVisible = true;
                SmapiSuccessBanner.IsVisible = false;
                SmapiProgressBar.Value = 0;

                var smapiProgress = new Progress<(double Percent, string Status)>(report =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        SmapiProgressBar.Value = report.Percent;
                        SmapiProgressStatusText.Text = report.Status;
                    });
                });

                var smapiRes = await _smapiInstaller.DownloadAndInstallLatestSmapiAsync(gamePath, smapiProgress);
                if (smapiRes.Success)
                {
                    SmapiProgressBar.Value = 100;
                    SmapiSuccessBannerText.Text = $"✓ SMAPI {smapiRes.Version} installed successfully!";
                    SmapiSuccessBanner.IsVisible = true;
                    _smapiFinalSummary = $"• SMAPI Status: Downloaded and installed ({smapiRes.Version})";
                }
                else
                {
                    SmapiProgressStatusText.Text = smapiRes.Message;
                    _smapiFinalSummary = $"• SMAPI Status: Not installed ({smapiRes.Message})";
                }
            }
            else if (!GamePathDetectorService.IsSmapiInstalled(gamePath))
            {
                _smapiFinalSummary = "• SMAPI Status: Not installed (User chose manual download)";
            }

            // Extract packaged mods
            ModsProgressSection.IsVisible = true;
            ModsProgressBar.Value = 0;

            ConflictResolutionMode conflictMode = ConflictResolutionMode.OverwriteConflicts;
            if (_installMode == InstallMode.Custom)
            {
                if (ConflictBackupCleanRadio.IsChecked == true)
                {
                    conflictMode = ConflictResolutionMode.BackupAndClean;
                }
                else if (ConflictSkipRadio.IsChecked == true)
                {
                    conflictMode = ConflictResolutionMode.SkipExisting;
                }
            }

            if (string.IsNullOrWhiteSpace(_payloadDir) || !Directory.Exists(_payloadDir))
            {
                FooterErrorText.Text = "Could not find package payload directory (payload/mods.svepu).";
                _isInstalling = false;
                BackBtn.IsVisible = true;
                NextOrInstallBtn.IsEnabled = true;
                return;
            }

            var extractProgress = new Progress<(double Percent, string Status)>(report =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    ModsProgressBar.Value = report.Percent;
                    ModsProgressStatusText.Text = report.Status;
                });
            });

            var extractRes = await _extractor.ExtractModsAsync(
                _payloadDir,
                gamePath,
                _metadata.Mods,
                conflictMode,
                extractProgress);

            if (!extractRes.Success)
            {
                FooterErrorText.Text = extractRes.Message;
                _isInstalling = false;
                BackBtn.IsVisible = true;
                NextOrInstallBtn.IsEnabled = true;
                return;
            }

            // Configure Steam Launch Options if applicable
            bool shouldSetSteamLaunch = _selectedService == GameServiceType.Steam &&
                                        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
                                        (_installMode == InstallMode.Recommended || CustomSetSteamLaunchCheck.IsChecked == true);

            if (shouldSetSteamLaunch)
            {
                bool autoRestartSteam = _installMode == InstallMode.Recommended || (CustomAutoRestartSteamCheck.IsChecked == true);
                var steamProgress = new Progress<string>(status =>
                {
                    Dispatcher.UIThread.Post(() => ModsProgressStatusText.Text = status);
                });

                var steamRes = await _steamLaunchService.ApplySteamLaunchOptionsAsync(gamePath, autoRestartSteam, steamProgress);
                _steamLaunchFinalSummary = $"• Steam Launch Options: {steamRes.Message}";
            }
            else
            {
                _steamLaunchFinalSummary = _selectedService == GameServiceType.Steam
                    ? "• Steam Launch Options: Skipped"
                    : $"• Launcher Service: {_selectedService}";
            }

            // Move to Step 5: Complete
            string modsDir = GamePathDetectorService.GetModsPath(gamePath);
            SummaryModsInstalledText.Text = string.IsNullOrWhiteSpace(extractRes.BackupDir)
                ? $"• Mods Installed: {extractRes.InstalledCount} mod(s) ({extractRes.SkippedCount} skipped)"
                : $"• Mods Installed: {extractRes.InstalledCount} mod(s) (Previous mods backed up to {Path.GetFileName(extractRes.BackupDir)})";
            SummaryTargetFolderText.Text = $"• Mods Folder: {modsDir}";
            SummarySmapiStatusText.Text = _smapiFinalSummary;
            SummarySteamLaunchText.Text = _steamLaunchFinalSummary;

            _isInstalling = false;
            _currentStep = 5;
            UpdateStepUi();
        }
        catch (Exception ex)
        {
            FooterErrorText.Text = $"Installation error: {ex.Message}";
            _isInstalling = false;
            BackBtn.IsVisible = true;
            NextOrInstallBtn.IsEnabled = true;
        }
    }

    private void OnOpenModsFolderClicked(object? sender, RoutedEventArgs e)
    {
        string modsDir = GamePathDetectorService.GetModsPath(GamePathTextBox.Text ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(modsDir) && Directory.Exists(modsDir))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = modsDir,
                    UseShellExecute = true
                });
            }
            catch
            {
                // Ignore shell open error
            }
        }
    }

    private void UpdateStepUi()
    {
        int totalSteps = _installMode == InstallMode.Custom ? 5 : 4;
        int displayStep = _currentStep;
        if (_installMode == InstallMode.Recommended && _currentStep >= 4)
        {
            displayStep = _currentStep - 1;
        }

        StepBadgeText.Text = $"Step {displayStep} of {totalSteps}";

        Step1Panel.IsVisible = _currentStep == 1;
        Step2Panel.IsVisible = _currentStep == 2;
        Step3CustomPanel.IsVisible = _currentStep == 3;
        Step4ProgressPanel.IsVisible = _currentStep == 4;
        Step5CompletePanel.IsVisible = _currentStep == 5;

        BackBtn.IsVisible = _currentStep is 2 or 3;
        NextOrInstallBtn.IsVisible = _currentStep != 4;
        NextOrInstallBtn.IsEnabled = !_isInstalling;

        NextOrInstallBtn.Content = _currentStep switch
        {
            1 => "Next →",
            2 => _installMode == InstallMode.Custom ? "Next: Custom Options →" : "Install Mods →",
            3 => "Install Mods →",
            5 => "Finish",
            _ => "Next →"
        };
    }
}
