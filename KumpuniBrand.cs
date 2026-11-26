using System.Reflection;

namespace Kumpuni;

internal static class KumpuniBrand
{
    public static Icon LoadIcon()
    {
        using Stream icon = Open("Kumpuni.Brand.Icon.ico");
        return new Icon(icon);
    }

    public static Bitmap LoadLogo() => LoadBitmap("Kumpuni.Brand.Logo.png");

    public static Bitmap LoadPrinterPhoto() => LoadBitmap("Kumpuni.Brand.Printer.jpg");

    private static Bitmap LoadBitmap(string name)
    {
        using Stream source = Open(name);
        using var decoded = new Bitmap(source);
        return new Bitmap(decoded);
    }

    private static Stream Open(string name) =>
        Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
        ?? throw new InvalidOperationException($"Missing Kumpuni branding resource: {name}");
}
