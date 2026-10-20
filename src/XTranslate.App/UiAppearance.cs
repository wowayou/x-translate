using System.Windows;

namespace XTranslate.App;

/// <summary>Persisted appearance preference, independent of interface and translation languages.</summary>
public enum UiTheme { System, Light, Dark }

/// <summary>
/// Uses the resource-dictionary entry points shipped with .NET 10 WPF, not the experimental
/// ThemeMode code API. WPF handles Windows theme/accent/high-contrast changes and the native
/// title bar/backdrop. No registry writes, polling, external assets or extra UI packages.
/// </summary>
internal static class UiAppearance
{
    private const string ThemeRoot = "pack://application:,,,/PresentationFramework.Fluent;component/Themes/";
    public static UiTheme Preference { get; private set; } = UiTheme.System;
    // WPF's Mica implementation requires 22H2, not merely the first Windows 11 build.
    public static bool SupportsBackdrop => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621);

    public static void Apply(UiTheme preference)
    {
        var application = Application.Current;
        application.Dispatcher.VerifyAccess();
        if (!Enum.IsDefined(preference)) preference = UiTheme.System;
        var source = new Uri(ThemeRoot + (preference switch
        {
            UiTheme.Light => "Fluent.Light.xaml",
            UiTheme.Dark => "Fluent.Dark.xaml",
            _ => "Fluent.xaml"
        }), UriKind.Absolute);
        var dictionaries = application.Resources.MergedDictionaries;
        var index = -1;
        for (var i = 0; i < dictionaries.Count; i++)
            if (dictionaries[i].Source?.AbsoluteUri.StartsWith(ThemeRoot, StringComparison.OrdinalIgnoreCase) == true)
            { index = i; break; }
        // Repeated settings saves must not churn dictionaries or reset controls/focus.
        if (Preference == preference && index >= 0 &&
            (dictionaries[index].Source == source || SystemParameters.HighContrast)) return;

        var theme = new ResourceDictionary { Source = source };
        Preference = preference;
        if (index >= 0) dictionaries[index] = theme;
        else
        {
            dictionaries.Insert(0, theme);
            // Resolve the app's styles only after the selected theme is available. This avoids
            // loading a full System theme first and then another full Light/Dark theme at startup.
            dictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/XTranslate;component/UiStyles.xaml", UriKind.Absolute)
            });
        }
    }
}
