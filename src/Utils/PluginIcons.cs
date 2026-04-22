using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;

namespace GenericMongoPlugin.Utils;

public static class PluginIcons
{
#pragma warning disable CA1416 // Grasshopper/Rhino provides System.Drawing on supported platforms.
    private static readonly ConcurrentDictionary<string, Bitmap> Cache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Lazy<string[]> ResourceNames = new(() =>
        Assembly.GetExecutingAssembly().GetManifestResourceNames(),
        isThreadSafe: true);

    private static readonly Lazy<Bitmap> Fallback = new(() => new Bitmap(24, 24), isThreadSafe: true);

    public static Bitmap Get(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return Fallback.Value;

        return Cache.GetOrAdd(fileName.Trim(), Load);
    }

    private static Bitmap Load(string fileName)
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var names = ResourceNames.Value;

            // Typical: <RootNamespace>.Icons.<fileName>
            var wanted1 = ".Icons." + fileName;
            var wanted2 = "." + fileName;

            var resourceName = names.FirstOrDefault(n =>
                n.EndsWith(wanted1, StringComparison.OrdinalIgnoreCase) ||
                n.EndsWith(wanted2, StringComparison.OrdinalIgnoreCase));

            if (string.IsNullOrWhiteSpace(resourceName))
                return Fallback.Value;

            using var stream = asm.GetManifestResourceStream(resourceName);
            if (stream == null)
                return Fallback.Value;

            using var tmp = new Bitmap(stream);
            return new Bitmap(tmp);
        }
        catch
        {
            return Fallback.Value;
        }
    }
#pragma warning restore CA1416
}
