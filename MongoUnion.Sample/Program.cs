using MongoDB.Driver;
using MongoUnion.Serialization;

// MongoUnion.Sample — the runnable demo from the blog post
// "Supporting C# Union Types in MongoDB" (https://kevsoft.net).
//
// Requires a MongoDB server on 127.0.0.1:27017, e.g.:
//   docker run -d -p 27017:27017 --name mongo mongo
//
// Teach the BSON serializer how to handle C# 15 union types before any collection is resolved.
UnionSerialization.Register();

var mongoClient = new MongoClient("mongodb://127.0.0.1:27017/?directConnection=true&serverSelectionTimeoutMS=2000");
var database = mongoClient.GetDatabase("test");
var collection = database.GetCollection<Pet>("pets");

await collection.InsertOneAsync(new Cat("Whiskers", IsIndoor: true));
await collection.InsertOneAsync(new Dog("Fido", Breed: "Labrador"));
await collection.InsertOneAsync(new Bird("Tweety", CanFly: true));

Console.WriteLine("Inserted 3 pets.");

var allPets = await collection.Find(_ => true).ToListAsync();
foreach (var pet in allPets)
{
    Console.WriteLine($"Read back: {pet.Value}");
}

public union Pet(Cat, Dog, Bird);

record Cat(string Name, bool IsIndoor);
record Dog(string Name, string Breed);
record Bird(string Name, bool CanFly);
