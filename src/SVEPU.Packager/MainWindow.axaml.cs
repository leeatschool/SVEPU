using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SVEPU.Core.Models;
using SVEPU.Core.Services;

namespace SVEPU.Packager;

public sealed partial class MainWindow : Window
{
    private readonly ModScannerService _scanner = new();
    private readonly GamePathDetectorService _pathDetector = new();
    private readonly PackageBuilderService _packageBuilder = new();
    private readonly ObservableCollection<ModEntry> _mods = new();

    private string? _defaultIconPath;
    private string? _customIconPath;
    private bool _isPackaging;

    public MainWindow()
    {
        InitializeComponent();

        ModsItemsControl.ItemsSource = _mods;

        _defaultIconPath = AssetLocator.FindAssetFile("app-icon.png");
        LoadDefaultIcon();

        string defaultOutDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "SVEPU_Packages");
        OutputFolderTextBox.Text = defaultOutDir;

        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);

        UpdateModCountsAndWarnings();
    }

    protected override void OnClosed(EventArgs e)
    {
        _scanner.Dispose();
        base.OnClosed(e);
    }

    private void LoadDefaultIcon()
    {
        _customIconPath = null;
        CustomIconNameText.Text = "Default (app-icon.png)";

        if (!string.IsNullOrWhiteSpace(_defaultIconPath) && File.Exists(_defaultIconPath))
        {
            try
            {
                var bmp = new Bitmap(_defaultIconPath);
                AppHeaderIcon.Source = bmp;
                CustomIconPreviewImage.Source = bmp;
                Icon = new WindowIcon(bmp);
            }
            catch
            {
                // Ignore icon load errors
            }
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (e.Data.Contains(DataFormats.Files))
        {
            e.DragEffects = DragDropEffects.Copy;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        var files = e.Data.GetFiles();
        if (files == null)
        {
            return;
        }

        int addedCount = 0;
        foreach (var item in files)
        {
            var localPath = item.TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(localPath))
            {
                addedCount += ScanAndAddPath(localPath);
            }
        }

        PackagingStatusText.Text = addedCount > 0
            ? $"Added {addedCount} mod(s) via drag & drop."
            : "No new SMAPI mods (manifest.json) found in the dropped item(s).";
    }

    private void OnAutoDetectModsClicked(object? sender, RoutedEventArgs e)
    {
        var detectedModsDir = _pathDetector.DetectAnyInstalledModsFolder();
        if (string.IsNullOrWhiteSpace(detectedModsDir) || !Directory.Exists(detectedModsDir))
        {
            PackagingStatusText.Text = "Could not auto-detect a local Stardew Valley Mods folder. Use '+ Add Mod Folder(s)...' to browse manually.";
            return;
        }

        int added = ScanAndAddPath(detectedModsDir);
        PackagingStatusText.Text = added > 0
            ? $"Auto-detected {added} mod(s) from {detectedModsDir}"
            : $"Found Mods folder at {detectedModsDir}, but no new mods were added.";
    }

    private async void OnAddModFolderClicked(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select a Mod Folder or a Stardew Valley 'Mods' Directory",
            AllowMultiple = true
        });

        if (folders.Count == 0)
        {
            return;
        }

        int added = 0;
        foreach (var folder in folders)
        {
            var path = folder.TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(path))
            {
                added += ScanAndAddPath(path);
            }
        }

        PackagingStatusText.Text = added > 0
            ? $"Added {added} mod(s) from selected folder(s)."
            : "No new SMAPI mods (manifest.json) were found in the selected folder(s).";
    }

    private async void OnAddModZipClicked(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Mod .zip Archive(s)",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Zip Archives") { Patterns = new[] { "*.zip" } }
            }
        });

        if (files.Count == 0)
        {
            return;
        }

        int added = 0;
        foreach (var file in files)
        {
            var path = file.TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(path))
            {
                added += ScanAndAddPath(path);
            }
        }

        PackagingStatusText.Text = added > 0
            ? $"Extracted and added {added} mod(s) from .zip archive(s)."
            : "No new SMAPI mods (manifest.json) were found inside the selected .zip archive(s).";
    }

    private int ScanAndAddPath(string path)
    {
        var scanned = _scanner.ScanPath(path);
        int added = 0;

        foreach (var entry in scanned)
        {
            bool alreadyExists = _mods.Any(m =>
                string.Equals(m.UniqueID, entry.UniqueID, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(entry.UniqueID));

            if (alreadyExists)
            {
                continue;
            }

            entry.PropertyChanged += OnModEntryPropertyChanged;
            _mods.Add(entry);
            added++;
        }

        UpdateModCountsAndWarnings();
        return added;
    }

    private void OnModEntryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ModEntry.IsSelected))
        {
            UpdateModCountsAndWarnings();
        }
    }

    private void OnRemoveModItemClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ModEntry entry })
        {
            entry.PropertyChanged -= OnModEntryPropertyChanged;
            _mods.Remove(entry);
            UpdateModCountsAndWarnings();
        }
    }

    private void OnClearModsClicked(object? sender, RoutedEventArgs e)
    {
        foreach (var mod in _mods)
        {
            mod.PropertyChanged -= OnModEntryPropertyChanged;
        }
        _mods.Clear();
        UpdateModCountsAndWarnings();
        PackagingStatusText.Text = "Cleared mod list.";
    }

    private void OnSelectAllClicked(object? sender, RoutedEventArgs e)
    {
        foreach (var mod in _mods)
        {
            mod.IsSelected = true;
        }
        UpdateModCountsAndWarnings();
    }

    private void OnDeselectAllClicked(object? sender, RoutedEventArgs e)
    {
        foreach (var mod in _mods)
        {
            mod.IsSelected = false;
        }
        UpdateModCountsAndWarnings();
    }

    private void UpdateModCountsAndWarnings()
    {
        ModScannerService.UpdateDependencyWarnings(_mods);
        int selected = _mods.Count(m => m.IsSelected);
        int total = _mods.Count;
        int warnings = _mods.Count(m => m.IsSelected && m.HasWarning);

        ModsCountHeaderText.Text = warnings > 0
            ? $"Detected Mods ({selected} selected of {total} • {warnings} dependency warning(s))"
            : $"Detected Mods ({selected} selected of {total})";

        EmptyModsPlaceholder.IsVisible = total == 0;
    }

    private async void OnChooseCustomIconClicked(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Custom Package Icon Image",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Image Files") { Patterns = new[] { "*.png", "*.ico", "*.jpg", "*.jpeg" } }
            }
        });

        if (files.Count > 0)
        {
            var path = files[0].TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                try
                {
                    CustomIconPreviewImage.Source = new Bitmap(path);
                    _customIconPath = path;
                    CustomIconNameText.Text = Path.GetFileName(path);
                }
                catch (Exception ex)
                {
                    PackagingStatusText.Text = $"Could not load image: {ex.Message}";
                }
            }
        }
    }

    private void OnResetCustomIconClicked(object? sender, RoutedEventArgs e)
    {
        LoadDefaultIcon();
    }

    private async void OnBrowseOutputFolderClicked(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Output Folder for Generated Package",
            AllowMultiple = false
        });

        if (folders.Count > 0)
        {
            var path = folders[0].TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(path))
            {
                OutputFolderTextBox.Text = path;
            }
        }
    }

    private void OnOpenOutputFolderClicked(object? sender, RoutedEventArgs e)
    {
        var outDir = OutputFolderTextBox.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(outDir))
        {
            Directory.CreateDirectory(outDir);
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = outDir,
                    UseShellExecute = true
                });
            }
            catch
            {
                // Ignore shell launch error
            }
        }
    }

    private async void OnPackageModsClicked(object? sender, RoutedEventArgs e)
    {
        if (_isPackaging)
        {
            return;
        }

        var selectedMods = _mods.Where(m => m.IsSelected).ToList();
        if (selectedMods.Count == 0)
        {
            PackagingStatusText.Text = "Please add and select at least one mod before packaging.";
            return;
        }

        string outputDir = OutputFolderTextBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(outputDir))
        {
            PackagingStatusText.Text = "Please choose an Output Folder first.";
            return;
        }

        _isPackaging = true;
        PackageModsBtn.IsEnabled = false;
        PackagingProgressBar.Value = 0;

        var metadata = new PackageMetadata
        {
            PackName = string.IsNullOrWhiteSpace(PackNameTextBox.Text) ? "Stardew Valley Modpack" : PackNameTextBox.Text.Trim(),
            Author = string.IsNullOrWhiteSpace(AuthorTextBox.Text) ? "Unknown" : AuthorTextBox.Text.Trim(),
            Version = string.IsNullOrWhiteSpace(VersionTextBox.Text) ? "1.0.0" : VersionTextBox.Text.Trim(),
            Description = DescriptionTextBox.Text?.Trim() ?? string.Empty,
            IncludeConfigs = IncludeConfigJsonCheck.IsChecked == true
        };

        string? iconToEmbed = !string.IsNullOrWhiteSpace(_customIconPath) && File.Exists(_customIconPath)
            ? _customIconPath
            : _defaultIconPath;

        var progress = new Progress<(double Percent, string Status)>(report =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                PackagingProgressBar.Value = report.Percent;
                PackagingStatusText.Text = report.Status;
            });
        });

        try
        {
            var res = await _packageBuilder.BuildPackageAsync(
                metadata,
                selectedMods,
                outputDir,
                customIconPath: iconToEmbed,
                keepUnzippedFolder: KeepUnzippedFolderCheck.IsChecked == true,
                progress: progress);

            PackagingStatusText.Text = res.Message;
            if (res.Success)
            {
                PackagingProgressBar.Value = 100;
            }
        }
        finally
        {
            _isPackaging = false;
            PackageModsBtn.IsEnabled = true;
        }
    }
}
