using System.Collections;
using System.Linq.Expressions;

namespace Olam;

/// <summary>Creates query-only LINQ roots without a data source.</summary>
public static class ODataQuery
{
    /// <summary>Creates an immutable query root over a POCO type using a snapshot of the options.</summary>
    public static IQueryable<T> For<T>(ODataQueryOptions? options = null)
        => new ODataQueryable<T>(new ODataQueryProvider(typeof(T), (options ?? new()).Snapshot()));
}

internal interface IODataQueryRoot { ODataQueryProvider ODataProvider { get; } }

internal sealed class ODataQueryable<T> : IOrderedQueryable<T>, IODataQueryRoot
{
    internal ODataQueryable(ODataQueryProvider provider)
    {
        ODataProvider = provider;
        Expression = Expression.Constant(this);
    }

    public ODataQueryable(ODataQueryProvider provider, Expression expression)
    {
        ODataProvider = provider;
        Expression = expression;
    }

    public ODataQueryProvider ODataProvider { get; }
    public Type ElementType => typeof(T);
    public Expression Expression { get; }
    public IQueryProvider Provider => ODataProvider;
    public IEnumerator<T> GetEnumerator() => throw ODataQueryProvider.ExecutionError();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public override string ToString() => this.ToQueryString();
}

internal sealed class ODataQueryProvider(Type rootType, ODataQueryOptions options) : IQueryProvider
{
    internal Type RootType { get; } = rootType;
    internal ODataQueryOptions Options { get; } = options;

    public IQueryable CreateQuery(Expression expression)
    {
        var element = expression.Type.GetInterfaces().Append(expression.Type)
            .First(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IQueryable<>)).GetGenericArguments()[0];
        return (IQueryable)Activator.CreateInstance(typeof(ODataQueryable<>).MakeGenericType(element), this, expression)!;
    }
    public IQueryable<TElement> CreateQuery<TElement>(Expression expression) => new ODataQueryable<TElement>(this, expression);
    public object? Execute(Expression expression) => throw ExecutionError();
    public TResult Execute<TResult>(Expression expression) => throw ExecutionError();
    internal static NotSupportedException ExecutionError() => new(
        "Olam generates queries only. Use ToODataQuery() or ToQueryString(); capture scalar operations with ToODataQuery(q => q.Count()).");
}
