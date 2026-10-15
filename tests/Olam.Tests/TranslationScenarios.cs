using System.Globalization;
using System.Linq.Expressions;
using System.Text.Json;

namespace Olam.Tests;

// These behavioral scenarios run unchanged under xUnit and the dependency-free offline runner.
internal static class TranslationScenarios
{
    private static IQueryable<Product> Products => ODataQuery.For<Product>();

    internal static IEnumerable<(string Name, Action Check)> Cases
    {
        get
        {
            yield return ("Empty root", () => Equal("", Products.ToQueryString()));
            yield return ("Filter order page select", () => Options(Products.Where(p => p.Price >= 25m && p.Name.StartsWith("A"))
                .OrderBy(p => p.Name).Skip(10).Take(20).Select(p => new { p.Id, p.Name }),
                ("$filter", "((Price ge 25) and startswith(Name,'A'))"), ("$orderby", "Name"), ("$skip", "10"), ("$top", "20"), ("$select", "Id,Name")));
            yield return ("Query comprehension", () => Options(from p in Products where p.Active orderby p.Price descending, p.Name select new { p.Id, p.Name },
                ("$filter", "Active"), ("$orderby", "Price desc,Name"), ("$select", "Id,Name")));
            yield return ("Repeated Where", () => Option(Products.Where(p => p.Id > 1).Where(p => p.Price < 10), "$filter", "(Id gt 1) and (Price lt 10)"));
            yield return ("Boolean precedence", () => Option(Products.Where(p => (p.Id == 1 || p.Id == 2) && !p.Active), "$filter", "(((Id eq 1) or (Id eq 2)) and not (Active))"));
            yield return ("Arithmetic precedence", () => Option(Products.Where(p => (p.Price + 1) * 2 > p.Price / 3), "$filter", "(((Price add 1) mul 2) gt (Price div 3))"));
            yield return ("Nullable access", () => Option(Products.Where(p => p.Stock.HasValue && p.Stock.Value > 0), "$filter", "((Stock ne null) and (Stock gt 0))"));
            yield return ("Null literal", () => Option(Products.Where(p => p.Stock == null), "$filter", "(Stock eq null)"));
            yield return ("Nested property", () => Option(Products.Where(p => p.Supplier.Address.City == "Austin"), "$filter", "(Supplier/Address/City eq 'Austin')"));
            yield return ("Apostrophe escaping", () => Option(Products.Where(p => p.Name == "O'Brien"), "$filter", "(Name eq 'O''Brien')"));
            yield return ("URI escape once", () =>
            {
                const string name = "O'Brien & café? # + %20 /";
                var result = Products.Where(p => p.Name == name).ToODataQuery();
                Equal("(Name eq 'O''Brien & café? # + %20 /')", result.QueryOptions["$filter"]);
                Equal("?$filter=" + Uri.EscapeDataString(result.QueryOptions["$filter"]), result.ToQueryString());
                Equal(result.QueryOptions["$filter"], Uri.UnescapeDataString(result.ToQueryString()[(result.ToQueryString().IndexOf('=') + 1)..]));
            });
            yield return ("String functions", () => Option(Products.Where(p => p.Name.Trim().ToLower().Contains("a") && p.Name.ToUpper().EndsWith("Z")),
                "$filter", "(contains(tolower(trim(Name)),'a') and endswith(toupper(Name),'Z'))"));
            yield return ("Substring and length", () => Option(Products.Where(p => p.Name.Substring(1, 2) == "ab" && p.Name.Length > 3),
                "$filter", "((substring(Name,1,2) eq 'ab') and (length(Name) gt 3))"));
            yield return ("String concatenation", () => Option(Products.Where(p => p.Name + "!" == "A!"), "$filter", "(concat(Name,'!') eq 'A!')"));
            yield return ("IsNullOrEmpty", () => Option(Products.Where(p => string.IsNullOrEmpty(p.Name)), "$filter", "(Name eq null or Name eq '')"));
            yield return ("Date and math functions", () => Option(Products.Where(p => p.CreatedAt.Year == 2026 && Math.Floor(p.Rating) >= 4),
                "$filter", "((year(CreatedAt) eq 2026) and (floor(Rating) ge 4))"));
            yield return ("Math rounding", () => Option(Products.Where(p => Math.Round(p.Rating) == 4 || Math.Ceiling(p.Rating) == 5),
                "$filter", "((round(Rating) eq 4) or (ceiling(Rating) eq 5))"));
            yield return ("Enum literal", () => Option(Products.Where(p => p.Status == ProductStatus.Active), "$filter", "(Status eq Olam.Tests.ProductStatus'Active')"));
            yield return ("Flags enum literal", () =>
            {
                var status = ProductStatus.Active | ProductStatus.Featured;
                Option(Products.Where(p => p.Status == status), "$filter", "(Status eq Olam.Tests.ProductStatus'Active,Featured')");
            });
            yield return ("Enum name mapping", () =>
            {
                var options = new ODataQueryOptions().MapEnum<ProductStatus>("Service.State");
                Option(ODataQuery.For<Product>(options).Where(p => p.Status == ProductStatus.Active), "$filter", "(Status eq Service.State'Active')");
            });
            yield return ("GUID literal", () =>
            {
                var id = Guid.Parse("a5e6b974-7660-4a8b-9135-d88628a093de");
                Option(Products.Where(p => p.Sku == id), "$filter", "(Sku eq a5e6b974-7660-4a8b-9135-d88628a093de)");
            });
            yield return ("DateTimeOffset literal", () =>
            {
                var date = new DateTimeOffset(2026, 10, 15, 12, 30, 0, TimeSpan.FromHours(-5));
                Option(Products.Where(p => p.CreatedAt >= date), "$filter", "(CreatedAt ge 2026-10-15T12:30:00.0000000-05:00)");
            });
            yield return ("Native date and time literals", () =>
            {
                var date = new DateOnly(2026, 10, 15);
                var time = new TimeOnly(9, 30);
                Option(Products.Where(p => p.IntroducedOn == date && p.OpeningTime == time), "$filter", "((IntroducedOn eq 2026-10-15) and (OpeningTime eq 09:30:00.0000000))");
            });
            yield return ("Duration literal", () =>
            {
                var duration = TimeSpan.FromMinutes(90);
                Option(Products.Where(p => p.Duration == duration), "$filter", "(Duration eq duration'PT1H30M')");
            });
            yield return ("Date-only comparison from DateTime member", () =>
            {
                var date = new DateTime(2026, 10, 15);
                Option(Products.Where(p => p.CreatedAt.Date == date), "$filter", "(date(CreatedAt) eq 2026-10-15)");
                Option(Products.Where(p => date == p.CreatedAt.Date), "$filter", "(2026-10-15 eq date(CreatedAt))");
            });
            yield return ("Date-only projected comparison", () =>
            {
                var date = new DateTime(2026, 10, 15);
                Option(Products.Select(p => new { Day = p.CreatedAt.Date }).Where(p => p.Day == date), "$filter", "(Day eq 2026-10-15)");
            });
            yield return ("UTC DateTime literal", () =>
            {
                var date = new DateTime(2026, 10, 15, 0, 0, 0, DateTimeKind.Utc);
                Option(Products.Where(p => p.UpdatedAt >= date), "$filter", "(UpdatedAt ge 2026-10-15T00:00:00.0000000+00:00)");
            });
            yield return ("Special floating point literals", () =>
            {
                var infinity = double.PositiveInfinity;
                var nan = double.NaN;
                Option(Products.Where(p => p.Rating < infinity || p.Rating == nan), "$filter", "((Rating lt INF) or (Rating eq NaN))");
            });
            yield return ("Numeric casts preserve type", () => Option(Products.Where(p => (long)p.Id > 5L), "$filter", "(cast(Id,Edm.Int64) gt 5)"));
            yield return ("Invariant culture", () =>
            {
                var prior = CultureInfo.CurrentCulture;
                try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR"); Option(Products.Where(p => p.Price > 12.5m), "$filter", "(Price gt 12.5)"); }
                finally { CultureInfo.CurrentCulture = prior; }
            });
            yield return ("Captured array membership", () =>
            {
                int[] ids = [1, 3, 5];
                Option(Products.Where(p => ids.Contains(p.Id)), "$filter", "(Id eq 1 or Id eq 3 or Id eq 5)");
            });
            yield return ("Inline array membership", () => Option(Products.Where(p => new[] { 1, 2 }.Contains(p.Id)), "$filter", "(Id eq 1 or Id eq 2)"));
            yield return ("Captured list membership", () =>
            {
                List<string> names = ["A", "O'Brien"];
                Option(Products.Where(p => names.Contains(p.Name)), "$filter", "(Name eq 'A' or Name eq 'O''Brien')");
            });
            yield return ("Captured HashSet membership", () =>
            {
                HashSet<int> ids = [1, 2];
                Option(Products.Where(p => ids.Contains(p.Id)), "$filter", "(Id eq 1 or Id eq 2)");
            });
            yield return ("Custom membership comparer rejected", () =>
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "a" };
                Reject(Products.Where(p => names.Contains(p.Name)));
            });
            yield return ("Empty membership", () =>
            {
                int[] ids = [];
                Option(Products.Where(p => ids.Contains(p.Id)), "$filter", "false");
            });
            yield return ("Any without predicate", () => Option(Products.Where(p => p.Orders.Any()), "$filter", "Orders/any()"));
            yield return ("Any predicate", () => Option(Products.Where(p => p.Orders.Any(o => o.Quantity > 2)), "$filter", "Orders/any(v0:(v0/Quantity gt 2))"));
            yield return ("All predicate", () => Option(Products.Where(p => p.Orders.All(o => o.Amount > 0)), "$filter", "Orders/all(v0:(v0/Amount gt 0))"));
            yield return ("Correlated Any", () => Option(Products.Where(p => p.Orders.Any(o => o.Amount > p.Price)), "$filter", "Orders/any(v0:(v0/Amount gt $it/Price))"));
            yield return ("Nested Any scoping", () => Option(Products.Where(p => p.Orders.Any(o => o.Lines.Any(l => l.Amount > o.Amount && l.Amount > p.Price))),
                "$filter", "Orders/any(v0:v0/Lines/any(v1:((v1/Amount gt v0/Amount) and (v1/Amount gt $it/Price))))"));
            yield return ("Primitive collection Contains", () => Option(Products.Where(p => p.Tags.Contains("featured")), "$filter", "Tags/any(v0:v0 eq 'featured')"));
            yield return ("Correlated Contains", () => Option(Products.Where(p => p.Tags.Contains(p.Name)), "$filter", "Tags/any(v0:v0 eq $it/Name)"));
            yield return ("Collection count property", () => Option(Products.Where(p => p.Orders.Count > 2), "$filter", "(Orders/$count gt 2)"));
            yield return ("Collection Count method", () => Option(Products.Where(p => p.Orders.Count() > 2), "$filter", "(Orders/$count gt 2)"));
            yield return ("Ordering and secondary ordering", () => Option(Products.OrderByDescending(p => p.Price).ThenBy(p => p.Category).ThenByDescending(p => p.Id),
                "$orderby", "Price desc,Category,Id desc"));
            yield return ("Replacement ordering", () => Option(Products.OrderBy(p => p.Name).OrderByDescending(p => p.Price), "$orderby", "Price desc"));
            yield return ("Repeated paging interval", () => Options(Products.Skip(5).Take(10).Skip(3).Take(4), ("$skip", "8"), ("$top", "4")));
            yield return ("Skip beyond Take", () => Options(Products.Take(3).Skip(10), ("$skip", "3"), ("$top", "0")));
            yield return ("Negative paging", () => Options(Products.Skip(-1).Take(-1), ("$top", "0")));
            yield return ("Paging overflow avoidance", () => Option(Products.Skip(int.MaxValue).Skip(int.MaxValue), "$skip", "4294967294"));
            yield return ("Identity selection", () => Equal("", Products.Select(p => p).ToQueryString()));
            yield return ("POCO selection", () => Option(Products.Select(p => new ProductView { Id = p.Id, Name = p.Name }), "$select", "Id,Name"));
            yield return ("Identity after projection", () => Option(Products.Select(p => new { p.Id, p.Name }).Select(p => p), "$select", "Id,Name"));
            yield return ("Scalar selection binding", () => Options(Products.Select(p => p.Price).Where(price => price > 10).OrderBy(price => price),
                ("$filter", "(Price gt 10)"), ("$orderby", "Price"), ("$select", "Price")));
            yield return ("Computed projection", () => Options(Products.Select(p => new { p.Id, Gross = p.Price * 1.2m }),
                ("$apply", "compute((Price mul 1.2) as Gross)"), ("$select", "Id,Gross")));
            yield return ("Computed projection binding", () => Options(Products.Select(p => new { Gross = p.Price * 2 }).Where(p => p.Gross > 50).OrderByDescending(p => p.Gross),
                ("$apply", "compute((Price mul 2) as Gross)"), ("$filter", "(Gross gt 50)"), ("$orderby", "Gross desc"), ("$select", "Gross")));
            yield return ("Property alias projection", () => Options(Products.Select(p => new { Label = p.Name }),
                ("$apply", "compute(Name as Label)"), ("$select", "Label")));
            yield return ("Composed projection pipeline", () => Option(Products.Select(p => new { Gross = p.Price * 2 }).Where(p => p.Gross > 20)
                .Select(p => new { Net = p.Gross / 2 }), "$apply", "compute((Price mul 2) as Gross)/filter((Gross gt 20))/compute((Gross div 2) as Net)"));
            yield return ("Scalar computed selection", () => Options(Products.Select(p => p.Price * 2),
                ("$apply", "compute((Price mul 2) as Value)"), ("$select", "Value")));
            yield return ("Include count", () => Option(Products.Where(p => p.Active).IncludeCount().Take(10), "$count", "true"));
            yield return ("Simple expansion", () => Option(Products.Expand(p => p.Orders), "$expand", "Orders"));
            yield return ("Nested expansion options", () => Option(Products.Expand(p => p.Orders, q => q.Where(o => o.Quantity > 0).OrderByDescending(o => o.Amount)
                .Skip(1).Take(3).Select(o => new { o.Id, o.Amount }).IncludeCount()), "$expand", "Orders($filter=(Quantity gt 0);$orderby=Amount desc;$skip=1;$top=3;$select=Id,Amount;$count=true)"));
            yield return ("Expansion inside expansion", () => Option(Products.Expand(p => p.Orders, q => q.Expand(o => o.Lines, lines => lines.Where(l => l.Amount > 5).Take(2))),
                "$expand", "Orders($expand=Lines($filter=(Amount gt 5);$top=2))"));
            yield return ("Singleton expansion", () => Option(Products.ExpandSingle(p => p.Supplier, q => q.Select(s => new { s.Id, s.Name })),
                "$expand", "Supplier($select=Id,Name)"));
            yield return ("Expansion deduplication", () => Option(Products.Expand(p => p.Orders).Expand(p => p.Orders), "$expand", "Orders"));
            yield return ("Grouped aggregates", () => Options(Products.Where(p => p.Active).GroupBy(p => p.Category)
                .Select(g => new { Category = g.Key, Total = g.Sum(p => p.Price), Count = g.Count() }),
                ("$apply", "filter(Active)/groupby((Category),aggregate(Price with sum as Total,$count as Count))"), ("$select", "Category,Total,Count")));
            yield return ("Filter and order grouped results", () => Options(Products.GroupBy(p => p.Category)
                .Select(g => new { Category = g.Key, Total = g.Sum(p => p.Price) }).Where(g => g.Total > 100).OrderByDescending(g => g.Total).Take(5),
                ("$apply", "groupby((Category),aggregate(Price with sum as Total))"), ("$filter", "(Total gt 100)"), ("$orderby", "Total desc"), ("$top", "5"), ("$select", "Category,Total")));
            yield return ("Composite grouping", () => Option(Products.GroupBy(p => new { p.Category, p.Active }).Select(g => new { g.Key.Category, g.Key.Active, Total = g.Sum(p => p.Price) }),
                "$apply", "groupby((Category,Active),aggregate(Price with sum as Total))"));
            yield return ("GroupBy result selector", () => Option(Products.GroupBy(p => p.Category, (category, items) => new { Category = category, Total = items.Sum(p => p.Price) }),
                "$apply", "groupby((Category),aggregate(Price with sum as Total))"));
            yield return ("POCO group projection", () => Option(Products.GroupBy(p => p.Category).Select(g => new CategorySummary { Category = g.Key, Total = g.Sum(p => p.Price), Count = g.Count() }),
                "$apply", "groupby((Category),aggregate(Price with sum as Total,$count as Count))"));
            yield return ("All aggregate methods", () => Option(Products.GroupBy(p => p.Category)
                .Select(g => new { g.Key, Smallest = g.Min(p => p.Price), Largest = g.Max(p => p.Price), Mean = g.Average(p => p.Price), Rows = g.LongCount() }),
                "$apply", "groupby((Category),aggregate(Price with min as Smallest,Price with max as Largest,Price with average as Mean,$count as Rows))/compute(Category as Key)"));
            yield return ("Count distinct", () => Option(Products.GroupBy(p => p.Category).Select(g => new { Category = g.Key, Kinds = g.Select(p => p.Name).Distinct().Count() }),
                "$apply", "groupby((Category),aggregate(Name with countdistinct as Kinds))"));
            yield return ("Constant grouping", () => Option(Products.GroupBy(p => 1).Select(g => new { Total = g.Sum(p => p.Price), Rows = g.Count() }),
                "$apply", "aggregate(Price with sum as Total,$count as Rows)"));
            yield return ("Computed grouping key", () => Option(Products.GroupBy(p => p.CreatedAt.Year).Select(g => new { Year = g.Key, Rows = g.Count() }),
                "$apply", "compute(year(CreatedAt) as __olam0)/groupby((__olam0),aggregate($count as Rows))/compute(__olam0 as Year)"));
            yield return ("Computed aggregate input", () => Option(Products.GroupBy(p => p.Category).Select(g => new { Category = g.Key, Revenue = g.Sum(p => p.Price * 2) }),
                "$apply", "compute((Price mul 2) as __olam0)/groupby((Category),aggregate(__olam0 with sum as Revenue))"));
            yield return ("Computed aggregate output", () => Option(Products.GroupBy(p => p.Category).Select(g => new { Category = g.Key, Doubled = g.Sum(p => p.Price) * 2 }),
                "$apply", "groupby((Category),aggregate(Price with sum as __olam0))/compute((__olam0 mul 2) as Doubled)"));
            yield return ("Distinct projection", () => Option(Products.Select(p => p.Category).Distinct(), "$apply", "groupby((Category))"));
            yield return ("Scalar Sum", () => Result(Products.ToODataQuery(q => q.Sum(p => p.Price)), ("$apply", "aggregate(Price with sum as Value)")));
            yield return ("Scalar Average", () => Result(Products.ToODataQuery(q => q.Average(p => p.Price)), ("$apply", "aggregate(Price with average as Value)")));
            yield return ("Scalar Min and Max", () =>
            {
                Result(Products.ToODataQuery(q => q.Min(p => p.Price)), ("$apply", "aggregate(Price with min as Value)"));
                Result(Products.ToODataQuery(q => q.Max(p => p.Price)), ("$apply", "aggregate(Price with max as Value)"));
            });
            yield return ("Scalar Count predicate", () => Result(Products.ToODataQuery(q => q.Count(p => p.Active)), ("$apply", "filter(Active)/aggregate($count as Value)")));
            yield return ("Scalar LongCount", () => Result(Products.ToODataQuery(q => q.LongCount()), ("$apply", "aggregate($count as Value)")));
            yield return ("Scalar capture composes source query", () => Result(Products.ToODataQuery(q => q.Where(p => p.Active).Sum(p => p.Price)),
                ("$apply", "filter(Active)/aggregate(Price with sum as Value)")));
            yield return ("Scalar selected Sum", () => Result(Products.Select(p => p.Price).ToODataQuery(q => q.Sum()), ("$apply", "aggregate(Price with sum as Value)")));
            yield return ("Scalar computed Sum", () => Result(Products.ToODataQuery(q => q.Sum(p => p.Price * 2)),
                ("$apply", "compute((Price mul 2) as __olam0)/aggregate(__olam0 with sum as Value)")));
            yield return ("Scalar distinct Count", () => Result(Products.Select(p => p.Category).Distinct().ToODataQuery(q => q.Count()),
                ("$apply", "groupby((Category))/aggregate($count as Value)")));
            yield return ("CLR names by default", () => Option(Products.Where(p => p.Name == "A"), "$filter", "(Name eq 'A')"));
            yield return ("JSON and camelCase names", () =>
            {
                var config = new ODataQueryOptions { UseJsonPropertyNames = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
                Options(ODataQuery.For<Product>(config).Where(p => p.Name == "A").Select(p => new { p.Id, p.Name }),
                    ("$apply", "filter((display_name eq 'A'))/compute(display_name as name)"), ("$select", "id,name"));
            });
            yield return ("Explicit mapping precedence", () =>
            {
                var config = new ODataQueryOptions { UseJsonPropertyNames = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }.MapProperty<Product, string>(p => p.Name, "title");
                Option(ODataQuery.For<Product>(config).Where(p => p.Name == "A"), "$filter", "(title eq 'A')");
            });
            yield return ("Snapshot configuration", () =>
            {
                var config = new ODataQueryOptions();
                var root = ODataQuery.For<Product>(config);
                config.MapProperty<Product, string>(p => p.Name, "title");
                Option(root.Where(p => p.Name == "A"), "$filter", "(Name eq 'A')");
            });
            yield return ("Captured values refreshed per conversion", () =>
            {
                var cutoff = 10m;
                var query = Products.Where(p => p.Price > cutoff);
                Option(query, "$filter", "(Price gt 10)");
                cutoff = 20m;
                Option(query, "$filter", "(Price gt 20)");
            });
            yield return ("Query reuse and immutable results", () =>
            {
                var root = Products.Where(p => p.Active);
                var result = root.Take(2).ToODataQuery();
                Equal("(Price gt 5)", root.Where(p => p.Price > 5).ToODataQuery().QueryOptions["$filter"].Split(" and ")[1]);
                Equal("2", result.QueryOptions["$top"]);
                Equal(false, root.ToODataQuery().QueryOptions.ContainsKey("$top"));
                Throws<NotSupportedException>(() => ((IDictionary<string, string>)result.QueryOptions).Add("$top", "3"));
            });
            yield return ("Parallel conversions", () =>
            {
                var query = Products.Where(p => p.Orders.Any(o => o.Quantity > 1)).Take(2);
                var expected = query.ToQueryString();
                Parallel.For(0, 40, _ => Equal(expected, query.ToQueryString()));
            });
            yield return ("Non-generic provider", () =>
            {
                var query = Products;
                var created = query.Provider.CreateQuery(query.Expression);
                Equal("", ((IQueryable<Product>)created).ToQueryString());
            });
            yield return ("Execution rejected", () =>
            {
                Throws<NotSupportedException>(() => Products.ToList());
                Throws<NotSupportedException>(() => Products.Count());
                Throws<NotSupportedException>(() => Products.Sum(p => p.Price));
            });
            yield return ("Unsupported ordering after paging", () => Reject(Products.Take(2).OrderBy(p => p.Name)));
            yield return ("Unsupported filtering after paging", () => Reject(Products.Take(2).Where(p => p.Price > 10)));
            yield return ("Unsupported grouping after paging", () => Reject(Products.Take(2).GroupBy(p => p.Category).Select(g => new { g.Key })));
            yield return ("Unsupported scalar aggregation after paging", () => Throws<ODataTranslationException>(() => Products.Take(2).ToODataQuery(q => q.Count())));
            yield return ("Unsupported custom method", () => Reject(Products.Where(p => CustomPredicate(p.Name))));
            yield return ("Unsupported string comparison overload", () => Reject(Products.Where(p => p.Name.Contains("a", StringComparison.OrdinalIgnoreCase))));
            yield return ("Unsupported indexed lambda", () => Reject(Products.Where((p, i) => p.Id == i)));
            yield return ("Unsupported custom comparer", () => Reject(Products.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)));
            yield return ("Unsupported Distinct comparer", () => Reject(Products.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase)));
            yield return ("Unsupported Join", () => Reject(Products.Join(Products, p => p.Id, p => p.Id, (a, b) => a)));
            yield return ("Unsupported SelectMany", () => Reject(Products.SelectMany(p => p.Orders)));
            yield return ("Unsupported set operator", () => Reject(Products.Union(Products)));
            yield return ("Unsupported bare GroupBy", () => Reject(Products.GroupBy(p => p.Category)));
            yield return ("Unsupported entity Distinct", () => Reject(Products.Distinct()));
            yield return ("Unsupported scalar capture", () => Throws<ODataTranslationException>(() => Products.ToODataQuery(q => q.First())));
            yield return ("Computed alias collision", () => Reject(Products.Select(p => new { Price = p.Price * 2 })));
            yield return ("Aggregate alias collision", () => Reject(Products.GroupBy(p => p.Category).Select(g => new { Category = g.Count() })));
            yield return ("Grouped filter count rejected", () => Reject(Products.GroupBy(p => p.Category).Select(g => new { Rows = g.Count(p => p.Active) })));
            yield return ("Group element selector rejected", () => Reject(Products.GroupBy(p => p.Category, p => p.Price).Select(g => new { g.Key })));
            yield return ("Expansion then computed projection rejected", () => Reject(Products.Expand(p => p.Orders).Select(p => new { Gross = p.Price * 2 })));
            yield return ("Computed projection then expansion rejected", () => Reject(Products.Select(p => new { p.Orders, Gross = p.Price * 2 }).Expand(p => p.Orders)));
            yield return ("Nested computation rejected", () => Reject(Products.Expand(p => p.Orders, orders => orders.Select(o => new { Revenue = o.Amount * 2 }))));
            yield return ("Enum ordering comparison rejected", () => Reject(Products.Where(p => (int)p.Status > 0)));
            yield return ("Conflicting expansion", () => Reject(Products.Expand(p => p.Orders).Expand(p => p.Orders, q => q.Take(2))));
            yield return ("Invalid singleton expansion option", () => Reject(Products.ExpandSingle(p => p.Supplier, q => q.Where(s => s.Id > 1))));
            yield return ("Invalid expand scalar", () => Reject(Products.Expand(p => p.Name)));
            yield return ("Unspecified DateTime rejected", () =>
            {
                var date = new DateTime(2026, 10, 15);
                Reject(Products.Where(p => p.UpdatedAt == date));
            });
            yield return ("Invalid identifier rejected", () => Throws<ArgumentException>(() => new ODataQueryOptions().MapProperty<Product, string>(p => p.Name, "Name&$top=1")));
            yield return ("Nested mapping selector rejected", () => Throws<ArgumentException>(() => new ODataQueryOptions().MapProperty<Product, string>(p => p.Supplier.Name, "Name")));
            yield return ("Foreign query rejected", () => Throws<ArgumentException>(() => new List<Product>().AsQueryable().ToQueryString()));
            yield return ("Translation exception identifies expression", () =>
            {
                var error = Throws<ODataTranslationException>(() => Products.Take(2).Where(p => p.Active).ToQueryString());
                Equal(true, error.Expression is MethodCallExpression);
                Equal(true, error.Message.Contains("Where", StringComparison.Ordinal));
            });
        }
    }

    private static bool CustomPredicate(string value) => value.Length > 1;

    private static void Option<T>(IQueryable<T> query, string key, string expected) => Equal(expected, query.ToODataQuery().QueryOptions[key]);
    private static void Options<T>(IQueryable<T> query, params (string Key, string Value)[] expected) => Result(query.ToODataQuery(), expected);
    private static void Result(ODataQueryResult result, params (string Key, string Value)[] expected)
    {
        Equal(expected.Length, result.QueryOptions.Count);
        foreach (var (key, value) in expected) Equal(value, result.QueryOptions.GetValueOrDefault(key));
    }
    private static void Reject<T>(IQueryable<T> query) => Throws<ODataTranslationException>(() => query.ToQueryString());
    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected <{expected}>; actual <{actual}>.");
    }
    private static T Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T error) { return error; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}
