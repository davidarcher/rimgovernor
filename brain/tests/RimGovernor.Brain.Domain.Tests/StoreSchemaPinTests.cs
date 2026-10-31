using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;
using RimGovernor.Brain.Store.Codec;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// A store opened at the same <see cref="Store.Store.SchemaVersion"/> is decoded as compatible, so a codec that starts
/// writing or requiring a property without a version bump makes old stores throw on read (2026-10-19: step Targets/Cells,
/// task PriorSteps and UseUntilTick broke "rebuild owners"). This pins the persisted JSON shape of every hand-written
/// store codec to the version it was recorded at. The shape is read from the compiled codecs, not their source: per codec
/// type, the multiset of string literals (property names) and of the JSON members and codec helpers they call.
/// </summary>
public sealed class StoreSchemaPinTests
{
    private const int PinnedSchemaVersion = 67;
    // Version 66 replaces older unclassified construction deliveries; codec properties are unchanged.
    private const string PinnedShapeHash = "A8A38A40402B516130616B8DE358AEEB34459EB3F03B53973CB9444B91E58644";

    [Fact]
    public void CodecShapeIsPinnedToTheSchemaVersion()
    {
        var hash = ShapeHash();
        Assert.True(hash == PinnedShapeHash && Store.Store.SchemaVersion == PinnedSchemaVersion,
            hash != PinnedShapeHash
                ? $"The stored JSON shape changed (hash {hash}). Bump Store.SchemaVersion, add its doc line in Store.cs, then set "
                  + $"{nameof(PinnedSchemaVersion)} to the new version and {nameof(PinnedShapeHash)} to \"{hash}\" in {nameof(StoreSchemaPinTests)}."
                : $"Store.SchemaVersion is {Store.Store.SchemaVersion}; set {nameof(PinnedSchemaVersion)} to it in {nameof(StoreSchemaPinTests)}.");
    }

    private static readonly Dictionary<short, OpCode> _opCodes = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static).Select(f => (OpCode)f.GetValue(null)!).ToDictionary(o => o.Value);

    private static string ShapeHash()
    {
        var assembly = typeof(Codecs).Assembly;
        var lines = assembly.GetTypes().GroupBy(Root)
            .Select(g => (Root: g.Key, Tokens: g.SelectMany(Tokens).Order(StringComparer.Ordinal).ToList()))
            // Every codec and codec kit that reads or writes JSON. Store itself only writes the governor state mirror, which has
            // its own version (GovernorStateSchemaVersion), beside SQL text and messages that do not shape a payload.
            .Where(r => r.Root != typeof(Store.Store) && r.Tokens.Any(t => t.StartsWith("json:", StringComparison.Ordinal)))
            .Select(r => r.Root.FullName + "\t" + string.Join("|", r.Tokens))
            .Order(StringComparer.Ordinal);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", lines))));
    }

    private static Type Root(Type type) => type.DeclaringType is { } outer ? Root(outer) : type;

    private static IEnumerable<string> Tokens(Type type)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (var method in type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)))
        {
            if (method.GetMethodBody()?.GetILAsByteArray() is not { } il)
            {
                continue;
            }
            var typeArgs = type.IsGenericType ? type.GetGenericArguments() : null;
            var methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;
            for (var i = 0; i < il.Length;)
            {
                var code = il[i] == 0xFE ? _opCodes[unchecked((short)(0xFE00 | il[i + 1]))] : _opCodes[il[i]];
                i += code.Size;
                var token = code.OperandType is OperandType.InlineString or OperandType.InlineMethod ? BitConverter.ToInt32(il, i) : 0;
                if (code.OperandType == OperandType.InlineString)
                {
                    yield return "str:" + type.Module.ResolveString(token);
                }
                else if (code.OperandType == OperandType.InlineMethod
                    && type.Module.ResolveMethod(token, typeArgs, methodArgs) is { DeclaringType: { } owner } called
                    && Shaping(owner))
                {
                    var generic = called.IsGenericMethod ? "<" + string.Join(",", called.GetGenericArguments().Select(Name)) + ">" : "";
                    yield return (owner.Namespace?.StartsWith("System.Text.Json", StringComparison.Ordinal) == true ? "json:" : "kit:")
                        + Name(owner) + "." + called.Name + generic;
                }
                i += code.OperandType switch
                {
                    OperandType.InlineNone => 0,
                    OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                    OperandType.InlineVar => 2,
                    OperandType.InlineI8 or OperandType.InlineR => 8,
                    OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(il, i)),
                    _ => 4,
                };
            }
        }
    }

    // The members that decide the JSON shape: System.Text.Json itself and the store's codec helpers (Codecs.Read/Write<T>,
    // JsonFields, the codec kits). Calls into the domain only build values and leave the shape alone.
    private static bool Shaping(Type owner) =>
        owner.Namespace?.StartsWith("System.Text.Json", StringComparison.Ordinal) == true
        || (owner.Assembly == typeof(Codecs).Assembly && (owner == typeof(Codecs) || (owner.IsAbstract && owner.IsSealed)));

    private static string Name(Type type) =>
        type.IsGenericType ? type.Name + "<" + string.Join(",", type.GetGenericArguments().Select(Name)) + ">" : type.Name;
}
