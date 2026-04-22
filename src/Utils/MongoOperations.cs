using System;
using System.Collections.Generic;
using Grasshopper.Kernel.Types;
using GenericMongoPlugin.Types;
using MongoDB.Bson;
using MongoDB.Driver;
using Rhino.Geometry;

namespace GenericMongoPlugin.Utils;

public static class MongoOperations
{
    private static BsonDocument BuildQueryFilter(IEnumerable<MongoFilterGoo> filters)
    {
        var and = new BsonArray();

        foreach (var f in filters)
        {
            var d = f?.Value?.Document;
            if (d == null) continue;
            if (d.ElementCount == 0) continue;
            and.Add(d);
        }

        if (and.Count == 0)
            return new BsonDocument();

        if (and.Count == 1)
            return and[0].AsBsonDocument;

        return new BsonDocument("$and", and);
    }

    public static BsonDocument MergeAttributes(IEnumerable<MongoAttributesGoo> attrsList)
    {
        var attrsDoc = new BsonDocument();

        foreach (var a in attrsList)
        {
            if (a?.Value?.Document == null) continue;
            foreach (var el in a.Value.Document)
                attrsDoc[el.Name] = el.Value;
        }

        return attrsDoc;
    }

    public static (string log, string id) StoreGeometry(
        MongoDbConnection conn,
        string collectionName,
        IGH_GeometricGoo geometry,
        Plane anchorPlane,
        IEnumerable<MongoAttributesGoo> attrsList)
    {
        var db = conn.CreateDatabase();
        var col = db.GetCollection<BsonDocument>(collectionName);

        var bytes = GhArchiveGeometrySerializer.Serialize(geometry);

        var bb = geometry.Boundingbox;
        var bboxDoc = new BsonDocument
        {
            { "min", new BsonArray { bb.Min.X, bb.Min.Y, bb.Min.Z } },
            { "max", new BsonArray { bb.Max.X, bb.Max.Y, bb.Max.Z } }
        };

        var geomType = GhArchiveGeometrySerializer.GetGeometryTypeName(geometry);
        var anchorDoc = PlaneBsonConverter.ToBson(anchorPlane);

        var doc = new BsonDocument
        {
            { "type", "geometry" },
            { "geom", new BsonBinaryData(bytes) },
            { "geomType", geomType },
            { "anchor", anchorDoc },
            { "bbox", bboxDoc },
            { "attrs", MergeAttributes(attrsList) },
            { "createdAt", DateTime.UtcNow }
        };

        col.InsertOne(doc);
        return ("Success: Inserted geometry.", doc.GetValue("_id", BsonNull.Value).ToString() ?? string.Empty);
    }

    public static (string log, string id) StoreData(
        MongoDbConnection conn,
        string collectionName,
        IGH_Goo dataGoo,
        IEnumerable<MongoAttributesGoo> attrsList)
    {
        var db = conn.CreateDatabase();
        var col = db.GetCollection<BsonDocument>(collectionName);

        var bytes = GhArchiveGooSerializer.Serialize(dataGoo);
        var dataType = GhArchiveGooSerializer.GetGooTypeName(dataGoo);

        var doc = new BsonDocument
        {
            { "type", "data" },
            { "data", new BsonBinaryData(bytes) },
            { "dataType", dataType },
            { "attrs", MergeAttributes(attrsList) },
            { "createdAt", DateTime.UtcNow }
        };

        col.InsertOne(doc);
        return ("Success: Inserted data.", doc.GetValue("_id", BsonNull.Value).ToString() ?? string.Empty);
    }

    public static BsonDocument BuildTypedQueryFilter(string type, string requiredField, IEnumerable<MongoFilterGoo> filters)
    {
        // Prefer explicit type filtering, but keep backward compatibility with older docs.
        var baseDoc = new BsonDocument("$or", new BsonArray
        {
            new BsonDocument("type", type),
            new BsonDocument
            {
                { "type", new BsonDocument("$exists", false) },
                { requiredField, new BsonDocument("$exists", true) }
            }
        });

        var and = new BsonArray { baseDoc };

        foreach (var f in filters)
        {
            var d = f?.Value?.Document;
            if (d == null) continue;
            if (d.ElementCount == 0) continue;
            and.Add(d);
        }

        if (and.Count == 1)
            return baseDoc;

        return new BsonDocument("$and", and);
    }

