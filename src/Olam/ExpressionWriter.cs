using System.Collections;
using System.Linq.Expressions;
using System.Reflection;

namespace Olam;

internal sealed class ExpressionWriter(ODataQueryOptions options)
{
    private int _lambdaId;
    private int _lambdaDepth;
    internal Func<MethodCallExpression, IReadOnlyDictionary<ParameterExpression, Binding>, Binding?>? Aggregate { get; set; }

    internal string Write(Expression expression, IReadOnlyDictionary<ParameterExpression, Binding> scope)
    {
        if (Resolve(expression, scope) is { } binding) return Text(binding, expression);
        if (TryEvaluate(expression, out var value)) return Literal(value, expression);

        return expression switch
        {
            BinaryExpression binary => Binary(binary, scope),
            UnaryExpression { NodeType: ExpressionType.Not } unary when unary.Type == typeof(bool) || unary.Type == typeof(bool?)
                => "not (" + Write(unary.Operand, scope) + ")",
            UnaryExpression { NodeType: ExpressionType.Negate or ExpressionType.NegateChecked } unary
                => "-(" + Write(unary.Operand, scope) + ")",
            UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary => ConvertValue(unary, scope),
            MethodCallExpression call => Call(call, scope),
            _ => throw Error("Unsupported scalar expression.", expression)
        };
    }

    internal Binding Bind(Expression expression, IReadOnlyDictionary<ParameterExpression, Binding> scope)
        => Resolve(expression, scope) ?? new ValueBinding(expression.Type, Write(expression, scope));

    internal Binding? Resolve(Expression expression, IReadOnlyDictionary<ParameterExpression, Binding> scope)
    {
        if (expression is ParameterExpression parameter) return scope.GetValueOrDefault(parameter);
        if (expression is UnaryExpression unary && Transparent(unary)) return Resolve(unary.Operand, scope);
        if (expression is MethodCallExpression call && Aggregate is not null)
            return Aggregate(call, scope);
        if (expression is not MemberExpression { Expression: { } target } member) return null;
        var parent = Resolve(target, scope);
        if (parent is ObjectBinding projection)
            return projection.Members.TryGetValue(member.Member.Name, out var selected) ? selected
                : throw Error("The member is not part of the preceding projection.", member);
        if (parent is GroupBinding grouping)
            return member.Member.Name == "Key" ? grouping.Key : throw Error("Only a grouping's Key can be read directly.", member);
        if (parent is not PathBinding and not ValueBinding) return null;
        var type = Nullable.GetUnderlyingType(target.Type) ?? target.Type;
        if (Nullable.GetUnderlyingType(target.Type) is not null)
        {
            if (member.Member.Name == "Value") return parent is PathBinding path
                ? new PathBinding(member.Type, path.Path, path.RootRelative) : new ValueBinding(member.Type, Text(parent, member));
            if (member.Member.Name == "HasValue") return new ValueBinding(member.Type, "(" + Text(parent, member) + " ne null)");
        }
        if (type == typeof(string) && member.Member.Name == nameof(string.Length))
            return new ValueBinding(member.Type, "length(" + Text(parent, member) + ")");
        if (type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(DateOnly) || type == typeof(TimeOnly))
        {
            var function = member.Member.Name switch
            {
                "Year" => "year", "Month" => "month", "Day" => "day", "Hour" => "hour",
                "Minute" => "minute", "Second" => "second", "Date" => "date", _ => null
            };
            if (parent.Type == typeof(DateOnly) && function is "hour" or "minute" or "second")
                throw Error("An OData date-only value has no time component.", member);
            return function is null ? throw Error("Unsupported date/time member.", member)
                : new ValueBinding(function == "date" ? typeof(DateOnly) : member.Type, function + "(" + Text(parent, member) + ")");
        }
        if (member.Member.Name == "Count" && typeof(IEnumerable).IsAssignableFrom(target.Type))
            return new ValueBinding(member.Type, Text(parent, member) + "/$count");
        if (parent is PathBinding propertyPath && member.Member is PropertyInfo && !IsPrimitive(type))
        {
            var name = options.Name(member.Member);
            return new PathBinding(member.Type, propertyPath.Path.Length == 0 ? name : propertyPath.Path + "/" + name, propertyPath.RootRelative);
        }
        throw Error("Unsupported property access.", member);
    }

