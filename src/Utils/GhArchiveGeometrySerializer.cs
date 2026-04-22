using System;
using Grasshopper.Kernel.Types;
using GH_IO.Serialization;

namespace GenericMongoPlugin.Utils;

public static class GhArchiveGeometrySerializer
{
    private const string ChunkName = "geom";

    public static byte[] Serialize(IGH_GeometricGoo geometry)
    {
        var archive = new GH_Archive();

        // Store in Grasshopper format.
        archive.AppendObject(geometry, ChunkName);

        return archive.Serialize_Binary();
    }

    public static string GetGeometryTypeName(IGH_GeometricGoo geometry)
        => geometry.GetType().AssemblyQualifiedName
           ?? geometry.GetType().FullName
           ?? geometry.GetType().Name;

    public static IGH_GeometricGoo Deserialize(byte[] data, string geometryTypeName)
    {
        var archive = new GH_Archive();
        if (!archive.Deserialize_Binary(data))
            throw new InvalidOperationException("Failed to deserialize GH_Archive binary.");

        var type = Type.GetType(geometryTypeName, throwOnError: false);
        if (type == null)
            throw new InvalidOperationException("Unknown geometry type. Inserted data was created by a different Grasshopper/Rhino installation.");

        if (!typeof(IGH_GeometricGoo).IsAssignableFrom(type))
            throw new InvalidOperationException("Stored type is not an IGH_GeometricGoo.");

        var instance = (IGH_GeometricGoo?)Activator.CreateInstance(type);
        if (instance == null)
            throw new InvalidOperationException("Could not create geometry instance.");

        if (!archive.ExtractObject(instance, ChunkName))
            throw new InvalidOperationException("GH_Archive did not contain geometry.");

        return instance;
    }
}
