# Olam

Olam is a .NET 10 library that translates C# LINQ expression trees over POCO types into **OData 4.0 query options**. It has no third-party runtime dependencies. It generates queries only: it does not send requests, load service metadata, or deserialize responses.

## Basic use

```csharp
using Olam;

var query = ODataQuery.For<Product>()
    .Where(p => p.Price >= 25m && p.Name.StartsWith("A"))
    .OrderBy(p => p.Name)
    .Skip(10)
    .Take(20)
    .Select(p => new { p.Id, p.Name });

ODataQueryResult result = query.ToODataQuery();
string queryString = result.ToQueryString();

// Convenience equivalent:
string direct = query.ToQueryString();

// Unescaped OData values, useful when another component builds the URI:
string filter = result.QueryOptions["$filter"];
// ((Price ge 25) and startswith(Name,'A'))
```

`ToQueryString()` returns a string beginning with `?`, with each option value URI-escaped once. An unmodified root produces an empty string. The option dictionary is immutable, uses `$`-prefixed lowercase option names, and contains **unescaped** OData expressions. Strings inside those expressions already have OData apostrophe escaping. Do not URI-encode `ToQueryString()` a second time.

Both fluent LINQ and C# query syntax work. A query root does not need any instances of the POCO, a service URL, an entity-set name, or an EDM model. Callers remain responsible for using names and types that match their service's EDM.

## Supported queries

| LINQ / extension | Generated OData |
| --- | --- |
| `Where` | `$filter`, or a `filter(...)` transformation when needed before another transformation |
| `OrderBy`, `OrderByDescending`, `ThenBy`, `ThenByDescending` | `$orderby` |
| `Skip`, `Take` with integer counts | `$skip`, `$top` |
| `Select` a property, anonymous object, or parameterless POCO member initializer | `$select`; aliases and computed values also use `$apply=compute(...)` |
| `IncludeCount()` | `$count=true` |
| `Expand` / `ExpandSingle` | `$expand`, including nested options |
| `GroupBy(key).Select(...)` or `GroupBy(key, resultSelector)` | `$apply=groupby(...,aggregate(...))` |
| `Sum`, `Average`, `Min`, `Max`, `Count`, `LongCount` inside grouping projections | Aggregate output properties |
| `group.Select(...).Distinct().Count()` | `countdistinct` |
| `Distinct()` after a property projection | `groupby(...)` without aggregates |
| Captured scalar aggregate lambda | `$apply=aggregate(... as Value)` |

Filters and computed expressions support comparisons, Boolean operators, arithmetic, numeric casts, nullable `HasValue` / `Value`, nested property paths, collection `Any` / `All`, and collection counts. Captured collection `Contains` becomes equality alternatives; collection-property `Contains` becomes `any`. The translator also recognizes the span-based array membership expressions emitted by C# 14.

Supported string functions are `Contains(string)`, `StartsWith(string)`, `EndsWith(string)`, parameterless `ToLower`, `ToUpper`, `Trim`, `Substring`, `Length`, `Equals(string)`, two-string concatenation, and `IsNullOrEmpty`. Supported date components are `Year`, `Month`, `Day`, `Hour`, `Minute`, `Second`, and `Date` where available. `Math.Round`, `Floor`, and `Ceiling` with one argument map to OData functions.

Literal support includes strings, characters, Booleans, numbers, GUIDs, named enums and flags, `DateOnly`, `TimeOnly`, `DateTimeOffset`, `DateTime`, `TimeSpan`, binary values, and null. Numbers and dates use invariant formatting. `DateTime` values must have UTC or Local kind; use `DateOnly` for dates without a time zone.

## Grouping and computed projections

```csharp
var report = ODataQuery.For<Product>()
    .Where(p => p.Active)
    .GroupBy(p => p.Category)
    .Select(g => new
    {
        Category = g.Key,
        Total = g.Sum(p => p.Price),
        Count = g.Count()
    })
    .Where(g => g.Total > 100)
    .OrderByDescending(g => g.Total)
    .Take(5)
    .ToODataQuery();

// report.QueryOptions["$apply"]:
// filter(Active)/groupby((Category),aggregate(Price with sum as Total,$count as Count))
// report.QueryOptions["$filter"]: (Total gt 100)
```

Composite grouping keys can be anonymous objects. Select their individual members in the result, such as `g.Key.Category`. Grouping by a constant supports several aggregates over the entire input. Computed grouping keys, aggregate inputs, and aggregate outputs are translated into `compute(...)` transformations at the appropriate stage.

```csharp
var projected = ODataQuery.For<Product>()
    .Select(p => new { p.Id, Gross = p.Price * 1.2m })
    .Where(p => p.Gross > 50)
    .ToODataQuery();
// $apply: compute((Price mul 1.2) as Gross)
// $filter: (Gross gt 50)
// $select: Id,Gross
```

Computed output names must not conflict with existing properties. Direct selections such as `new { p.Price }` retain the original property. Renaming a property, for example `new { Label = p.Name }`, generates `compute(Name as Label)` so the output alias is represented by the query itself. Scalar computed selections use the alias `Value`.

