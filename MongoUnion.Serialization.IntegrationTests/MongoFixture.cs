using MongoDB.Bson;
using MongoDB.Driver;
using MongoUnion.Serialization;
using Xunit;

namespace MongoUnion.Serialization.IntegrationTests;

/// <summary>
/// Shared connection to a local MongoDB. Registers union serialization, verifies the server is
/// reachable, hands each test an isolated database, and drops that database on teardown.
/// </summary>
public sealed class MongoFixture : IAsyncLifetime
{
    private const string ConnectionString =
        "mongodb://127.0.0.1:27017/?directConnection=true&serverSelectionTimeoutMS=2000";

    private MongoClient _client = null!;

    public bool IsAvailable { get; private set; }
    public IMongoDatabase Database { get; private set; } = null!;
    public string DatabaseName { get; } = "mongounion_it_" + Guid.NewGuid().ToString("N");

    public async Task InitializeAsync()
    {
        UnionSerialization.Register();

        _client = new MongoClient(ConnectionString);
        try
        {
            await _client.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1));
            Database = _client.GetDatabase(DatabaseName);
            IsAvailable = true;
        }
        catch (TimeoutException)
        {
            IsAvailable = false;
        }
        catch (MongoConnectionException)
        {
            IsAvailable = false;
        }
    }

    public async Task DisposeAsync()
    {
        if (IsAvailable)
            await _client.DropDatabaseAsync(DatabaseName);
    }

    /// <summary>Returns a freshly named collection so tests don't interfere with one another.</summary>
    public IMongoCollection<T> NewCollection<T>() =>
        Database.GetCollection<T>("c_" + Guid.NewGuid().ToString("N"));

    public IMongoCollection<BsonDocument> RawCollection(string name) =>
        Database.GetCollection<BsonDocument>(name);
}

[CollectionDefinition(Name)]
public sealed class MongoCollection : ICollectionFixture<MongoFixture>
{
    public const string Name = "mongodb";
}
