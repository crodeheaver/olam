using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OData;
using Microsoft.AspNetCore.OData.Extensions;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.AspNetCore.OData.Query.Wrapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OData.Edm;
using Microsoft.OData.ModelBuilder;
using Microsoft.OData.UriParser;

namespace Olam.Tests;

public sealed class ServerCompatibilityTests
{
    private static IQueryable<ServiceProduct> Products => ODataQuery.For<ServiceProduct>();

    private static IEnumerable<(string Name, Func<ODataQueryResult> Query)> ParserCases
    {
        get
        {
            yield return ("core", () => Products.Where(p => p.Active && p.Price > 5).OrderBy(p => p.Name).Skip(1).Take(3)
                .Select(p => new { p.Id, p.Name }).IncludeCount().ToODataQuery());
            yield return ("strings", () => Products.Where(p => p.Name.Trim().ToLower().Contains("a") && p.Name.Length > 1).ToODataQuery());
            yield return ("escaping", () => Products.Where(p => p.Name == "O'Brien & café? + %20").ToODataQuery());
            yield return ("enum", () => Products.Where(p => p.Status == ProductStatus.Active).ToODataQuery());
            yield return ("date", () => Products.Where(p => p.CreatedAt.Year == 2026).ToODataQuery());
            yield return ("nullable", () => Products.Where(p => p.Stock.HasValue && p.Stock.Value > 0).ToODataQuery());
            yield return ("membership", () => Products.Where(p => new[] { 1, 2 }.Contains(p.Id)).ToODataQuery());
            yield return ("any", () => Products.Where(p => p.Orders.Any(o => o.Amount > p.Price)).ToODataQuery());
            yield return ("nested-any", () => Products.Where(p => p.Orders.Any(o => o.Lines.Any(l => l.Amount > o.Amount))).ToODataQuery());
            yield return ("primitive-contains", () => Products.Where(p => p.Tags.Contains(p.Name)).ToODataQuery());
            yield return ("expand", () => Products.Expand(p => p.Orders, q => q.Where(o => o.Quantity > 0).OrderByDescending(o => o.Amount)
                .Skip(1).Take(2).Select(o => new { o.Id, o.Amount }).IncludeCount()).ToODataQuery());
            yield return ("nested-expand", () => Products.Expand(p => p.Orders, q => q.Expand(o => o.Lines, lines => lines.Take(2))).ToODataQuery());
            yield return ("computed", () => Products.Select(p => new { p.Id, Gross = p.Price * 2 }).Where(p => p.Gross > 10).ToODataQuery());
            yield return ("computed-chain", () => Products.Select(p => new { Gross = p.Price * 2 }).Where(p => p.Gross > 20)
                .Select(p => new { Net = p.Gross / 2 }).ToODataQuery());
            yield return ("grouped", () => Products.Where(p => p.Active).GroupBy(p => p.Category)
                .Select(g => new { Category = g.Key, Total = g.Sum(p => p.Price), Rows = g.Count() }).Where(g => g.Total > 5)
                .OrderByDescending(g => g.Total).Take(3).ToODataQuery());
            yield return ("computed-input", () => Products.GroupBy(p => p.Category)
                .Select(g => new { Category = g.Key, Revenue = g.Sum(p => p.Price * 2) }).ToODataQuery());
            yield return ("computed-output", () => Products.GroupBy(p => p.Category)
                .Select(g => new { Category = g.Key, Doubled = g.Sum(p => p.Price) * 2 }).ToODataQuery());
            yield return ("computed-key", () => Products.GroupBy(p => p.CreatedAt.Year).Select(g => new { Year = g.Key, Rows = g.Count() }).ToODataQuery());
            yield return ("count-distinct", () => Products.GroupBy(p => p.Category)
                .Select(g => new { Category = g.Key, Kinds = g.Select(p => p.Name).Distinct().Count() }).ToODataQuery());
            yield return ("distinct", () => Products.Select(p => p.Category).Distinct().ToODataQuery());
            yield return ("scalar", () => Products.ToODataQuery(q => q.Sum(p => p.Price)));
        }
    }

