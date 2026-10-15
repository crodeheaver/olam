using System.Linq.Expressions;
using System.Reflection;

namespace Olam;

internal sealed class QueryTranslator
{
    private readonly ODataQueryProvider _provider;
    private readonly Type _rootType;
    private readonly ExpressionWriter _writer;
    private readonly HashSet<string> _reserved;
    private readonly HashSet<string> _dynamic = new(StringComparer.Ordinal);
    private int _aliasId;

    internal QueryTranslator(ODataQueryProvider provider, Type? rootType = null)
    {
        _provider = provider;
        _rootType = rootType ?? provider.RootType;
        _writer = new ExpressionWriter(provider.Options);
        _reserved = _rootType.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(p => provider.Options.Name(p)).ToHashSet(StringComparer.Ordinal);
    }

    internal ODataQueryResult Translate(Expression expression)
    {
        var model = Parse(expression);
        Finish(model, expression);
        return model.Render();
    }

    internal ODataQueryResult TranslateScalar(Expression expression)
    {
        if (expression is not MethodCallExpression call || !Expressions.IsLinq(call.Method) || !IsAggregate(call.Method.Name))
            throw Error("Capture one standalone Sum, Average, Min, Max, Count, or LongCount operation.", expression);
        var model = Parse(call.Arguments[0]);
        Finish(model, expression);
        RequireUnpaged(model, expression);
        if (model.Expand.Count > 0) throw Error("Aggregation cannot be combined with expansion.", expression);
        if (model.OrderBy.Count > 0) model.OrderBy.Clear();
        Binding? value = null;
        var count = call.Method.Name is "Count" or "LongCount";
        if (call.Arguments.Count == 2)
        {
            var selector = OneParameter(call.Arguments[1]);
            if (count) model.Filters.Add(_writer.Write(selector.Body, Scope(selector, model.Current)));
            else value = _writer.Bind(selector.Body, Scope(selector, model.Current));
        }
        else if (call.Arguments.Count != 1) throw Error("Unsupported aggregate overload.", expression);
        if (!count) value ??= model.Current;
        var aggregate = count ? "$count" : AggregateValue(value!, model, expression) + " with " + AggregateName(call.Method.Name);
        model.AddTransformation("aggregate(" + aggregate + " as Value)");
        model.Select.Clear();
        model.IncludeCount = false;
        return model.Render();
    }

