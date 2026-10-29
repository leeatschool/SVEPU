using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace SVEPU.Setup;

public sealed partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            bool isUninstall = false;
            string? explicitInstallDir = null;
            var args = Program.Args;

            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--uninstall", StringComparison.OrdinalIgnoreCase))
                {
                    isUninstall = true;
                }
                else if (string.Equals(args[i], "--install-dir", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    explicitInstallDir = args[i + 1];
                }
            }

            desktop.MainWindow = new MainWindow(isUninstall, explicitInstallDir);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
