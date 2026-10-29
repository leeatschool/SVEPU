using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SVEPU.Core.Models;

public sealed class PackageMetadata
{
    public string PackName { get; set; } = "Stardew Valley Modpack";
    public string Author { get; set; } = "Unknown";
    public string Version { get; set; } = "1.0.0";
    public string Description { get; set; } = string.Empty;
    public bool IncludeConfigs { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string? CustomIconFileName { get; set; }
    public List<PackagedModInfo> Mods { get; set; } = new();
}

public sealed class PackagedModInfo : INotifyPropertyChanged
{
    private bool _isSelected = true;

    public string Name { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Version { get; set; } = "1.0.0";
    public string UniqueID { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string FolderName { get; set; } = string.Empty;
    public bool HasConfigJson { get; set; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