    private QueryModel Parse(Expression expression, ParameterExpression? childRoot = null)
    {
        if (expression == childRoot) return new QueryModel(_rootType);
        if (expression is ConstantExpression { Value: IODataQueryRoot root } && root.ODataProvider == _provider)
            return new QueryModel(_rootType);
        if (expression is not MethodCallExpression call || call.Arguments.Count == 0
            || (call.Method.DeclaringType != typeof(Queryable) && call.Method.DeclaringType != typeof(ODataQueryableExtensions)))
            throw Error("Expected a query originating from ODataQuery.For<T>() and supported Queryable operators.", expression);
        var model = Parse(call.Arguments[0], childRoot);
        if (model.PendingGroup is not null && call.Method.Name != nameof(Queryable.Select))
            throw Error("Project a GroupBy using Select before applying further operators.", call);

        if (call.Method.DeclaringType == typeof(ODataQueryableExtensions))
        {
            switch (call.Method.Name)
            {
                case nameof(ODataQueryableExtensions.IncludeCount): model.IncludeCount = true; return model;
                case nameof(ODataQueryableExtensions.Expand):
                case nameof(ODataQueryableExtensions.ExpandSingle): Expand(model, call); return model;
                default: throw Error("Unsupported query extension.", call);
            }
        }

        switch (call.Method.Name)
        {
            case nameof(Queryable.Where):
                RequireArguments(call, 2);
                RequireUnpaged(model, call);
                var predicate = OneParameter(call.Arguments[1]);
                model.Filters.Add(_writer.Write(predicate.Body, Scope(predicate, model.Current)));
                break;
            case nameof(Queryable.OrderBy):
            case nameof(Queryable.OrderByDescending):
            case nameof(Queryable.ThenBy):
            case nameof(Queryable.ThenByDescending):
                RequireArguments(call, 2);
                RequireUnpaged(model, call);
                var order = OneParameter(call.Arguments[1]);
                if (call.Method.Name.StartsWith("OrderBy", StringComparison.Ordinal)) model.OrderBy.Clear();
                else if (model.OrderBy.Count == 0) throw Error("ThenBy requires a preceding OrderBy.", call);
                model.OrderBy.Add(_writer.Write(order.Body, Scope(order, model.Current))
                    + (call.Method.Name.EndsWith("Descending", StringComparison.Ordinal) ? " desc" : ""));
                break;
            case nameof(Queryable.Skip):
            case nameof(Queryable.Take):
                RequireArguments(call, 2);
                var amount = PagingAmount(call.Arguments[1]);
                if (call.Method.Name == nameof(Queryable.Take)) model.Top = Math.Min(model.Top ?? long.MaxValue, amount);
                else
                {
                    var consumed = model.Top.HasValue ? Math.Min(amount, model.Top.Value) : amount;
                    model.Skip = checked(model.Skip + consumed);
                    if (model.Top.HasValue) model.Top -= consumed;
                }
                break;
            case nameof(Queryable.Select):
                RequireArguments(call, 2);
                var selector = OneParameter(call.Arguments[1]);
                if (model.PendingGroup is { } group)
                    ProjectGroup(model, selector.Body, Scope(selector, group), call);
                else Project(model, selector.Body, Scope(selector, model.Current));
                break;
            case nameof(Queryable.GroupBy):
                Group(model, call);
                break;
            case nameof(Queryable.Distinct):
                RequireArguments(call, 1);
                RequireUnpaged(model, call);
                if (model.Select.Count == 0 || model.Expand.Count > 0 || model.OrderBy.Count > 0)
                    throw Error("Distinct requires a property projection without expansion or preceding ordering.", call);
                model.AddTransformation("groupby((" + string.Join(",", model.Select.Distinct(StringComparer.Ordinal)) + "))");
                break;
            case nameof(Queryable.AsQueryable):
                RequireArguments(call, 1);
                break;
            default:
                throw Error("Unsupported query operator or overload.", call);
        }
        return model;
    }

    private void Project(QueryModel model, Expression body, IReadOnlyDictionary<ParameterExpression, Binding> scope)
    {
        var computations = new List<string>();
        var selected = new List<string>();
        var binding = Projection(body, scope, (value, name, origin) => SelectValue(model, value, name, origin, computations, selected));
        if (computations.Count > 0 && model.Expand.Count > 0)
            throw Error("Computed projections cannot be combined with expansion.", body);
        if (computations.Count > 0) model.AddTransformation("compute(" + string.Join(",", computations) + ")");
        model.Current = binding;
        model.Select.Clear();
        model.Select.AddRange(selected);
    }

    private Binding Projection(Expression expression, IReadOnlyDictionary<ParameterExpression, Binding> scope,
        Func<Binding, string?, Expression, Binding> select)
    {
        if (expression is NewExpression { Members: { } members } construction)
        {
            var result = new Dictionary<string, Binding>(StringComparer.Ordinal);
            for (var i = 0; i < members.Count; i++)
            {
                var argument = construction.Arguments[i];
                result.Add(members[i].Name, select(_writer.Bind(argument, scope), _provider.Options.Name(members[i]), argument));
            }
            if (result.Count == 0) throw Error("Select at least one property.", expression);
            return new ObjectBinding(expression.Type, result);
        }
        if (expression is MemberInitExpression initializer && initializer.NewExpression.Arguments.Count == 0)
        {
            var result = new Dictionary<string, Binding>(StringComparer.Ordinal);
            foreach (var member in initializer.Bindings)
            {
                if (member is not MemberAssignment assignment) throw Error("Only simple POCO property assignments are supported.", expression);
                result.Add(member.Member.Name, select(_writer.Bind(assignment.Expression, scope), _provider.Options.Name(member.Member), assignment.Expression));
            }
            if (result.Count == 0) throw Error("Select at least one property.", expression);
            return new ObjectBinding(expression.Type, result);
        }
        var value = _writer.Bind(expression, scope);
        if (value is ObjectBinding existing)
            return new ObjectBinding(existing.Type, existing.Members.ToDictionary(p => p.Key, p => select(p.Value, null, expression), StringComparer.Ordinal));
        return select(value, null, expression);
    }

