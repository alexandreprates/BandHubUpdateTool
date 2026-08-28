using System;
using System.IO;
using System.Linq;
using Gtk;

namespace BandHub.FirmwareUpdate;

internal static class Program
{
    private const string ApplicationIconResource = "BandHub.FirmwareUpdate.icon.png";

    [STAThread]
    private static void Main()
    {
        ConfigureBundledGtkRuntime();
        Application.Init();
        using var icon = new Gdk.Pixbuf(typeof(Program).Assembly, ApplicationIconResource);
        using var window = new MainWindow { Icon = icon };
        window.ShowAll();
        Application.Run();
    }

    private static void ConfigureBundledGtkRuntime()
    {
        if (Environment.OSVersion.Platform != PlatformID.Win32NT)
        {
            return;
        }

        var applicationDirectory = AppDomain.CurrentDomain.BaseDirectory;
        var schemaDirectory = Path.Combine(
            applicationDirectory, "share", "glib-2.0", "schemas");
        if (Directory.Exists(schemaDirectory))
        {
            Environment.SetEnvironmentVariable("GSETTINGS_SCHEMA_DIR", schemaDirectory);
        }

        var pixbufDirectory = Path.Combine(
            applicationDirectory, "lib", "gdk-pixbuf-2.0");
        var loaderCache = Directory.Exists(pixbufDirectory)
            ? Directory.GetFiles(
                pixbufDirectory, "loaders.cache", SearchOption.AllDirectories)
                .SingleOrDefault()
            : null;
        if (loaderCache != null)
        {
            Environment.SetEnvironmentVariable("GDK_PIXBUF_MODULE_FILE", loaderCache);
        }

        // The generated loader cache uses paths relative to the installed application.
        Directory.SetCurrentDirectory(applicationDirectory);
    }
}