    internal string Text(Binding binding, Expression expression) => binding switch
    {
        PathBinding path => path.Path.Length == 0 ? "$it" : _lambdaDepth > 0 && path.RootRelative ? "$it/" + path.Path : path.Path,
        ValueBinding value => value.Text,
        _ => throw Error("An object or grouping cannot be used as an OData scalar value.", expression)
    };

    private string Binary(BinaryExpression binary, IReadOnlyDictionary<ParameterExpression, Binding> scope)
    {
        if (binary.Method is not null && binary.Method.DeclaringType != typeof(string)
            && binary.Method.DeclaringType != typeof(decimal) && binary.Method.DeclaringType != typeof(DateTime)
            && binary.Method.DeclaringType != typeof(DateTimeOffset) && binary.Method.DeclaringType != typeof(TimeSpan)
            && binary.Method.DeclaringType != typeof(Guid) && binary.Method.DeclaringType != typeof(DateOnly)
            && binary.Method.DeclaringType != typeof(TimeOnly))
            throw Error("Custom operator overloads cannot be translated.", binary);
        if (binary.NodeType == ExpressionType.Add && binary.Type == typeof(string))
            return "concat(" + Write(binary.Left, scope) + "," + Write(binary.Right, scope) + ")";
        var operation = binary.NodeType switch
        {
            ExpressionType.Equal => "eq", ExpressionType.NotEqual => "ne", ExpressionType.LessThan => "lt",
            ExpressionType.LessThanOrEqual => "le", ExpressionType.GreaterThan => "gt", ExpressionType.GreaterThanOrEqual => "ge",
            ExpressionType.AndAlso => "and", ExpressionType.OrElse => "or", ExpressionType.Add => "add",
            ExpressionType.Subtract => "sub", ExpressionType.Multiply => "mul", ExpressionType.Divide => "div",
            ExpressionType.Modulo => "mod", _ => throw Error("Unsupported binary operator.", binary)
        };
        var leftExpression = StripEnumConvert(binary.Left);
        var rightExpression = StripEnumConvert(binary.Right);
        if (binary.NodeType is not ExpressionType.Equal and not ExpressionType.NotEqual
            && ((Nullable.GetUnderlyingType(leftExpression.Type) ?? leftExpression.Type).IsEnum
                || (Nullable.GetUnderlyingType(rightExpression.Type) ?? rightExpression.Type).IsEnum))
            throw Error("Only equality and inequality comparisons of enum values are supported.", binary);
        var left = EnumOperand(leftExpression, rightExpression, scope);
        var right = EnumOperand(rightExpression, leftExpression, scope);
        return "(" + left + " " + operation + " " + right + ")";
    }

    private string EnumOperand(Expression expression, Expression other, IReadOnlyDictionary<ParameterExpression, Binding> scope)
    {
        var enumType = Nullable.GetUnderlyingType(other.Type) ?? other.Type;
        if (enumType.IsEnum && !expression.Type.IsEnum && TryEvaluate(expression, out var value) && value is not null)
            return Literal(Enum.ToObject(enumType, value), expression);
        if (Resolve(other, scope)?.Type == typeof(DateOnly) && TryEvaluate(expression, out var date) && date is DateTime dateTime)
            return Literal(DateOnly.FromDateTime(dateTime), expression);
        return Write(expression, scope);
    }

    private static Expression StripEnumConvert(Expression expression)
        => expression is UnaryExpression { NodeType: ExpressionType.Convert } unary
            && (Nullable.GetUnderlyingType(unary.Operand.Type) ?? unary.Operand.Type).IsEnum ? unary.Operand : expression;

    private string ConvertValue(UnaryExpression unary, IReadOnlyDictionary<ParameterExpression, Binding> scope)
    {
        if (unary.Method is not null && unary.Method.DeclaringType != typeof(decimal))
            throw Error("Custom conversions cannot be translated.", unary);
        if (Transparent(unary)) return Write(unary.Operand, scope);
        var edm = (Nullable.GetUnderlyingType(unary.Type) ?? unary.Type) switch
        {
            var t when t == typeof(byte) => "Byte", var t when t == typeof(sbyte) => "SByte",
            var t when t == typeof(short) => "Int16", var t when t == typeof(int) => "Int32",
            var t when t == typeof(long) => "Int64", var t when t == typeof(float) => "Single",
            var t when t == typeof(double) => "Double", var t when t == typeof(decimal) => "Decimal",
            _ => throw Error("Unsupported conversion target.", unary)
        };
        return "cast(" + Write(unary.Operand, scope) + ",Edm." + edm + ")";
    }

