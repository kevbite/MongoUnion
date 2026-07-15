using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace MongoUnion.Serialization.IntegrationTests;

[Collection(MongoCollection.Name)]
public class PetUnionIntegrationTests(MongoFixture fixture)
{
    [SkippableFact]
    public async Task Reading_a_pet_as_its_concrete_case_trips_on_the_server_added_id()
    {
        Skip.IfNot(fixture.IsAvailable, "MongoDB is not running on 127.0.0.1:27017.");
        var name = "pets_" + Guid.NewGuid().ToString("N");

        await fixture.Database.GetCollection<Pet>(name).InsertOneAsync(new Cat("Whiskers", IsIndoor: true));

        // The stored document is a clean Cat, but MongoDB adds an _id on insert, and the id-less
        // Cat record has no member to bind it to. This is a normal MongoDB concern, not a union one.
        var cats = fixture.Database.GetCollection<Cat>(name);
        var ex = await Assert.ThrowsAsync<FormatException>(
            () => cats.Find(FilterDefinition<Cat>.Empty).FirstAsync());

        Assert.Contains("_id", ex.Message);
    }

    [SkippableFact]
    public async Task Reading_a_pet_as_its_concrete_case_works_once_the_id_is_excluded()
    {
        Skip.IfNot(fixture.IsAvailable, "MongoDB is not running on 127.0.0.1:27017.");
        var name = "pets_" + Guid.NewGuid().ToString("N");

        await fixture.Database.GetCollection<Pet>(name).InsertOneAsync(new Cat("Whiskers", IsIndoor: true));

        // No discriminator is stored, so the document genuinely is a Cat. Exclude the _id and it
        // reads straight back through a Cat-typed collection.
        var cat = await fixture.Database.GetCollection<Cat>(name)
            .Find(FilterDefinition<Cat>.Empty)
            .Project<Cat>(Builders<Cat>.Projection.Exclude("_id"))
            .FirstAsync();

        Assert.Equal(new Cat("Whiskers", true), cat);
    }

    [SkippableFact]
    public async Task Inserts_and_reads_back_every_case()
    {
        Skip.IfNot(fixture.IsAvailable, "MongoDB is not running on 127.0.0.1:27017.");
        var pets = fixture.NewCollection<Pet>();

        await pets.InsertOneAsync(new Cat("Whiskers", IsIndoor: true));
        await pets.InsertOneAsync(new Dog("Fido", Breed: "Labrador"));
        await pets.InsertOneAsync(new Bird("Tweety", CanFly: true));

        var readBack = await pets.Find(FilterDefinition<Pet>.Empty).ToListAsync();
        var values = readBack.Select(p => p.Value).ToList();

        Assert.Equal(3, values.Count);
        Assert.Contains(new Cat("Whiskers", true), values);
        Assert.Contains(new Dog("Fido", "Labrador"), values);
        Assert.Contains(new Bird("Tweety", true), values);
    }

    [SkippableFact]
    public async Task Stored_document_matches_the_case_shape_with_no_discriminator()
    {
        Skip.IfNot(fixture.IsAvailable, "MongoDB is not running on 127.0.0.1:27017.");
        var pets = fixture.NewCollection<Pet>();

        await pets.InsertOneAsync(new Cat("Whiskers", IsIndoor: true));

        var raw = await fixture.RawCollection(pets.CollectionNamespace.CollectionName)
            .Find(FilterDefinition<BsonDocument>.Empty)
            .FirstAsync();

        Assert.False(raw.Contains("_t")); // no type discriminator, same as System.Text.Json
        Assert.True(raw.Contains("_id")); // MongoDB's own key
        Assert.Equal("Whiskers", raw["Name"].AsString);
        Assert.True(raw["IsIndoor"].AsBoolean);
    }

    [SkippableFact]
    public async Task Replaces_a_document_switching_the_active_case()
    {
        Skip.IfNot(fixture.IsAvailable, "MongoDB is not running on 127.0.0.1:27017.");
        var pets = fixture.NewCollection<Pet>();

        await pets.InsertOneAsync(new Cat("Whiskers", IsIndoor: true));
        var stored = await pets.Find(FilterDefinition<Pet>.Empty).FirstAsync();

        // Replacing keeps the same _id but swaps the active case from Cat to Dog.
        await pets.ReplaceOneAsync(FilterDefinition<Pet>.Empty, new Dog("Rex", "Poodle"));
        var replaced = await pets.Find(FilterDefinition<Pet>.Empty).FirstAsync();

        Assert.IsType<Cat>(stored.Value);
        Assert.Equal(new Dog("Rex", "Poodle"), Assert.IsType<Dog>(replaced.Value));
    }

    [SkippableFact]
    public async Task Stores_and_reads_union_as_subdocument_and_in_an_array()
    {
        Skip.IfNot(fixture.IsAvailable, "MongoDB is not running on 127.0.0.1:27017.");
        var households = fixture.NewCollection<Household>();

        var household = new Household
        {
            Owner = "Alice",
            Favourite = new Dog("Fido", "Labrador"),
            Pets = [new Cat("Whiskers", true), new Dog("Fido", "Labrador"), new Bird("Tweety", true)]
        };
        await households.InsertOneAsync(household);

        var back = await households.Find(FilterDefinition<Household>.Empty).FirstAsync();

        Assert.Equal("Alice", back.Owner);
        Assert.Equal(new Dog("Fido", "Labrador"), Assert.IsType<Dog>(back.Favourite.Value)); // sub-document
        Assert.Equal(3, back.Pets.Count);
        Assert.Contains(new Cat("Whiskers", true), back.Pets.Select(p => p.Value));
        Assert.Contains(new Dog("Fido", "Labrador"), back.Pets.Select(p => p.Value));
        Assert.Contains(new Bird("Tweety", true), back.Pets.Select(p => p.Value));
    }

    [SkippableFact]
    public async Task Nested_and_array_elements_have_no_id_or_discriminator()
    {
        Skip.IfNot(fixture.IsAvailable, "MongoDB is not running on 127.0.0.1:27017.");
        var households = fixture.NewCollection<Household>();

        await households.InsertOneAsync(new Household
        {
            Owner = "Bob",
            Favourite = new Cat("Whiskers", true),
            Pets = [new Dog("Fido", "Labrador"), new Bird("Tweety", true)]
        });

        var raw = await fixture.RawCollection(households.CollectionNamespace.CollectionName)
            .Find(FilterDefinition<BsonDocument>.Empty)
            .FirstAsync();

        Assert.True(raw.Contains("_id")); // only the top-level document gets an _id

        var favourite = raw["Favourite"].AsBsonDocument;
        Assert.False(favourite.Contains("_t"));
        Assert.False(favourite.Contains("_id"));
        Assert.Equal(new[] { "Name", "IsIndoor" }, favourite.Names);

        Assert.All(raw["Pets"].AsBsonArray, element =>
        {
            var doc = element.AsBsonDocument;
            Assert.False(doc.Contains("_t"));
            Assert.False(doc.Contains("_id"));
        });
    }
}

// --- Types under test ---

public union Pet(Cat, Dog, Bird);

public record Cat(string Name, bool IsIndoor);
public record Dog(string Name, string Breed);
public record Bird(string Name, bool CanFly);

public class Household
{
    public ObjectId Id { get; set; }
    public string Owner { get; set; } = "";
    public Pet Favourite { get; set; }
    public List<Pet> Pets { get; set; } = [];
}
