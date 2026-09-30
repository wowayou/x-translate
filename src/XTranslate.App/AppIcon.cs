using System;
using System.Drawing;
using System.IO;
using System.Reflection;

namespace XTranslate.App;

internal static class AppIcon
{
    private const string ResourceName = "XTranslate.App.Assets.xtranslate.ico";

    /// <summary>
    /// Creates an independent 16px tray icon from the embedded multi-size ICO.
    /// The caller owns and must dispose the returned icon.
    /// </summary>
    internal static Icon CreateTrayIcon()
    {
        Assembly assembly = typeof(AppIcon).Assembly;
        using Stream source = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded icon resource '{ResourceName}' was not found.");
        using MemoryStream bytes = new();
        source.CopyTo(bytes);
        bytes.Position = 0;
        using Icon selected = new(bytes, 16, 16);
        return (Icon)selected.Clone();
    }
}
