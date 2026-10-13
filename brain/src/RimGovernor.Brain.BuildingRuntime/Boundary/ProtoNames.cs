using System.Reflection;
using Google.Protobuf.Reflection;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>Go's <c>enum.String()</c> for a protobuf enum: the member's original schema name (<c>FAILURE_CODE_REFUSED</c>).</summary>
internal static class ProtoNames
{
    public static string Of<T>(T value)
        where T : struct, Enum
    {
        var name = value.ToString();
        var member = typeof(T).GetField(name, BindingFlags.Public | BindingFlags.Static);
        return member?.GetCustomAttribute<OriginalNameAttribute>()?.Name ?? name;
    }
}
