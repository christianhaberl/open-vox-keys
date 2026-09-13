using System.Reflection;

static class AppBrand
{
    internal static readonly Icon Icon = Load();
    static Icon Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("OpenVoxKeys.AppIcon")
         ?? throw new InvalidOperationException("Application icon resource missing");
        using var source = new Icon(stream, 32, 32);
        return (Icon)source.Clone();
    }
}