    private Binding SelectValue(QueryModel model, Binding value, string? name, Expression origin, List<string> computations, List<string> selected)
    {
        if (value is PathBinding { Path.Length: > 0 } path && (name is null || path.Path == name))
        {
            selected.Add(path.Path);
            return path;
        }
        if (value is PathBinding { Path.Length: 0 }) return value; // Identity selection retains all properties.
        if (value is not PathBinding and not ValueBinding) throw Error("Nested object projections are unsupported; use Expand for navigation properties.", origin);
        var alias = name ?? "Value";
        ReserveAlias(alias, origin);
        computations.Add(_writer.Text(value, origin) + " as " + alias);
        selected.Add(alias);
        return new PathBinding(value.Type, alias);
    }

    private void Group(QueryModel model, MethodCallExpression call)
    {
        RequireUnpaged(model, call);
        if (model.OrderBy.Count > 0 || model.Expand.Count > 0)
            throw Error("Group before applying ordering or expansion.", call);
        if (call.Arguments.Count is < 2 or > 3) throw Error("Unsupported GroupBy overload.", call);
        var keySelector = OneParameter(call.Arguments[1]);
        var scope = Scope(keySelector, model.Current);
        var computations = new List<string>();
        Binding key;
        if (ExpressionWriter.TryEvaluate(keySelector.Body, out var constant))
            key = new ValueBinding(keySelector.ReturnType, Literals.Format(constant, _provider.Options));
        else key = Projection(keySelector.Body, scope, (value, name, origin) =>
        {
            if (value is not PathBinding { Path.Length: > 0 } path)
            {
                var alias = InternalAlias();
                computations.Add(_writer.Text(value, origin) + " as " + alias);
                path = new PathBinding(value.Type, alias);
            }
            if (!model.GroupKeys.Contains(path.Path, StringComparer.Ordinal)) model.GroupKeys.Add(path.Path);
            return path;
        });
        if (computations.Count > 0) model.AddTransformation("compute(" + string.Join(",", computations) + ")");
        var groupingType = call.Type.GetGenericArguments()[0];
        var group = new GroupBinding(groupingType, key, model.Current);
        model.PendingGroup = group;
        model.Current = group;
        model.Select.Clear();
        if (call.Arguments.Count == 3)
        {
            var result = Expressions.Lambda(call.Arguments[2]);
            if (result.Parameters.Count != 2) throw Error("GroupBy element selectors and custom comparers are unsupported; use a result selector.", call);
            ProjectGroup(model, result.Body, new Dictionary<ParameterExpression, Binding>
            {
                [result.Parameters[0]] = key,
                [result.Parameters[1]] = group
            }, call);
        }
    }

