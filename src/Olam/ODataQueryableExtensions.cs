using System.Linq.Expressions;
using System.Reflection;

namespace Olam;

/// <summary>Converts captured queries and adds OData-specific query options.</summary>
public static class ODataQueryableExtensions
{
    /// <summary>Translates a query without enumerating or executing it.</summary>
    public static ODataQueryResult ToODataQuery<T>(this IQueryable<T> query)
    {
        var provider = Provider(query);
        return new QueryTranslator(provider).Translate(query.Expression);
    }

    /// <summary>Captures a standalone Sum, Average, Min, Max, Count, or LongCount operation as an aggregate query.</summary>
    public static ODataQueryResult ToODataQuery<T, TResult>(this IQueryable<T> query,
        Expression<Func<IQueryable<T>, TResult>> aggregate)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        var provider = Provider(query);
        var body = new Substitute(aggregate.Parameters[0], query.Expression).Visit(aggregate.Body)!;
        return new QueryTranslator(provider).TranslateScalar(body);
    }

    /// <summary>Translates and renders a URI-escaped query string, including the leading ?.</summary>
    public static string ToQueryString<T>(this IQueryable<T> query) => query.ToODataQuery().ToQueryString();

    /// <summary>Requests the matching collection count alongside the results using $count=true.</summary>
    public static IQueryable<T> IncludeCount<T>(this IQueryable<T> query)
        => Marker(query, (MethodInfo)MethodBase.GetCurrentMethod()!, [typeof(T)]);

    /// <summary>Expands a navigation property. Paths through singleton navigation properties are supported.</summary>
    public static IQueryable<T> Expand<T, TNavigation>(this IQueryable<T> query, Expression<Func<T, TNavigation>> navigation)
    {
        ArgumentNullException.ThrowIfNull(navigation);
        return Marker(query, (MethodInfo)MethodBase.GetCurrentMethod()!, [typeof(T), typeof(TNavigation)], Expression.Quote(navigation));
    }

    /// <summary>Expands a collection with a captured query describing its nested query options.</summary>
    public static IQueryable<T> Expand<T, TElement, TResult>(this IQueryable<T> query,
        Expression<Func<T, IEnumerable<TElement>>> navigation,
        Expression<Func<IQueryable<TElement>, IQueryable<TResult>>> configure)
    {
        ArgumentNullException.ThrowIfNull(navigation);
        ArgumentNullException.ThrowIfNull(configure);
        return Marker(query, (MethodInfo)MethodBase.GetCurrentMethod()!, [typeof(T), typeof(TElement), typeof(TResult)],
            Expression.Quote(navigation), Expression.Quote(configure));
    }

    /// <summary>Expands a singleton with a captured query limited to selection and further expansion.</summary>
    public static IQueryable<T> ExpandSingle<T, TNavigation, TResult>(this IQueryable<T> query,
        Expression<Func<T, TNavigation>> navigation,
        Expression<Func<IQueryable<TNavigation>, IQueryable<TResult>>> configure)
    {
        ArgumentNullException.ThrowIfNull(navigation);
        ArgumentNullException.ThrowIfNull(configure);
        return Marker(query, (MethodInfo)MethodBase.GetCurrentMethod()!, [typeof(T), typeof(TNavigation), typeof(TResult)],
            Expression.Quote(navigation), Expression.Quote(configure));
    }

    private static IQueryable<T> Marker<T>(IQueryable<T> query, MethodInfo method, Type[] arguments, params Expression[] extra)
    {
        Provider(query);
        return query.Provider.CreateQuery<T>(Expression.Call(null, method.GetGenericMethodDefinition().MakeGenericMethod(arguments),
            new[] { query.Expression }.Concat(extra)));
    }

    private static ODataQueryProvider Provider<T>(IQueryable<T> query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return query.Provider as ODataQueryProvider ?? throw new ArgumentException("The query must originate from ODataQuery.For<T>().", nameof(query));
    }
}

internal sealed class Substitute(Expression from, Expression to) : ExpressionVisitor
{
    public override Expression? Visit(Expression? node) => node == from ? to : base.Visit(node);
}
