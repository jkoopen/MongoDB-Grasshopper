using MongoDB.Bson;

namespace GenericMongoPlugin.Types;

public sealed class MongoFilterEntry
{
    public MongoFilterEntry(string key, BsonValue value)
    {
        Key = key;
        Value = value;
    }

    public string Key { get; }

    public BsonValue Value { get; }

    public override string ToString() => $"{Key}: {Value}";
}
