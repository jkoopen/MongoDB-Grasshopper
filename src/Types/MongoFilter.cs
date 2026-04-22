using MongoDB.Bson;

namespace GenericMongoPlugin.Types;

public sealed class MongoFilter
{
    public MongoFilter(BsonDocument document)
    {
        Document = document;
    }

    public BsonDocument Document { get; }

    public MongoFilter Clone() => new(Document.DeepClone().AsBsonDocument);

    public override string ToString() => Document.ToJson();

    public static MongoFilter Empty() => new(new BsonDocument());
}
