using System.Reflection;

namespace RimGovernor.Brain.Runtime;

/// <summary>The wire name of an enum member (<c>FAILURE_CODE_REFUSED</c>) from its <c>[WireName]</c>, or its member name.</summary>
internal static class EnumWireNames
{
    public static string Of<T>(T value)
        where T : struct, Enum
    {
        var name = value.ToString();
        var member = typeof(T).GetField(name, BindingFlags.Public | BindingFlags.Static);
        return member?.GetCustomAttribute<WireNameAttribute>()?.Name ?? name;
    }
}
