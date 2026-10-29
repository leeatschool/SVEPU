using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace SVEPU.Installer;

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
            string? explicitPackageRoot = null;
            var args = Program.Args;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], "--package-root", StringComparison.OrdinalIgnoreCase))
                {
                    explicitPackageRoot = args[i + 1];
                    break;
                }
            }

            desktop.MainWindow = new MainWindow(explicitPackageRoot);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
