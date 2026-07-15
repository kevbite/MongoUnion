---
layout: post
title: Supporting C# Union Types in MongoDB
tags: [csharp, dotnet, mongodb, serialization, unions]
---

<!-- NOTE: front matter is a best guess, adjust to match your blog's format. -->

C# 15 ships with .NET 11 this November, and one of the features I've been waiting years for is finally landing: **union types**. If you've ever modelled a value that is "one of a fixed set of shapes" and reached for an abstract base class, an enum plus a bag of nullable properties, or a third-party library like OneOf, unions are about to make your life a lot simpler.

There's just one problem. The MongoDB C# driver doesn't know what a union is yet, and if you try to store one today it falls over. This post covers what a union type actually compiles down to, why the driver chokes on it, and how we can teach it to serialize unions the same way System.Text.Json does, so that anyone moving between the two gets the same mental model of how the data maps.

I've raised a ticket with the driver team to support this properly ([CSHARP-6127](https://jira.mongodb.org/browse/CSHARP-6127)), but until that lands, here's a serializer you can drop in today.

## Union types in thirty seconds

A union represents a value that is exactly one of several **case types**. You declare one with the new `union` keyword:

```csharp
public union Pet(Cat, Dog, Bird);

public record Cat(string Name, bool IsIndoor);
public record Dog(string Name, string Breed);
public record Bird(string Name, bool CanFly);
```

Each case type gets an implicit conversion, so you can just assign a `Cat` to a `Pet`, and the compiler makes `switch` expressions exhaustive across every case with no fallback arm needed:

```csharp
Pet pet = new Dog("Rex", "Labrador");

string describe = pet switch
{
    Cat c => $"{c.Name} the cat",
    Dog d => $"{d.Name} the {d.Breed}",
    Bird b => $"{b.Name} the bird",
};
```