    private void ProjectGroup(QueryModel model, Expression body, IReadOnlyDictionary<ParameterExpression, Binding> scope, Expression origin)
    {
        var aggregates = new List<string>();
        var aggregateBindings = new Dictionary<MethodCallExpression, Binding>();
        var computations = new List<string>();
        var selected = new List<string>();
        string? preferredAlias = null;
        _writer.Aggregate = (call, innerScope) =>
        {
            if (!Expressions.IsLinq(call.Method) || !IsAggregate(call.Method.Name)) return null;
            if (aggregateBindings.TryGetValue(call, out var existing)) return existing;
            var source = call.Arguments[0];
            var distinct = false;
            if (source is MethodCallExpression distinctCall && Expressions.IsLinq(distinctCall.Method) && distinctCall.Method.Name == "Distinct")
            {
                RequireArguments(distinctCall, 1);
                distinct = true;
                source = distinctCall.Arguments[0];
            }
            LambdaExpression? elementSelector = null;
            if (source is MethodCallExpression selectCall && Expressions.IsLinq(selectCall.Method) && selectCall.Method.Name == "Select")
            {
                RequireArguments(selectCall, 2);
                elementSelector = OneParameter(selectCall.Arguments[1]);
                source = selectCall.Arguments[0];
            }
            if (_writer.Resolve(source, innerScope) is not GroupBinding group) return null;
            var count = call.Method.Name is "Count" or "LongCount";
            if (distinct && (!count || elementSelector is null))
                throw Error("Count distinct requires group.Select(x => x.Property).Distinct().Count().", call);
            Binding? value = elementSelector is null ? null : _writer.Bind(elementSelector.Body, Scope(elementSelector, group.Element));
            if (call.Arguments.Count == 2)
            {
                if (count) throw Error("Filtered group counts are unsupported. Filter the input before grouping.", call);
                var selector = OneParameter(call.Arguments[1]);
                value = _writer.Bind(selector.Body, Scope(selector, value ?? group.Element));
            }
            else if (call.Arguments.Count != 1) throw Error("Unsupported aggregate overload.", call);
            var alias = preferredAlias ?? InternalAlias();
            Names.Validate(alias);
            if (model.GroupKeys.Any(k => k == alias || k.StartsWith(alias + "/", StringComparison.Ordinal))
                || aggregates.Any(a => a.EndsWith(" as " + alias, StringComparison.Ordinal)))
                throw Error("An aggregate alias conflicts with a grouping key or another aggregate.", call);
            _dynamic.Add(alias);
            var aggregation = count && !distinct ? "$count"
                : AggregateValue(value ?? group.Element, model, call) + " with " + (distinct ? "countdistinct" : AggregateName(call.Method.Name));
            aggregates.Add(aggregation + " as " + alias);
            var binding = new PathBinding(call.Type, alias);
            aggregateBindings.Add(call, binding);
            return binding;
        };
        try
        {
            // Give directly projected aggregates their public output names; composed aggregates use private aliases.
            Binding BindSlot(Expression value, string? name)
            {
                preferredAlias = value is MethodCallExpression method && Expressions.IsLinq(method.Method) && IsAggregate(method.Method.Name) ? name : null;
                try { return _writer.Bind(value, scope); }
                finally { preferredAlias = null; }
            }
            Binding projected;
            if (body is NewExpression { Members: { } members } construction)
            {
                var slots = new Dictionary<string, Binding>(StringComparer.Ordinal);
                for (var i = 0; i < members.Count; i++)
                {
                    var name = _provider.Options.Name(members[i]);
                    slots.Add(members[i].Name, SelectValue(model, BindSlot(construction.Arguments[i], name), name,
                        construction.Arguments[i], computations, selected));
                }
                if (slots.Count == 0) throw Error("Select at least one grouping key or aggregate.", body);
                projected = new ObjectBinding(body.Type, slots);
            }
            else if (body is MemberInitExpression initializer && initializer.NewExpression.Arguments.Count == 0)
            {
                var slots = new Dictionary<string, Binding>(StringComparer.Ordinal);
                foreach (var item in initializer.Bindings)
                {
                    if (item is not MemberAssignment assignment) throw Error("Unsupported group projection assignment.", body);
                    var name = _provider.Options.Name(item.Member);
                    slots.Add(item.Member.Name, SelectValue(model, BindSlot(assignment.Expression, name), name,
                        assignment.Expression, computations, selected));
                }
                if (slots.Count == 0) throw Error("Select at least one grouping key or aggregate.", body);
                projected = new ObjectBinding(body.Type, slots);
            }
            else projected = SelectValue(model, BindSlot(body, "Value"), null, body, computations, selected);

            var aggregation = aggregates.Count > 0 ? "aggregate(" + string.Join(",", aggregates) + ")" : "";
            if (model.GroupKeys.Count > 0)
                model.AddTransformation("groupby((" + string.Join(",", model.GroupKeys) + ")" + (aggregation.Length > 0 ? "," + aggregation : "") + ")");
            else if (aggregation.Length > 0) model.AddTransformation(aggregation);
            else throw Error("Constant grouping requires at least one aggregate.", origin);
            if (computations.Count > 0) model.AddTransformation("compute(" + string.Join(",", computations) + ")");
            model.Select.Clear();
            model.Select.AddRange(selected);
            model.Current = projected;
            model.PendingGroup = null;
            model.GroupKeys.Clear();
        }
        finally { _writer.Aggregate = null; }
    }

    private string AggregateValue(Binding binding, QueryModel model, Expression origin)
    {
        if (!ExpressionWriter.IsPrimitive(Nullable.GetUnderlyingType(binding.Type) ?? binding.Type))
            throw Error("Aggregates require a primitive property or scalar expression.", origin);
        if (binding is PathBinding { Path.Length: > 0 } path) return path.Path;
        if (binding is not ValueBinding) throw Error("Aggregate a selected property or provide a selector.", origin);
        var alias = InternalAlias();
        model.AddTransformation("compute(" + _writer.Text(binding, origin) + " as " + alias + ")");
        return alias;
    }

