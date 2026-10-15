namespace Olam;

internal sealed class QueryModel(Type rootType)
{
    internal Binding Current { get; set; } = new PathBinding(rootType, "");
    internal List<string> Apply { get; } = [];
    internal List<string> Filters { get; } = [];
    internal List<string> OrderBy { get; } = [];
    internal List<string> Select { get; } = [];
    internal SortedDictionary<string, string> Expand { get; } = new(StringComparer.Ordinal);
    internal bool IncludeCount { get; set; }
    internal long Skip { get; set; }
    internal long? Top { get; set; }
    internal bool HasPaging => Skip > 0 || Top.HasValue;
    internal GroupBinding? PendingGroup { get; set; }
    internal List<string> GroupKeys { get; } = [];

    internal void FlushFilters()
    {
        if (Filters.Count == 0) return;
        Apply.Add("filter(" + string.Join(" and ", Filters) + ")");
        Filters.Clear();
    }

    internal void AddTransformation(string transformation)
    {
        FlushFilters();
        Apply.Add(transformation);
    }

    internal ODataQueryResult Render()
    {
        var result = new Dictionary<string, string>();
        if (Apply.Count > 0) result.Add("$apply", string.Join('/', Apply));
        if (Filters.Count > 0) result.Add("$filter", string.Join(" and ", Filters));
        if (OrderBy.Count > 0) result.Add("$orderby", string.Join(',', OrderBy));
        if (Skip > 0) result.Add("$skip", Skip.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (Top.HasValue) result.Add("$top", Top.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (Select.Count > 0) result.Add("$select", string.Join(',', Select.Distinct(StringComparer.Ordinal)));
        if (Expand.Count > 0) result.Add("$expand", string.Join(',', Expand.Values));
        if (IncludeCount) result.Add("$count", "true");
        return new ODataQueryResult(result);
    }
}