I've always loved union types. I've leaned on them heavily in TypeScript for years, where a value being `Cat | Dog | Bird` is just how you model the world, and I've faked them in C# on more projects than I can count with [OneOf](https://kevsoft.net/2025/06/20/one-of-vs-fluent-results.html). I've even [given a talk on the OneOf library](https://kevsoft.net/events/2020-02-04-dotnetsheff-oneof-library.html). They're the backbone of railway-oriented programming, where instead of throwing exceptions you model success and failure as cases of a union and let the type system force you to handle every branch. So having real unions baked into the language, with the compiler doing the exhaustiveness checking for me, is genuinely one of the things I'm most excited about in C# 15.

If you've read my post on [polymorphic documents in MongoDB](https://kevsoft.net/2026/06/13/polymorphic-documents-in-mongodb-with-csharp.html), this will feel familiar. It's the same "closed set of related shapes" problem, but expressed in the type system instead of a class hierarchy, and the compiler does the exhaustiveness checking for you.

System.Text.Json already understands unions as of [.NET 11 Preview 6](https://devblogs.microsoft.com/dotnet/dotnet-11-preview-6/). It writes the active case's value directly with no wrapper object, so a union of `int` and `string` round-trips as `42` or `"hello"`. That's the behavior we want to match.

## What happens in the driver today

Here's the whole program. Insert three pets, read them back:

```csharp
using MongoDB.Driver;

var client = new MongoClient("mongodb://127.0.0.1:27017");
var collection = client.GetDatabase("test").GetCollection<Pet>("pets");

await collection.InsertOneAsync(new Cat("Whiskers", IsIndoor: true));
await collection.InsertOneAsync(new Dog("Fido", Breed: "Labrador"));
await collection.InsertOneAsync(new Bird("Tweety", CanFly: true));

var allPets = await collection.Find(_ => true).ToListAsync();
```

Run this against the latest driver (3.10.0) and it doesn't even get as far as talking to the database:

```shell
MongoDB.Bson.BsonSerializationException: Creator map for class Pet has 1 arguments, but none are configured.
   at MongoDB.Bson.Serialization.BsonCreatorMap.Freeze()
   at MongoDB.Bson.Serialization.BsonClassMap.Freeze()
   at MongoDB.Bson.Serialization.BsonClassMap.LookupClassMap(Type classType)
   at MongoDB.Bson.Serialization.BsonClassMapSerializationProvider.GetSerializer(...)
```

The exception fires at `GetCollection<Pet>`, before a single document is written. The driver has no concept of a union, so it does what it does for any other type it doesn't recognize: it hands it to the class map provider, which tries to treat `Pet` as a plain old object. It finds a constructor that takes one argument, decides that argument must map to a property, can't find one, and gives up. Unions aren't broken in the driver so much as invisible to it.

## Pulling a union apart

Before we can serialize a union, we need to know what one actually looks like at runtime. The language spec tells us a union is any type carrying `System.Runtime.CompilerServices.UnionAttribute`, but the most reliable way to see the shape is to reflect over it:

```csharp
var t = typeof(Pet);
Console.WriteLine($"IsValueType={t.IsValueType}");
foreach (var i in t.GetInterfaces()) Console.WriteLine(i.Name);
foreach (var c in t.GetConstructors()) Console.WriteLine($"ctor({c.GetParameters()[0].ParameterType.Name})");
foreach (var p in t.GetProperties()) Console.WriteLine($"{p.PropertyType.Name} {p.Name}");
```

Which prints:

```shell
IsValueType=True
IUnion
ctor(Cat)
ctor(Dog)
ctor(Bird)
Object Value
```

So a union `Pet(Cat, Dog, Bird)` gives us four things worth holding on to:

- It's a **struct**, and it implements `IUnion` and carries `UnionAttribute`, so we can detect one without hard-coding type names.
- It has one **constructor per case type**, each taking a single argument. These are the creation members the compiler uses for those implicit conversions, and they're also how we wrap a value back into the union.
- It exposes a **`Value` property of type `object`** that holds whichever case is currently active, or null.

That's everything a serializer needs. We can read the active case out of `Value`, and we can put a case back in through the matching constructor.

I've wrapped that reflection up behind a small cache so we only pay for it once per type:

```csharp
internal sealed class UnionInfo
{
    public static bool IsUnion(Type type) =>
        typeof(IUnion).IsAssignableFrom(type) || type.IsDefined(typeof(UnionAttribute), false);

    public IReadOnlyList<Type> CaseTypes { get; }        // from the constructors
    public object? GetValue(object union) => ...;         // reads the Value property
    public object Create(Type caseType, object value) => ...; // invokes the matching constructor
}
```

## A serializer, not a convention

The obvious question when you want to change how the driver handles a type is whether this should be a **convention** or a **serializer**. It's worth being clear about why, because it's easy to reach for the wrong one.

Conventions only ever shape a `BsonClassMap`. They tweak how members are discovered, how creators are matched, how the discriminator is named. All of that lives inside the class map model, and a union has no members to map. That model is exactly what threw the exception above. No amount of convention tweaking makes a union fit a shape it was never meant to have.

A **serializer** is the right tool. It owns the bytes directly, reading and writing the BSON however it likes. And to get the driver to use it ahead of the class map provider, we register an `IBsonSerializationProvider` that recognizes unions and returns our serializer, deferring everything else back to the driver:

```csharp
public sealed class UnionSerializationProvider : IBsonSerializationProvider
{
    public IBsonSerializer? GetSerializer(Type type)
    {
        if (!UnionInfo.IsUnion(type))
            return null; // not a union, let the driver's other providers handle it

        var serializerType = typeof(UnionSerializer<>).MakeGenericType(type);
        return (IBsonSerializer)Activator.CreateInstance(serializerType)!;
    }
}
```

This is the same shape as System.Text.Json's approach, where a union is recognized through its own contract kind rather than the default object contract. The provider is the discovery step ("this is a union, don't class-map it"), and the serializer does the work.

You register it once at startup, before you touch a collection:

```csharp
BsonSerializer.RegisterSerializationProvider(new UnionSerializationProvider());
```

Registering a provider puts it at the front of the queue, so it gets first refusal on every type, and steps aside for anything that isn't a union.

I like to wrap that up so it's safe to call from anywhere and only takes effect once. This is also a nice excuse to use the new [`System.Threading.Lock`](https://learn.microsoft.com/en-us/dotnet/api/system.threading.lock) type from C# 13, which the `lock` statement understands directly and is a touch faster than locking on a plain `object`:

```csharp
public static class UnionSerialization
{
    private static bool _registered;
    private static readonly Lock Gate = new();

    public static void Register()
    {
        lock (Gate)
        {
            if (_registered)
                return;

            BsonSerializer.RegisterSerializationProvider(new UnionSerializationProvider());
            _registered = true;
        }
    }
}
```

Then it's just `UnionSerialization.Register()` at the top of your app.

## Writing a union

Writing is the easy direction. We read the active case out of `Value`, find its serializer, and let it write itself:

```csharp
public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args, TUnion value)
{
    var caseValue = _info.GetValue(value!);
    if (caseValue is null)
    {
        context.Writer.WriteNull();
        return;
    }

    var caseType = caseValue.GetType();
    var caseArgs = args;
    caseArgs.NominalType = caseType;
    BsonSerializer.LookupSerializer(caseType).Serialize(context, caseArgs, caseValue);
}
```

The one subtlety worth calling out is `caseArgs.NominalType = caseType`. When you delegate to a case's serializer, the driver compares the value's actual type against the nominal type it was asked to serialize. If those differ, and they do here (nominal `Pet`, actual `Cat`), the class map serializer assumes polymorphism and writes a `_t` discriminator into the document. That's the thing we're specifically trying to avoid, because System.Text.Json doesn't do it. Setting the nominal type to the case type tells the serializer "this is exactly a `Cat`, nothing clever going on", and the discriminator disappears.

## Reading a union back

Reading is where it gets interesting, because we've deliberately thrown away the one piece of information that would make this trivial: the discriminator. Without a `_t` telling us which case we're looking at, we have to recover it from the shape of the value itself.

We peek at the BSON type. A document is matched by its set of field names, and a scalar is matched by its BSON type:

```csharp
public override TUnion Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
{
    var bsonType = context.Reader.GetCurrentBsonType();
    if (bsonType == BsonType.Null)
    {
        context.Reader.ReadNull();
        return default!;
    }

    Type caseType;
    object caseValue;

    if (bsonType == BsonType.Document)
    {
        var document = BsonDocumentSerializer.Instance.Deserialize(context);
        caseType = SelectDocumentCase(document);

        if (!ElementNamesFor(caseType).Contains("_id"))
            document.Remove("_id");

        caseValue = BsonSerializer.Deserialize(document, caseType);
    }
    else
    {
        caseType = SelectScalarCase(bsonType);
        caseValue = BsonSerializer.LookupSerializer(caseType).Deserialize(context, args);
    }

    return (TUnion)_info.Create(caseType, caseValue);
}
```

`SelectDocumentCase` buffers the document, compares its field names against each case type's mapped members, and picks the one that matches exactly. If nothing matches, or more than one does, it throws a clear exception rather than guessing.

There's one wrinkle that only shows up once you're talking to a real database, and it's the sort of thing unit tests happily miss. When MongoDB stores a top-level document it injects an `_id`, so a `Cat` comes back as `{ _id, Name, IsIndoor }`, not `{ Name, IsIndoor }`. An exact field-name match would reject it. So the matcher ignores `_id` for any case that doesn't map its own id, and we strip it before deserializing the case, otherwise the case's class map complains about an element it doesn't recognize. Documents nested in an array or sub-document don't get an `_id`, so they match exactly.

## What ends up in MongoDB

This is the part I always want to see for myself. Once the provider is registered, inserting a `Cat` stores exactly what you'd hope:

```json
{
  "_id": ObjectId("66a3f1c2e5b4a2d1f0c99a01"),
  "Name": "Whiskers",
  "IsIndoor": true
}
```

No wrapper. No `_t`. Just the case, sitting in the collection as if you'd stored a `Cat` directly, which is exactly how System.Text.Json writes it too. The `Dog` and `Bird` look just as clean, each with their own fields.

Unions compose the way you'd want, so an array of pets embedded in a household document is just an array of bare case documents:

```json
{
  "_id": ObjectId("66a3f1c2e5b4a2d1f0c99a02"),
  "Owner": "Alice",
  "Favourite": { "Name": "Fido", "Breed": "Labrador" },
  "Pets": [
    { "Name": "Whiskers", "IsIndoor": true },
    { "Name": "Fido", "Breed": "Labrador" },
    { "Name": "Tweety", "CanFly": true }
  ]
}
```

Notice the array elements and the embedded `Favourite` have no `_id` of their own, only the top-level document does. Read that household back and every pet, whether it's the single favourite or one of the three in the array, comes back as the right case type.

## Reading a case back as its concrete type

Here's a nice side effect of writing no discriminator. Because a stored `Cat` is just `{ Name, IsIndoor }` with nothing union-specific wrapped around it, the document genuinely *is* a `Cat`. So you'd hope you could point a `Cat`-typed collection at it and read it straight back:

```csharp
await database.GetCollection<Pet>("pets").InsertOneAsync(new Cat("Whiskers", IsIndoor: true));

var cat = await database.GetCollection<Cat>("pets")
    .Find(_ => true)
    .FirstAsync();
```

Run that and it throws:

```shell
System.FormatException: Element '_id' does not match any field or property of class Cat.
```

This trips people up, but it's worth being clear that it has nothing to do with unions. MongoDB adds an `_id` to every top-level document on insert, and the `Cat` record has no member to bind it to, so the class map serializer rejects it. You'd get the exact same error storing a plain id-less `Cat` directly. The data is fine, the read is just strict about extra elements.

Account for the `_id` and it reads back cleanly. The quickest way is to project it out:

```csharp
var cat = await database.GetCollection<Cat>("pets")
    .Find(_ => true)
    .Project<Cat>(Builders<Cat>.Projection.Exclude("_id"))
    .FirstAsync();
```

Or give the case type an `Id` member, or mark it `[BsonIgnoreExtraElements]`, whichever fits your model. The point is that the union serializer isn't standing in your way here. Because there's no discriminator baked into the document, the case types stay perfectly readable on their own.

## Caveats and limitations

This works, and I've got it round-tripping through a real MongoDB across sub-documents and arrays, but it is a stopgap, and it's honest to say where it stops.

**Same-shaped cases are ambiguous.** Because we recover the case from its shape, two cases that serialize to the same shape can't be told apart. Consider:

```csharp
public union Measurement(Square, Circle);
public record Square(int Size);
public record Circle(int Size);
```

Both write to `{ "Size": ... }`. Writing works fine, but reading throws:

```shell
BsonSerializationException: Document fields {Size} are ambiguous across union
'Measurement' cases Square, Circle; a discriminator would be required to disambiguate.
```

I'd rather throw loudly than pick a case at random and hand you back the wrong type. This is the same stance System.Text.Json takes, it treats cases that share a JSON token as ambiguous. The proper fix is a discriminator, which is exactly the sort of thing the driver should own rather than something bolted on from the outside.

**Scalar matching is basic.** Scalars are matched by BSON type, so a union of `int` and `long` has the same ambiguity problem, and I've only wired up the common types.

**No LINQ or type-safe filtering.** Filtering a union field in a `Find` isn't something this touches. And because a union isn't a base class that its cases derive from, you can't lean on the driver's polymorphic helpers either. The obvious thing to reach for:

```csharp
var builder = Builders<Pet>.Filter.OfType<Bird>();
```

doesn't even compile:

```shell
Program.cs(17,36): Error CS0311: The type 'Bird' cannot be used as type parameter
'TDerived' in the generic type or method 'FilterDefinitionBuilder<Pet>.OfType<TDerived>()'.
There is no implicit reference conversion from 'Bird' to 'Pet'.
```

`OfType<TDerived>` expects `TDerived` to inherit from `Pet`, but a union case doesn't inherit from the union, it converts to it. Querying by case is a whole separate piece of work, and it's another reason this really wants to live in the driver.

None of these are hard blockers for the "store a closed set of shapes and read them back" case, which covers a lot of real modelling. But this post is really just an example of how to *get it working*, not a finished product. There's plenty that would be nice to have: a `[BsonUnion]` attribute to opt into a discriminator so same-shaped cases work, richer scalar matching, LINQ and type-safe filtering, and the rest. That's the sort of thing that belongs in the driver rather than bolted on from the outside, which is why I opened [CSHARP-6127](https://jira.mongodb.org/browse/CSHARP-6127). My hope is that the MongoDB driver team ends up supporting an extensive set of features here, increasing type safety and making union types seamless to work with in MongoDB.

## Wrapping up

Union types are one of the best things coming to C# in years, and the gap between "the language has them" and "my database understands them" is exactly the kind of gap a serializer is built to close. A provider to spot the union, a serializer to read the `Value` and write the active case, and a bit of care around the discriminator and MongoDB's `_id`, and you've got clean, System.Text.Json-compatible documents with no wrapper cruft.

You can find the full working solution, including the unit and integration tests, on GitHub: [kevbite/MongoUnion](https://github.com/kevbite/MongoUnion) (placeholder link for now, I'll update it once it's published).

And if this is something you'd like to see properly supported, go and vote on [CSHARP-6127](https://jira.mongodb.org/browse/CSHARP-6127). The more interest it gets, the sooner unions become something you just store, without thinking about it at all.