    public static (string log, List<IGH_GeometricGoo> geometries, List<Plane> planes, List<MongoAttributesGoo> attributes) QueryGeometry(
        MongoDbConnection conn,
        string collectionName,
        IEnumerable<MongoFilterGoo> filters,
        int limit)
    {
        var db = conn.CreateDatabase();
        var col = db.GetCollection<BsonDocument>(collectionName);

        var filterDoc = BuildTypedQueryFilter("geometry", "geom", filters);
        var filter = new BsonDocumentFilterDefinition<BsonDocument>(filterDoc);

        var docs = col.Find(filter).Limit(limit).ToList();

        var geometries = new List<IGH_GeometricGoo>(docs.Count);
        var planes = new List<Plane>(docs.Count);
        var attributes = new List<MongoAttributesGoo>(docs.Count);

        var skipped = 0;
        foreach (var d in docs)
        {
            if (!d.TryGetValue("geom", out var geomVal) || geomVal.IsBsonNull) continue;

            if (!d.TryGetValue("geomType", out var typeVal) || !typeVal.IsString)
            {
                skipped++;
                continue;
            }

            try
            {
                var bytes = geomVal.AsBsonBinaryData.Bytes;
                var geo = GhArchiveGeometrySerializer.Deserialize(bytes, typeVal.AsString);
                geometries.Add(geo);
            }
            catch
            {
                skipped++;
                continue;
            }

            // Anchor plane is optional to keep backward compatibility with older documents.
            var plane = Plane.WorldXY;
            if (d.TryGetValue("anchor", out var anchorVal))
            {
                if (PlaneBsonConverter.TryFromBson(anchorVal, out var parsed))
                    plane = parsed;
            }
            planes.Add(plane);

            if (d.TryGetValue("attrs", out var attrsVal) && attrsVal.IsBsonDocument)
            {
                var doc = attrsVal.AsBsonDocument.DeepClone().AsBsonDocument;
                attributes.Add(new MongoAttributesGoo(new MongoAttributes(doc)));
            }
            else
            {
                attributes.Add(new MongoAttributesGoo(new MongoAttributes()));
            }
        }

        var suffix = skipped > 0 ? $" (skipped {skipped})" : string.Empty;
        return ($"Success: Returned {geometries.Count} geometry item(s).{suffix}", geometries, planes, attributes);
    }

    public static (string log, List<IGH_Goo> data, List<MongoAttributesGoo> attributes) QueryData(
        MongoDbConnection conn,
        string collectionName,
        IEnumerable<MongoFilterGoo> filters,
        int limit)
    {
        var db = conn.CreateDatabase();
        var col = db.GetCollection<BsonDocument>(collectionName);

        var filterDoc = BuildTypedQueryFilter("data", "data", filters);
        var filter = new BsonDocumentFilterDefinition<BsonDocument>(filterDoc);

        var docs = col.Find(filter).Limit(limit).ToList();

        var data = new List<IGH_Goo>(docs.Count);
        var attributes = new List<MongoAttributesGoo>(docs.Count);

        var skipped = 0;
        foreach (var d in docs)
        {
            if (!d.TryGetValue("data", out var dataVal) || dataVal.IsBsonNull) continue;

            if (!d.TryGetValue("dataType", out var typeVal) || !typeVal.IsString)
            {
                skipped++;
                continue;
            }

            try
            {
                var bytes = dataVal.AsBsonBinaryData.Bytes;
                var goo = GhArchiveGooSerializer.Deserialize(bytes, typeVal.AsString);
                data.Add(goo);
            }
            catch
            {
                skipped++;
                continue;
            }

            if (d.TryGetValue("attrs", out var attrsVal) && attrsVal.IsBsonDocument)
            {
                var doc = attrsVal.AsBsonDocument.DeepClone().AsBsonDocument;
                attributes.Add(new MongoAttributesGoo(new MongoAttributes(doc)));
            }
            else
            {
                attributes.Add(new MongoAttributesGoo(new MongoAttributes()));
            }
        }

        var suffix = skipped > 0 ? $" (skipped {skipped})" : string.Empty;
        return ($"Success: Returned {data.Count} item(s).{suffix}", data, attributes);
    }

