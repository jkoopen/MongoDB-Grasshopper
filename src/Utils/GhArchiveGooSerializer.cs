using System;
using GH_IO.Serialization;
using Grasshopper.Kernel.Types;

namespace GenericMongoPlugin.Utils;

public static class GhArchiveGooSerializer
{
    private const string ChunkName = "data";

    public static byte[] Serialize(IGH_Goo goo)
    {
        var archive = new GH_Archive();
        archive.AppendObject(goo, ChunkName);
        return archive.Serialize_Binary();
    }

    public static string GetGooTypeName(IGH_Goo goo)
        => goo.GetType().AssemblyQualifiedName
           ?? goo.GetType().FullName
           ?? goo.GetType().Name;

    public static IGH_Goo Deserialize(byte[] data, string gooTypeName)
    {
        var archive = new GH_Archive();
        if (!archive.Deserialize_Binary(data))
            throw new InvalidOperationException("Failed to deserialize GH_Archive binary.");

        var type = Type.GetType(gooTypeName, throwOnError: false);
        if (type == null)
            throw new InvalidOperationException("Unknown data type. Inserted data was created by a different Grasshopper/Rhino installation.");

        if (!typeof(IGH_Goo).IsAssignableFrom(type))
            throw new InvalidOperationException("Stored type is not an IGH_Goo.");

        var instance = (IGH_Goo?)Activator.CreateInstance(type);
        if (instance == null)
            throw new InvalidOperationException("Could not create data instance.");

        if (!archive.ExtractObject(instance, ChunkName))
            throw new InvalidOperationException("GH_Archive did not contain data.");

        return instance;
    }
}