These features require the server's **OData data aggregation extension**, including the relevant `$apply` transformations. They use `compute(...)` within `$apply` rather than the OData 4.01 `$compute` option. ASP.NET Core OData **9.5.0** is the compatibility test baseline; a server's enabled options and underlying data provider still determine which expressions it accepts.

## Standalone aggregates

Ordinary LINQ scalar calls execute a provider immediately. Capture the operation in an expression instead:

```csharp
var total = ODataQuery.For<Product>()
    .Where(p => p.Active)
    .ToODataQuery(q => q.Sum(p => p.Price));
// $apply: filter(Active)/aggregate(Price with sum as Value)

var count = ODataQuery.For<Product>()
    .ToODataQuery(q => q.Count(p => p.Active));
// $apply: filter(Active)/aggregate($count as Value)
```

The lambda is inspected, never executed. These queries request a single aggregation row with a property named `Value`; they do not request the `/$count` resource path. `IncludeCount()` instead requests count metadata alongside a collection. Scalar aggregate generation omits collection-count metadata.

## Expansion

```csharp
var query = ODataQuery.For<Product>()
    .Expand(p => p.Orders, orders => orders
        .Where(o => o.Quantity > 0)
        .OrderByDescending(o => o.Amount)
        .Take(3)
        .Select(o => new { o.Id, o.Amount })
        .IncludeCount())
    .ExpandSingle(p => p.Supplier, supplier => supplier
        .Select(s => new { s.Id, s.Name }));
```

Use `Expand(p => p.Orders)` for an expansion without nested options. Collection expansions support nested filters, ordering, paging, property selection, count, and further expansions. `ExpandSingle` permits only selection and further expansion. Nested `$apply` transformations are unsupported. Repeating identical expansions is allowed; conflicting options for one path throw. Expansion is explicit; selection does not infer navigation expansion without service metadata.

## Property and enum names

CLR names are used by default, without required annotations. Configure a root when the service uses other names:

```csharp
using System.Text.Json;

var options = new ODataQueryOptions
{
    UseJsonPropertyNames = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
};
options.MapProperty<Product, string>(p => p.Name, "title");
options.MapEnum<ProductStatus>("MyService.ProductStatus");

var query = ODataQuery.For<Product>(options);
```

Name precedence is explicit property override, enabled `[JsonPropertyName]`, naming policy, then CLR name. Overrides select a direct property; configure a nested type's own property to map a nested path. Projection members also use this naming configuration. Mapping names are validated as OData identifiers. Enum names default to the CLR namespace-qualified type name, with an override available when the EDM uses a different namespace. Undefined numeric enum values are rejected.

Each root takes a configuration snapshot. Reusing or composing queries does not mutate earlier queries or results. Captured field and property values are read when a query is converted, so changing a captured cutoff changes the next generated query. Capture results of arbitrary methods, static clocks, or value construction in variables before using them in the query. The translator does not compile predicates or invoke arbitrary captured method calls.

## Translation boundaries

Unsupported expressions throw `ODataTranslationException`, which includes the offending expression. There is no silent omission or local-query fallback. `ToList()`, enumeration, and direct `Count()` / `Sum()` calls throw because the provider cannot execute queries. Conversion of foreign `IQueryable` providers is unsupported.

Filtering, ordering, grouping, distinct operations, or aggregation **after effective paging** are rejected because OData's evaluation order would change the query's meaning. Repeated paging is normalized as an interval; negative skips are no-ops and nonpositive takes produce `$top=0`. Put final ordering and paging after grouping and aggregation. Ordering or expansion before grouping is unsupported.

Other unsupported constructs include joins, `SelectMany`, set operators, indexed lambdas, custom comparers or comparison overloads, custom functions/operators, conditional/coalescing expressions, nested object construction, parameterized POCO constructors, and GroupBy element-selector overloads. Project a grouping before filtering its aggregate results. Filtered aggregates within one group, nested aggregation in expansion, and combinations of expansion with grouping or computed projections are unsupported.

The generated expressions follow OData/server semantics for nulls, empty aggregates, string comparison, rounding, and numeric promotion. The library does not materialize projected objects or reproduce LINQ-to-Objects exception behavior on the server. A scalar `Select` still requests an OData property in the service's normal response shape.

## Build and test

Requires a .NET 10 SDK. The standard test project restores xUnit and Microsoft ASP.NET Core OData packages; those dependencies are confined to tests.

```text
dotnet build Olam.slnx -c Release
dotnet test Olam.slnx -c Release
dotnet pack src/Olam/Olam.csproj -c Release -o artifacts
```

The test suite includes shared translation regression scenarios, EDM parsing checks, and server query-option application against seeded POCO collections. Server tests compare results to equivalent LINQ without an HTTP server or an HttpClient.

For an environment without NuGet access, run the same translation scenarios using the dependency-free runner:

```text
dotnet run --project tests/Olam.Tests -c Release -p:OfflineTests=true
```

That mode excludes xUnit discovery, Microsoft OData parsing, and server compatibility tests. It does not establish server compatibility. On restricted Windows hosts that cannot launch MSBuild worker/compiler servers, add `-m:1 -nr:false -p:UseSharedCompilation=false` to build commands.