    public static IEnumerable<object[]> ParserNames => ParserCases.Select(c => new object[] { c.Name });

    [Theory]
    [MemberData(nameof(ParserNames))]
    public void Generated_options_parse_against_the_EDM(string name)
    {
        var result = ParserCases.Single(c => c.Name == name).Query();
        var model = Model();
        var set = model.EntityContainer.FindEntitySet("Products");
        var parser = new ODataQueryOptionParser(model, model.FindDeclaredType("Olam.Tests.ServiceProduct"), set,
            new Dictionary<string, string>(result.QueryOptions));
        // Applying transformations first registers computed and aggregate output properties for later options.
        if (result.QueryOptions.ContainsKey("$apply")) Assert.NotNull(parser.ParseApply());
        if (result.QueryOptions.ContainsKey("$filter")) Assert.NotNull(parser.ParseFilter());
        if (result.QueryOptions.ContainsKey("$orderby")) Assert.NotNull(parser.ParseOrderBy());
        if (result.QueryOptions.ContainsKey("$select") || result.QueryOptions.ContainsKey("$expand")) Assert.NotNull(parser.ParseSelectAndExpand());
        if (result.QueryOptions.ContainsKey("$skip")) Assert.NotNull(parser.ParseSkip());
        if (result.QueryOptions.ContainsKey("$top")) Assert.NotNull(parser.ParseTop());
        if (result.QueryOptions.ContainsKey("$count")) Assert.NotNull(parser.ParseCount());
    }

