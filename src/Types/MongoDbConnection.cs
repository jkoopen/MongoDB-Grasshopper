using MongoDB.Driver;

namespace GenericMongoPlugin.Types;

public class MongoDbConnection
{
    public string ConnectionString { get; set; } = string.Empty;
    public string DatabaseName { get; set; } = string.Empty;

    public bool IsValid => !string.IsNullOrWhiteSpace(ConnectionString) && !string.IsNullOrWhiteSpace(DatabaseName);

    public IMongoDatabase CreateDatabase()
    {
        var client = new MongoClient(ConnectionString);
        return client.GetDatabase(DatabaseName);
    }
}
