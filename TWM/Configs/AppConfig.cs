using System.Text.RegularExpressions;

namespace TilingWindowManager.Configs;

public enum AppExtraFlag { WmForce, WmUnmanage }

/// <summary>
/// Defines a single override rule for the window manager.
/// Rather than using complex JSON serialization or inheritance trees,
/// we use standard C# Func delegates to evaluate windows procedurally.
/// </summary>
public class AppConfig
{
    public required string Id;
    public required Func<string, string, string, string, bool> Match;
    public required AppExtraFlag Flag;
}

/// <summary>
/// Holds the hardcoded rules for applications that should NOT be tiled.
/// </summary>
public class ConfigLoader
{
    private readonly List<AppConfig> _rules = new();

    public ConfigLoader()
    {
        // System background apps (match by path OR className fallback for access-denied processes)
        var systemAppClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Windows.UI.Core.CoreWindow",     // UWP system overlays (e.g. Start Menu internals)
            "ImmersiveLauncher",              // Windows Start Menu
            "Shell_SecondaryTrayWnd",         // Secondary taskbar
            "Shell_TrayWnd",                  // Primary taskbar
            "Windows.UI.Input.InputSite.WindowClass"  // Touch keyboard / input panels
        };

        Unmanage("SystemApps", (_, className, _, path) =>
            path.Contains(@"Windows\SystemApps", StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrEmpty(className) && systemAppClasses.Contains(className))
        );

        // Universal Installer Heuristic
        // Match standard installer executable names or known installer window classes
        var installerExeRegex = new Regex(@"(setup|install|update|uninst)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        var installerClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase) 
        { 
            "TWizardForm",         // Inno Setup
            "NullsoftInst",        // NSIS
            "MsiDialogCloseClass"  // Windows Installer
        };

        Unmanage("Installers", (_, className, exeName, _) => 
            (!string.IsNullOrEmpty(exeName) && installerExeRegex.IsMatch(exeName)) || 
            (!string.IsNullOrEmpty(className) && installerClasses.Contains(className))
        );
    }

    private void Unmanage(string id, Func<string, string, string, string, bool> match) =>
        _rules.Add(new AppConfig { Id = id, Match = match, Flag = AppExtraFlag.WmUnmanage });

    public AppConfig? FindMatch(string title, string className, string exe, string path) =>
        _rules.FirstOrDefault(r => r.Match(title, className, exe, path));
}