    [Fact]
    public void Server_filter_order_and_paging_match_LINQ()
    {
        var query = Products.Where(p => p.Active && p.Price >= 10).OrderByDescending(p => p.Price).ThenBy(p => p.Id).Skip(1).Take(2);
        var actual = Apply(query.ToODataQuery()).Cast<ServiceProduct>().Select(p => p.Id).ToArray();
        var expected = Data().Where(p => p.Active && p.Price >= 10).OrderByDescending(p => p.Price).ThenBy(p => p.Id).Skip(1).Take(2).Select(p => p.Id).ToArray();
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Server_correlated_collection_filter_matches_LINQ()
    {
        var query = Products.Where(p => p.Orders.Any(o => o.Amount > p.Price)).OrderBy(p => p.Id);
        var actual = Apply(query.ToODataQuery()).Cast<ServiceProduct>().Select(p => p.Id).ToArray();
        var expected = Data().Where(p => p.Orders.Any(o => o.Amount > p.Price)).OrderBy(p => p.Id).Select(p => p.Id).ToArray();
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Server_computed_projection_matches_LINQ()
    {
        var query = Products.Where(p => p.Active).Select(p => new { p.Id, Gross = p.Price * 2 }).Where(p => p.Gross > 25).OrderBy(p => p.Id);
        var actual = Apply(query.ToODataQuery()).Cast<object>().Select(Row).Select(r => (Id: Integer(r["Id"]), Gross: Number(r["Gross"]))).ToArray();
        var expected = Data().Where(p => p.Active).Select(p => new { p.Id, Gross = p.Price * 2 }).Where(p => p.Gross > 25).OrderBy(p => p.Id)
            .Select(p => (p.Id, p.Gross)).ToArray();
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Server_grouping_aggregates_and_post_filter_match_LINQ()
    {
        var query = Products.Where(p => p.Active).GroupBy(p => p.Category)
            .Select(g => new { Category = g.Key, Total = g.Sum(p => p.Price), Rows = g.Count() })
            .Where(g => g.Total > 15).OrderBy(g => g.Category);
        var actual = Apply(query.ToODataQuery()).Cast<object>().Select(Row)
            .Select(r => (Category: (string)r["Category"], Total: Number(r["Total"]), Rows: Integer(r["Rows"]))).ToArray();
        var expected = Data().Where(p => p.Active).GroupBy(p => p.Category)
            .Select(g => (Category: g.Key, Total: g.Sum(p => p.Price), Rows: g.Count()))
            .Where(g => g.Total > 15).OrderBy(g => g.Category).ToArray();
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Server_scalar_aggregate_matches_LINQ()
    {
        var result = Products.Where(p => p.Active).ToODataQuery(q => q.Sum(p => p.Price));
        var actual = Row(Assert.Single(Apply(result).Cast<object>()));
        Assert.Equal(Data().Where(p => p.Active).Sum(p => p.Price), Number(actual["Value"]));
    }

    [Fact]
    public void Server_count_distinct_matches_LINQ()
    {
        var query = Products.GroupBy(p => p.Category)
            .Select(g => new { Category = g.Key, Kinds = g.Select(p => p.Name).Distinct().Count() }).OrderBy(g => g.Category);
        var actual = Apply(query.ToODataQuery()).Cast<object>().Select(Row)
            .Select(r => (Category: (string)r["Category"], Kinds: Integer(r["Kinds"]))).ToArray();
        var expected = Data().GroupBy(p => p.Category).Select(g => (Category: g.Key, Kinds: g.Select(p => p.Name).Distinct().Count()))
            .OrderBy(g => g.Category).ToArray();
        Assert.Equal(expected, actual);
    }

    private static IEdmModel Model()
    {
        var builder = new ODataConventionModelBuilder { Namespace = "Olam.Tests" };
        builder.EntitySet<ServiceProduct>("Products");
        builder.EntitySet<Order>("Orders");
        builder.EntitySet<OrderLine>("OrderLines");
        builder.EntitySet<Supplier>("Suppliers");
        return builder.GetEdmModel();
    }

    private static object[] Apply(ODataQueryResult result)
    {
        var model = Model();
        var set = model.EntityContainer.FindEntitySet("Products");
        var path = new ODataPath(new EntitySetSegment(set));
        using var services = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        http.Request.Method = "GET";
        http.Request.QueryString = new QueryString(result.ToQueryString());
        http.Request.ODataFeature().Model = model;
        http.Request.ODataFeature().Path = path;
        var context = new ODataQueryContext(model, typeof(ServiceProduct), path);
        var options = new ODataQueryOptions<ServiceProduct>(context, http.Request);
        var settings = new ODataQuerySettings { HandleNullPropagation = HandleNullPropagationOption.True, EnsureStableOrdering = false };
        return options.ApplyTo(Data().AsQueryable(), settings).Cast<object>().ToArray();
    }

    private static IDictionary<string, object> Row(object value) => value is ISelectExpandWrapper selected ? selected.ToDictionary()
        : Assert.IsAssignableFrom<IDictionary<string, object>>(value.GetType().GetProperty("Values")?.GetValue(value));

    private static decimal Number(object value) => Convert.ToDecimal(value, CultureInfo.InvariantCulture);
    private static int Integer(object value) => Convert.ToInt32(value, CultureInfo.InvariantCulture);

    private static ServiceProduct[] Data() =>
    [
        new() { Id = 1, Name = "Apple", Category = "Food", Price = 10, Active = true, Orders = [new() { Id = 1, Amount = 12, Quantity = 2 }] },
        new() { Id = 2, Name = "Bread", Category = "Food", Price = 20, Active = true, Orders = [new() { Id = 2, Amount = 10, Quantity = 1 }] },
        new() { Id = 3, Name = "Pen", Category = "Office", Price = 40, Active = true, Orders = [new() { Id = 3, Amount = 50, Quantity = 4 }] },
        new() { Id = 4, Name = "Apple", Category = "Food", Price = 90, Active = false },
        new() { Id = 5, Name = "Pen", Category = "Office", Price = 5, Active = true }
    ];
}

public sealed class ServiceProduct
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public decimal Price { get; set; }
    public bool Active { get; set; }
    public int? Stock { get; set; }
    public ProductStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public List<Order> Orders { get; set; } = [];
    public List<string> Tags { get; set; } = [];
}
