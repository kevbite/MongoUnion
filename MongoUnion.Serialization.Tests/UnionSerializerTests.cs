using System.Runtime.CompilerServices;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoUnion.Serialization;
using Xunit;

namespace MongoUnion.Serialization.Tests;

internal static class TestModuleInitializer
{
    [ModuleInitializer]
    internal static void Init()
    {
        // Guids need an explicit representation in the modern driver, otherwise serialization throws.
        BsonSerializer.RegisterSerializer(new GuidSerializer(GuidRepresentation.Standard));
        UnionSerialization.Register();
    }
}

public class UnionSerializerTests
{
    // --- Representation: active case written directly, no wrapper, no discriminator ---

    [Fact]
    public void Serializes_object_case_as_the_bare_case_document()
    {
        var doc = new Pet(new Cat("Whiskers", IsIndoor: true)).ToBsonDocument();

        Assert.Equal(new[] { "Name", "IsIndoor" }, doc.Names);
        Assert.Equal("Whiskers", doc["Name"].AsString);
        Assert.True(doc["IsIndoor"].AsBoolean);
        Assert.False(doc.Contains("_t")); // matches System.Text.Json: no type discriminator
    }

    // --- Round-trips for every object case ---

    [Fact]
    public void RoundTrips_cat()
    {
        Pet original = new(new Cat("Whiskers", IsIndoor: true));

        var back = BsonSerializer.Deserialize<Pet>(original.ToBsonDocument());

        Assert.Equal(new Cat("Whiskers", true), Assert.IsType<Cat>(back.Value));
    }

    [Fact]
    public void RoundTrips_dog()
    {
        Pet original = new(new Dog("Fido", Breed: "Labrador"));

        var back = BsonSerializer.Deserialize<Pet>(original.ToBsonDocument());

        Assert.Equal(new Dog("Fido", "Labrador"), Assert.IsType<Dog>(back.Value));
    }

    [Fact]
    public void RoundTrips_bird()
    {
        Pet original = new(new Bird("Tweety", CanFly: true));

        var back = BsonSerializer.Deserialize<Pet>(original.ToBsonDocument());

        Assert.Equal(new Bird("Tweety", true), Assert.IsType<Bird>(back.Value));
    }

    [Fact]
    public void Deserializes_case_when_document_carries_a_mongo_id()
    {
        // Mirrors how a document looks once MongoDB has persisted it: an extra '_id' field
        // that isn't part of the case type's shape.
        var stored = new BsonDocument
        {
            { "_id", ObjectId.GenerateNewId() },
            { "Name", "Whiskers" },
            { "IsIndoor", true }
        };

        var back = BsonSerializer.Deserialize<Pet>(stored);

        Assert.Equal(new Cat("Whiskers", true), Assert.IsType<Cat>(back.Value));
    }

    // --- Null handling (union embedded in a document) ---

    [Fact]
    public void RoundTrips_empty_union_as_null()
    {
        var holder = new Holder { Pet = default };

        var doc = holder.ToBsonDocument();
        Assert.Equal(BsonNull.Value, doc["Pet"]);

        var back = BsonSerializer.Deserialize<Holder>(doc);
        Assert.Null(back.Pet.Value);
    }

    [Fact]
    public void RoundTrips_union_nested_in_document()
    {
        var holder = new Holder { Pet = new Dog("Rex", "Poodle") };

        var back = BsonSerializer.Deserialize<Holder>(holder.ToBsonDocument());

        Assert.Equal(new Dog("Rex", "Poodle"), Assert.IsType<Dog>(back.Pet.Value));
    }

    [Fact]
    public void RoundTrips_union_nested_two_levels_deep()
    {
        var zoo = new Zoo { Name = "City Zoo", MainEnclosure = new Enclosure { Star = new Bird("Tweety", true) } };

        var back = BsonSerializer.Deserialize<Zoo>(zoo.ToBsonDocument());

        Assert.Equal("City Zoo", back.Name);
        Assert.Equal(new Bird("Tweety", true), Assert.IsType<Bird>(back.MainEnclosure.Star.Value));
    }

    // --- Arrays of unions ---

    [Fact]
    public void RoundTrips_array_of_unions()
    {
        var kennel = new Kennel
        {
            Pets = [new Cat("Whiskers", true), new Dog("Fido", "Labrador"), new Bird("Tweety", true)]
        };

        var back = BsonSerializer.Deserialize<Kennel>(kennel.ToBsonDocument());

        Assert.Equal(3, back.Pets.Count);
        Assert.Equal(new Cat("Whiskers", true), Assert.IsType<Cat>(back.Pets[0].Value));
        Assert.Equal(new Dog("Fido", "Labrador"), Assert.IsType<Dog>(back.Pets[1].Value));
        Assert.Equal(new Bird("Tweety", true), Assert.IsType<Bird>(back.Pets[2].Value));
    }