    private void Expand(QueryModel model, MethodCallExpression call)
    {
        if (model.Apply.Count > 0) throw Error("Expansion cannot follow grouping or computed projections.", call);
        var selector = OneParameter(call.Arguments[1]);
        if (_writer.Resolve(selector.Body, Scope(selector, model.Current)) is not PathBinding { Path.Length: > 0 } path
            || ExpressionWriter.IsPrimitive(Nullable.GetUnderlyingType(selector.ReturnType) ?? selector.ReturnType))
            throw Error("Expand requires a navigation property path.", call);
        var expansion = path.Path;
        if (call.Arguments.Count == 3)
        {
            var configure = OneParameter(call.Arguments[2]);
            var element = configure.Parameters[0].Type.GetGenericArguments()[0];
            var childTranslator = new QueryTranslator(_provider, element);
            var child = childTranslator.Parse(configure.Body, configure.Parameters[0]);
            Finish(child, configure.Body);
            var options = child.Render().QueryOptions;
            if (options.ContainsKey("$apply")) throw Error("Nested expansion supports property selection, filtering, ordering, paging, count, and expansion; nested $apply is unsupported.", call);
            if (call.Method.Name == nameof(ODataQueryableExtensions.ExpandSingle) && options.Keys.Any(k => k is not "$select" and not "$expand"))
                throw Error("Singleton expansion permits only selection and further expansion.", call);
            if (options.Count > 0) expansion += "(" + string.Join(";", options.Select(p => p.Key + "=" + p.Value)) + ")";
        }
        if (model.Expand.TryGetValue(path.Path, out var prior) && prior != expansion)
            throw Error("Conflicting options for the same expansion path.", call);
        model.Expand[path.Path] = expansion;
    }

    private void ReserveAlias(string alias, Expression origin)
    {
        Names.Validate(alias);
        if (_reserved.Contains(alias) || !_dynamic.Add(alias))
            throw Error($"Computed alias '{alias}' conflicts with an existing property. Choose a different output member name.", origin);
    }

    private string InternalAlias()
    {
        string alias;
        do { alias = "__olam" + _aliasId++; } while (_reserved.Contains(alias) || _dynamic.Contains(alias));
        _dynamic.Add(alias);
        return alias;
    }

    private static Dictionary<ParameterExpression, Binding> Scope(LambdaExpression lambda, Binding binding)
        => new() { [lambda.Parameters[0]] = binding };

    private static LambdaExpression OneParameter(Expression expression)
    {
        var lambda = Expressions.Lambda(expression);
        if (lambda.Parameters.Count != 1) throw Error("Indexed lambdas and custom comparers are unsupported.", expression);
        return lambda;
    }

    private static long PagingAmount(Expression expression)
        => ExpressionWriter.TryEvaluate(expression, out var value) && value is int amount ? Math.Max(0, amount)
            : throw Error("Skip/Take requires a captured integer count; Range overloads are unsupported.", expression);

    private static void RequireArguments(MethodCallExpression call, int count)
    {
        if (call.Arguments.Count != count) throw Error("Unsupported operator overload.", call);
    }

    private static void RequireUnpaged(QueryModel model, Expression expression)
    {
        if (model.HasPaging) throw Error("This operation after Skip/Take cannot preserve LINQ operation order. Apply filtering, ordering, and aggregation before paging.", expression);
    }

    private static void Finish(QueryModel model, Expression expression)
    {
        if (model.PendingGroup is not null) throw Error("Project a GroupBy with Select before generating the query.", expression);
    }

    private static bool IsAggregate(string name) => name is "Count" or "LongCount" or "Sum" or "Average" or "Min" or "Max";
    private static string AggregateName(string name) => name switch
    {
        "Sum" => "sum", "Average" => "average", "Min" => "min", "Max" => "max",
        _ => throw new InvalidOperationException("Not a value aggregate.")
    };

    private static ODataTranslationException Error(string message, Expression expression) => new(message, expression);
}
