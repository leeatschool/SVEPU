namespace SVEPU.Core.Models;

public enum GameServiceType
{
    Steam,
    GogGalaxy,
    XboxApp,
    Custom
}

public enum ConflictResolutionMode
{
    OverwriteConflicts,
    BackupAndClean,
    SkipExisting
}

public enum InstallMode
{
    Recommended,
    Custom
}
