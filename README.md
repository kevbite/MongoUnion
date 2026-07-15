# MongoUnion

Teach the MongoDB C# driver how to serialize **C# 15 union types** the same way
`System.Text.Json` does — the active case written directly, with no wrapper object and no
type discriminator.

This repository is the companion code for the blog post
[**Supporting C# Union Types in MongoDB**](https://kevsoft.net/2026/07/15/supporting-csharp-union-types-in-mongodb.html)
on [kevsoft.net](https://kevsoft.net). The post walks through *why* the driver chokes on a union
today and *how* the serializer in this repo closes the gap. This README is the quick reference for
the code itself.

> ⚠️ **Preview software.** Union types ship with C# 15 / .NET 11 (November 2025). This code targets
> `net11.0` with `<LangVersion>preview</LangVersion>` and requires the .NET 11 preview SDK. It is a
> stopgap, not a production library — see [Caveats & limitations](#caveats--limitations).

## The problem in one paragraph

A union such as `public union Pet(Cat, Dog, Bird);` compiles down to a `struct` that implements
`IUnion`, carries `UnionAttribute`, has one constructor per case type, and exposes a single
`object Value` property holding the active case. The driver has no concept of a union, so it hands
`Pet` to its class-map provider, which tries to treat it as a plain POCO and throws before a single
document is written:

```text
MongoDB.Bson.BsonSerializationException: Creator map for class Pet has 1 arguments, but none are configured.
```

## The fix in one paragraph

A convention can't help — a union has no members to map. The right tool is a **serializer** that
owns the bytes directly, registered through an `IBsonSerializationProvider` that recognises unions
ahead of the class-map provider. On write it reads the active case out of `Value` and delegates to
that case type's own serializer (pinning the nominal type so no `_t` discriminator is emitted). On
read it recovers the case from the *shape* of the stored value — object cases by their set of field
names, scalar cases by their BSON type.

## Usage

Register the provider once at startup, before you touch a collection:

```csharp
using MongoUnion.Serialization;

UnionSerialization.Register(); // safe to call more than once; only the first call takes effect
```

Then use your union types with the driver as if they were ordinary documents:

```csharp
using MongoDB.Driver;

var collection = new MongoClient("mongodb://127.0.0.1:27017")
    .GetDatabase("test")
    .GetCollection<Pet>("pets");

await collection.InsertOneAsync(new Cat("Whiskers", IsIndoor: true));
await collection.InsertOneAsync(new Dog("Fido", Breed: "Labrador"));

var pets = await collection.Find(_ => true).ToListAsync();

public union Pet(Cat, Dog, Bird);

public record Cat(string Name, bool IsIndoor);
public record Dog(string Name, string Breed);
public record Bird(string Name, bool CanFly);
```

A stored `Cat` lands in the collection with no wrapper and no discriminator — exactly the shape
`System.Text.Json` would write:

```json
{ "_id": ObjectId("..."), "Name": "Whiskers", "IsIndoor": true }
```

## Project layout

| Project | Type | What it is |
| --- | --- | --- |
| **`MongoUnion.Serialization`** | Class library | The serializer. The only project you'd reference to use this. Three small files: `UnionInfo` (reflects over a union and caches its shape), `UnionSerializer<TUnion>` (reads/writes the active case), and `UnionSerializationProvider` / `UnionSerialization` (discovery + one-line registration). |
| **`MongoUnion.Serialization.Tests`** | xUnit | Fast, in-memory unit tests that serialize to `BsonDocument` and back. No database required. Covers representation, round-trips, nesting, arrays, every scalar type, and the ambiguous-shape failure. |
| **`MongoUnion.Serialization.IntegrationTests`** | xUnit | End-to-end tests against a real MongoDB on `127.0.0.1:27017`. Each test is a `[SkippableFact]` that skips automatically when no server is reachable, so the suite is safe to run without Mongo installed. |
| **`MongoUnion.Sample`** | Console app | The minimal runnable demo from the blog post: register the provider, insert three pets, read them back. |

## Getting started

### Prerequisites

- [.NET 11 preview SDK](https://dotnet.microsoft.com/download/dotnet/11.0) — the exact version is
  pinned in [`global.json`](global.json).
- (Integration tests / sample only) A MongoDB server listening on `127.0.0.1:27017`. The quickest
  way:

  ```shell
  docker run -d -p 27017:27017 --name mongo mongo
  ```

### Build

```shell
dotnet build MongoUnion.slnx
```

### Run the unit tests (no database needed)

```shell
dotnet test MongoUnion.Serialization.Tests
```

### Run the integration tests (needs MongoDB)

```shell
dotnet test MongoUnion.Serialization.IntegrationTests
```

Tests skip themselves if no server is reachable, so this is safe to run either way.

### Run the sample

```shell
dotnet run --project MongoUnion.Sample
```

## How it works

The three pieces of the library map onto the three jobs a serializer has to do:

1. **See the union.** `UnionInfo.IsUnion(type)` returns `true` for anything implementing `IUnion` or
   carrying `UnionAttribute`. `UnionInfo.For(type)` reflects over it once and caches the case types
   (from the single-argument constructors), the `Value` accessor, and the constructor used to wrap
   each case back up. `UnionSerializationProvider` uses this to hand back a
   `UnionSerializer<TUnion>` for unions and `null` for everything else.

2. **Write the active case.** `UnionSerializer.Serialize` pulls the case out of `Value`, looks up
   *that* type's serializer, and delegates to it — after setting `NominalType` to the case type so
   the class-map serializer treats it as non-polymorphic and writes no `_t`.

3. **Recover the case on read.** With no discriminator to lean on, `UnionSerializer.Deserialize`
   inspects the BSON. Documents are matched by field names against each case's mapped members
   (ignoring MongoDB's injected `_id` unless a case maps its own); scalars are matched by BSON type.
   Exactly one match wins; zero or more than one throws a clear `BsonSerializationException`.

## Caveats & limitations

This round-trips cleanly across sub-documents and arrays against a real MongoDB, but because the
case is recovered from shape alone there are honest limits — the same stance `System.Text.Json`
takes:

- **Same-shaped cases are ambiguous.** Two cases that serialize to identical BSON (e.g.
  `record Square(int Size)` and `record Circle(int Size)`) can be written but not read back, and the
  deserializer throws rather than guess.
- **Scalar matching is basic.** Scalars are matched by BSON type, so `int` vs `long` collide, and
  only the common types are wired up.
- **No LINQ or type-safe filtering.** A union isn't a base class its cases derive from, so the
  driver's polymorphic helpers (`OfType<TDerived>()`, etc.) don't apply.

The proper fix for these — an opt-in discriminator, richer scalar matching, LINQ support — belongs
in the driver itself. That's tracked in
[CSHARP-6127](https://jira.mongodb.org/browse/CSHARP-6127); if you'd like to see unions supported
first-class, go and vote for it.

## Further reading

- Blog post: [Supporting C# Union Types in MongoDB](https://kevsoft.net/2026/07/15/supporting-csharp-union-types-in-mongodb.html)
- Related: [Polymorphic documents in MongoDB with C#](https://kevsoft.net/2026/06/13/polymorphic-documents-in-mongodb-with-csharp.html)
- [.NET 11 Preview 6 — union types in System.Text.Json](https://devblogs.microsoft.com/dotnet/dotnet-11-preview-6/)
- Driver ticket: [CSHARP-6127](https://jira.mongodb.org/browse/CSHARP-6127)