    [Fact]
    public void Array_elements_carry_no_discriminator_or_id()
    {
        var kennel = new Kennel { Pets = [new Cat("Whiskers", true), new Dog("Fido", "Labrador")] };

        var array = kennel.ToBsonDocument()["Pets"].AsBsonArray;

        Assert.All(array, element =>
        {
            var doc = element.AsBsonDocument;
            Assert.False(doc.Contains("_t")); // no discriminator, same as System.Text.Json
            Assert.False(doc.Contains("_id")); // array elements are not top-level documents
        });
        Assert.Equal(new[] { "Name", "IsIndoor" }, array[0].AsBsonDocument.Names);
        Assert.Equal(new[] { "Name", "Breed" }, array[1].AsBsonDocument.Names);
    }

    // --- Scalar cases selected by BSON type ---

    [Theory]
    [InlineData(42)]
    [InlineData("hello")]
    public void RoundTrips_scalar_union(object caseValue)
    {
        Numeric original = caseValue is int i ? new Numeric(i) : new Numeric((string)caseValue);
        var holder = new ScalarHolder { Value = original };

        var back = BsonSerializer.Deserialize<ScalarHolder>(holder.ToBsonDocument());

        Assert.Equal(caseValue, back.Value.Value);
    }

    // A union is a value type, and MongoDB can't have a value type as a root document, so scalar
    // unions live inside a { Value: ... } holder. Each .NET type maps to a distinct BSON type,
    // which is how the case is recovered on read.
    public static readonly Dictionary<string, object> Scalars = new()
    {
        ["int"] = 42,
        ["long"] = 9_000_000_000L,
        ["string"] = "hello",
        ["bool"] = true,
        ["double"] = 3.14d,
        ["decimal"] = 9.99m,
        ["Guid"] = Guid.Parse("11111111-2222-3333-4444-555555555555"),
        ["DateTime"] = new DateTime(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc),
        ["ObjectId"] = ObjectId.Parse("66a3f1c2e5b4a2d1f0c99a01"),
    };

    [Theory]
    [InlineData("int")]
    [InlineData("long")]
    [InlineData("string")]
    [InlineData("bool")]
    [InlineData("double")]
    [InlineData("decimal")]
    [InlineData("Guid")]
    [InlineData("DateTime")]
    [InlineData("ObjectId")]
    public void RoundTrips_every_scalar_dotnet_type(string key)
    {
        var value = Scalars[key];
        var box = new ScalarBox { Value = (AllScalars)Activator.CreateInstance(typeof(AllScalars), value)! };

        var back = BsonSerializer.Deserialize<ScalarBox>(box.ToBsonDocument());

        Assert.Equal(value, back.Value.Value);
    }

    // --- Documented limitation: cases with identical shape are ambiguous ---

    [Fact]
    public void Throws_when_cases_share_the_same_shape()
    {
        // Both Square and Circle serialize to { "Size": ... }, so the case cannot be recovered
        // without a discriminator.
        var doc = new Ambiguous(new Square(5)).ToBsonDocument();

        var ex = Assert.Throws<BsonSerializationException>(() => BsonSerializer.Deserialize<Ambiguous>(doc));
        Assert.Contains("ambiguous", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}

// --- Types under test ---

public union Pet(Cat, Dog, Bird);

public record Cat(string Name, bool IsIndoor);
public record Dog(string Name, string Breed);
public record Bird(string Name, bool CanFly);

public class Holder
{
    public Pet Pet { get; set; }
}

public class Kennel
{
    public List<Pet> Pets { get; set; } = [];
}

public class Enclosure
{
    public Pet Star { get; set; }
}

public class Zoo
{
    public string Name { get; set; } = "";
    public Enclosure MainEnclosure { get; set; } = new();
}

public union Numeric(int, string);

public class ScalarHolder
{
    public Numeric Value { get; set; }
}

public union AllScalars(int, long, string, bool, double, decimal, Guid, DateTime, ObjectId);

public class ScalarBox
{
    public AllScalars Value { get; set; }
}

public union Ambiguous(Square, Circle);

public record Square(int Size);
public record Circle(int Size);