    public static (string log, List<IGH_Goo> data, List<MongoAttributesGoo> attributes) QueryAny(
        MongoDbConnection conn,
        string collectionName,
        IEnumerable<MongoFilterGoo> filters,
        int limit)
    {
        var filterDoc = BuildQueryFilter(filters);
        return QueryAny(conn, collectionName, filterDoc, limit);
    }

    public static (string log, List<IGH_Goo> data, List<MongoAttributesGoo> attributes) QueryAny(
        MongoDbConnection conn,
        string collectionName,
        BsonDocument filterDoc,
        int limit)
    {
        var db = conn.CreateDatabase();
        var col = db.GetCollection<BsonDocument>(collectionName);

        filterDoc ??= new BsonDocument();
        var filter = new BsonDocumentFilterDefinition<BsonDocument>(filterDoc);

        var docs = col.Find(filter).Limit(limit).ToList();

        var data = new List<IGH_Goo>(docs.Count);
        var attributes = new List<MongoAttributesGoo>(docs.Count);

        var skipped = 0;
        foreach (var d in docs)
        {
            // Prefer decoding geometry docs when present.
            if (d.TryGetValue("geom", out var geomVal) && !geomVal.IsBsonNull)
            {
                if (!d.TryGetValue("geomType", out var geomTypeVal) || !geomTypeVal.IsString)
                {
                    skipped++;
                    continue;
                }

                try
                {
                    var bytes = geomVal.AsBsonBinaryData.Bytes;
                    var geo = GhArchiveGeometrySerializer.Deserialize(bytes, geomTypeVal.AsString);
                    data.Add(geo);
                }
                catch
                {
                    skipped++;
                    continue;
                }

                if (d.TryGetValue("attrs", out var attrsVal) && attrsVal.IsBsonDocument)
                {
                    var doc = attrsVal.AsBsonDocument.DeepClone().AsBsonDocument;
                    attributes.Add(new MongoAttributesGoo(new MongoAttributes(doc)));
                }
                else
                {
                    attributes.Add(new MongoAttributesGoo(new MongoAttributes()));
                }

                continue;
            }

            // Otherwise attempt generic data decoding.
            if (d.TryGetValue("data", out var dataVal) && !dataVal.IsBsonNull)
            {
                if (!d.TryGetValue("dataType", out var dataTypeVal) || !dataTypeVal.IsString)
                {
                    skipped++;
                    continue;
                }

                try
                {
                    var bytes = dataVal.AsBsonBinaryData.Bytes;
                    var goo = GhArchiveGooSerializer.Deserialize(bytes, dataTypeVal.AsString);
                    data.Add(goo);
                }
                catch
                {
                    skipped++;
                    continue;
                }

                if (d.TryGetValue("attrs", out var attrsVal) && attrsVal.IsBsonDocument)
                {
                    var doc = attrsVal.AsBsonDocument.DeepClone().AsBsonDocument;
                    attributes.Add(new MongoAttributesGoo(new MongoAttributes(doc)));
                }
                else
                {
                    attributes.Add(new MongoAttributesGoo(new MongoAttributes()));
                }

                continue;
            }

            skipped++;
        }

        var suffix = skipped > 0 ? $" (skipped {skipped})" : string.Empty;
        return ($"Success: Returned {data.Count} item(s).{suffix}", data, attributes);
    }
}
