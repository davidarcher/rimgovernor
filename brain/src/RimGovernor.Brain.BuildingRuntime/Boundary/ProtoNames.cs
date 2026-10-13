using System.Reflection;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>Go's <c>enum.String()</c> for a wire enum: the member's original schema name (<c>FAILURE_CODE_REFUSED</c>), carried by its <c>[WireName]</c>.</summary>
internal static class ProtoNames
{
    public static string Of<T>(T value)
        where T : struct, Enum
    {
        var name = value.ToString();
        var member = typeof(T).GetField(name, BindingFlags.Public | BindingFlags.Static);
        return member?.GetCustomAttribute<WireNameAttribute>()?.Name ?? name;
    }
}
