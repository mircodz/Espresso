# Espresso

An in-memory caching library for .NET — a faithful port of
[Caffeine](https://github.com/ben-manes/caffeine).

## Install

```sh
dotnet add package Espresso.Caching
```

## Quick start

```csharp
using Espresso;

ICache<string, string> cache = Espresso.NewBuilder<string, string>()
    .MaximumSize(10_000)
    .ExpireAfterWrite(TimeSpan.FromMinutes(5))
    .RecordStats()
    .Build();

cache.Put("hello", "world");
string? value = cache.GetIfPresent("hello");

// Compute-on-miss: the factory runs at most once per key, atomically.
string computed = cache.Get("key", k => Load(k))!;
```

## Acknowledgements

Espresso is a port of [Caffeine](https://github.com/ben-manes/caffeine) by Ben Manes. The eviction
policy, timer wheel, and frequency sketch follow its design closely.

## License

[MIT](LICENSE)