    private static bool Transparent(UnaryExpression unary)
        => unary.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked
            && (unary.Type == typeof(object) || unary.Type.IsAssignableFrom(unary.Operand.Type)
                || (Nullable.GetUnderlyingType(unary.Type) ?? unary.Type) == (Nullable.GetUnderlyingType(unary.Operand.Type) ?? unary.Operand.Type));

    private string Call(MethodCallExpression call, IReadOnlyDictionary<ParameterExpression, Binding> scope)
    {
        if (call.Method.DeclaringType == typeof(string)) return StringCall(call, scope);
        if (call.Method.DeclaringType == typeof(Math) && call.Arguments.Count == 1)
        {
            var function = call.Method.Name switch { "Round" => "round", "Floor" => "floor", "Ceiling" => "ceiling", _ => null };
            if (function is not null) return function + "(" + Write(call.Arguments[0], scope) + ")";
        }
        if (Expressions.IsLinq(call.Method) && call.Method.Name is "Any" or "All") return Quantifier(call, scope);
        if (Expressions.IsLinq(call.Method) && call.Method.Name is "Count" or "LongCount" && call.Arguments.Count == 1
            && Resolve(call.Arguments[0], scope) is PathBinding collection)
            return Text(collection, call) + "/$count";
        if (call.Method.Name == "Contains" && (((Expressions.IsLinq(call.Method) || call.Method.DeclaringType == typeof(MemoryExtensions)) && call.Arguments.Count == 2)
            || (call.Object is not null && typeof(IEnumerable).IsAssignableFrom(call.Object.Type) && call.Arguments.Count == 1
                && call.Method.DeclaringType?.Namespace is "System.Collections" or "System.Collections.Generic")))
            return Contains(call, scope);
        throw Error("Unsupported method or overload.", call);
    }

    private string StringCall(MethodCallExpression call, IReadOnlyDictionary<ParameterExpression, Binding> scope)
    {
        if (call.Object is null && call.Method.Name == "IsNullOrEmpty" && call.Arguments.Count == 1)
        {
            var value = Write(call.Arguments[0], scope);
            return "(" + value + " eq null or " + value + " eq '')";
        }
        if (call.Object is null && call.Method.Name == "Concat" && call.Arguments.Count == 2 && call.Arguments.All(a => a.Type == typeof(string)))
            return "concat(" + string.Join(',', call.Arguments.Select(a => Write(a, scope))) + ")";
        var function = call.Method.Name switch
        {
            "Contains" when call.Arguments.Count == 1 && call.Arguments[0].Type == typeof(string) => "contains",
            "StartsWith" when call.Arguments.Count == 1 && call.Arguments[0].Type == typeof(string) => "startswith",
            "EndsWith" when call.Arguments.Count == 1 && call.Arguments[0].Type == typeof(string) => "endswith",
            "ToLower" or "ToUpper" or "Trim" when call.Arguments.Count == 0 => call.Method.Name.ToLowerInvariant(),
            "Substring" when call.Arguments.Count is 1 or 2 => "substring",
            "Equals" when call.Arguments.Count == 1 && call.Arguments[0].Type == typeof(string) => "eq",
            _ => null
        };
        if (function is null || call.Object is null) throw Error("Unsupported string method or overload.", call);
        if (function == "eq") return "(" + Write(call.Object, scope) + " eq " + Write(call.Arguments[0], scope) + ")";
        return function + "(" + string.Join(',', new[] { call.Object }.Concat(call.Arguments).Select(a => Write(a, scope))) + ")";
    }

