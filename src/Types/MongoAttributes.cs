using MongoDB.Bson;

namespace GenericMongoPlugin.Types;

public sealed class MongoAttributes
{
    public MongoAttributes(BsonDocument document)
    {
        Document = document;
    }

    public MongoAttributes() : this(new BsonDocument())
    {
    }

    public BsonDocument Document { get; }

    public MongoAttributes Clone() => new(Document.DeepClone().AsBsonDocument);

    public void SetText(string key, string value) => Document[key] = value;

    public void SetNumber(string key, double value) => Document[key] = value;

    public void SetBoolean(string key, bool value) => Document[key] = value;

    public static bool IsValidKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        // MongoDB field names cannot contain '.' and cannot start with '$'
        if (key.Contains('.')) return false;
        if (key.StartsWith('$')) return false;
        return true;
    }
}
