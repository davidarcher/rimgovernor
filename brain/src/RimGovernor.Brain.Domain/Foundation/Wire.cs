using System.Reflection;
using System.Text;

namespace RimGovernor.Brain.Domain;

/// <summary>
/// Overrides the wire name of an enum member. Go's closed string sets (<c>Stage</c>, <c>ActionKind</c>, ...)
/// become enums; the string Go used stays available through <see cref="Wire"/> for logs, journals and
/// boundary mapping. Without the attribute the wire name is the snake_case of the member name.
/// </summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class WireNameAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

public static class Wire
{
    /// <summary>The Go string for <paramref name="value"/> (empty for the <c>None</c> member that stands for Go's empty string).</summary>
    public static string Name<TEnum>(TEnum value)
        where TEnum : struct, Enum => Table<TEnum>.ByValue.TryGetValue(value, out var name) ? name : value.ToString();

    /// <summary>Parses a Go string; false when it names no member.</summary>
    public static bool TryParse<TEnum>(string? name, out TEnum value)
        where TEnum : struct, Enum
    {
        if (name is not null && Table<TEnum>.ByName.TryGetValue(name, out value))
        {
            return true;
        }
        value = default;
        return false;
    }

    /// <summary>Go's <c>r.valid()</c> on a string-backed type: the value is a declared member.</summary>
    public static bool IsDefined<TEnum>(TEnum value)
        where TEnum : struct, Enum => Table<TEnum>.ByValue.ContainsKey(value);

    public static string SnakeCase(string pascal)
    {
        var sb = new StringBuilder(pascal.Length + 4);
        for (var i = 0; i < pascal.Length; i++)
        {
            if (i > 0 && char.IsUpper(pascal[i]))
            {
                sb.Append('_');
            }
            sb.Append(char.ToLowerInvariant(pascal[i]));
        }
        return sb.ToString();
    }

    private static class Table<TEnum>
        where TEnum : struct, Enum
    {
        public static readonly Dictionary<TEnum, string> ByValue = [];
        public static readonly Dictionary<string, TEnum> ByName = new(StringComparer.Ordinal);

        static Table()
        {
            foreach (var field in typeof(TEnum).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                var member = (TEnum)field.GetValue(null)!;
                var name = field.GetCustomAttribute<WireNameAttribute>()?.Name ?? SnakeCase(field.Name);
                ByValue.TryAdd(member, name);
                ByName.TryAdd(name, member);
            }
        }
    }
}
