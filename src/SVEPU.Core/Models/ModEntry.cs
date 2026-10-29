using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SVEPU.Core.Models;

public sealed class ModEntry : INotifyPropertyChanged
{
    private bool _isSelected = true;
    private string _dependencyWarning = string.Empty;

    public string Name { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Version { get; set; } = "1.0.0";
    public string UniqueID { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public string FolderName { get; set; } = string.Empty;
    public bool HasConfigJson { get; set; }
    public bool IsFromArchive { get; set; }
    public string? OriginalArchivePath { get; set; }
    public List<string> RequiredDependencies { get; set; } = new();

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged();
            }
        }
    }

    public string DependencyWarning
    {
        get => _dependencyWarning;
        set
        {
            if (_dependencyWarning != value)
            {
                _dependencyWarning = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasWarning));
            }
        }
    }

    public bool HasWarning => !string.IsNullOrWhiteSpace(_dependencyWarning);

    public string SourceTypeDisplay => IsFromArchive ? "Archive (.zip)" : "Folder";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