    private string Quantifier(MethodCallExpression call, IReadOnlyDictionary<ParameterExpression, Binding> scope)
    {
        if (call.Arguments.Count is < 1 or > 2 || Resolve(call.Arguments[0], scope) is not PathBinding path)
            throw Error("Any/All requires a collection property and an optional predicate.", call);
        var collection = Text(path, call);
        var operation = call.Method.Name.ToLowerInvariant();
        if (call.Arguments.Count == 1) return collection + "/any()";
        var predicate = Expressions.Lambda(call.Arguments[1]);
        if (predicate.Parameters.Count != 1) throw Error("Indexed predicates are unsupported.", call);
        var variable = "v" + _lambdaId++;
        var nested = new Dictionary<ParameterExpression, Binding>(scope)
        {
            [predicate.Parameters[0]] = new PathBinding(predicate.Parameters[0].Type, variable, false)
        };
        _lambdaDepth++;
        try { return collection + "/" + operation + "(" + variable + ":" + Write(predicate.Body, nested) + ")"; }
        finally { _lambdaDepth--; }
    }

    private string Contains(MethodCallExpression call, IReadOnlyDictionary<ParameterExpression, Binding> scope)
    {
        var source = call.Object ?? call.Arguments[0];
        // C# 14 prefers MemoryExtensions.Contains for arrays, introducing an implicit span conversion.
        if (call.Method.DeclaringType == typeof(MemoryExtensions)
            && source is MethodCallExpression { Method.Name: "op_Implicit", Arguments.Count: 1 } conversion
            && conversion.Method.DeclaringType is { IsGenericType: true } spanType
            && (spanType.GetGenericTypeDefinition() == typeof(Span<>) || spanType.GetGenericTypeDefinition() == typeof(ReadOnlySpan<>))
            && conversion.Arguments[0].Type.IsArray)
            source = conversion.Arguments[0];
        var item = call.Arguments[^1];
        if (TryEvaluate(source, out var value) && value is IEnumerable sequence)
        {
            var collectionType = value.GetType();
            if (collectionType.IsGenericType && collectionType.GetGenericTypeDefinition() == typeof(HashSet<>))
            {
                var comparer = collectionType.GetProperty("Comparer")!.GetValue(value);
                var defaultComparer = typeof(EqualityComparer<>).MakeGenericType(collectionType.GetGenericArguments())
                    .GetProperty("Default")!.GetValue(null);
                if (!ReferenceEquals(comparer, defaultComparer))
                    throw Error("Captured membership collections with custom comparers are unsupported.", call);
            }
            var itemText = Write(item, scope);
            var comparisons = sequence.Cast<object?>().Select(v => itemText + " eq " + Literal(v, source)).ToArray();
            return comparisons.Length == 0 ? "false" : "(" + string.Join(" or ", comparisons) + ")";
        }
        if (Resolve(source, scope) is PathBinding path)
        {
            var variable = "v" + _lambdaId++;
            var collection = Text(path, source);
            _lambdaDepth++;
            try { return collection + "/any(" + variable + ":" + variable + " eq " + Write(item, scope) + ")"; }
            finally { _lambdaDepth--; }
        }
        throw Error("Contains requires a captured collection or collection property.", call);
    }

    private string Literal(object? value, Expression expression)
    {
        try { return Literals.Format(value, options); }
        catch (NotSupportedException error) { throw Error(error.Message, expression); }
    }

    internal static bool TryEvaluate(Expression expression, out object? value)
    {
        switch (expression)
        {
            case ConstantExpression constant:
                value = constant.Value;
                return true;
            case MemberExpression { Expression: { } target } member when TryEvaluate(target, out var parent):
                value = member.Member switch
                {
                    FieldInfo field => field.GetValue(parent),
                    PropertyInfo property when property.GetIndexParameters().Length == 0 => property.GetValue(parent),
                    _ => null
                };
                return member.Member is FieldInfo or PropertyInfo;
            case UnaryExpression unary when Transparent(unary):
                return TryEvaluate(unary.Operand, out value);
            case NewArrayExpression { NodeType: ExpressionType.NewArrayInit } array:
                var values = new object?[array.Expressions.Count];
                for (var i = 0; i < values.Length; i++)
                    if (!TryEvaluate(array.Expressions[i], out values[i])) { value = null; return false; }
                value = values;
                return true;
            default:
                value = null;
                return false;
        }
    }

    internal static bool IsPrimitive(Type type)
        => type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) || type == typeof(Guid)
            || type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(DateOnly)
            || type == typeof(TimeOnly) || type == typeof(TimeSpan) || type == typeof(byte[]);

    private static ODataTranslationException Error(string message, Expression expression) => new(message, expression);
}
