using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using RimGovernor.Brain.Acceptance;
using RimGovernor.Brain.Acceptance.Cases;
using RimGovernor.Brain.GameFacts;
using RimGovernor.Brain.GameOrders;
using Xunit;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// Every closed type the acceptance code hands to a source-generated JSON entry point is registered in that entry point's
/// context. An unregistered type compiles and only fails in the game as <c>ArgumentNullException: jsonTypeInfo</c> (#3570:
/// <c>RecordWire</c> of a <c>NeedTarget[]</c>).
/// </summary>
public sealed class AcceptanceJsonRegistrationTests
{
    /// <summary>The generic entry points whose type argument must be registered, with the serializer that proves it.</summary>
    private static readonly (MethodInfo[] Definitions, MethodInfo Serialize)[] EntryPoints =
    [
        (Generic(typeof(OrdersJson)), typeof(OrdersJson).GetMethod(nameof(OrdersJson.Serialize))!),
        (Generic(typeof(TypedSession), nameof(TypedSession.RecordWire), nameof(TypedSession.RecordWireRows)), typeof(OrdersJson).GetMethod(nameof(OrdersJson.Serialize))!),
        (Generic(typeof(TypedSession), nameof(TypedSession.RecordFixture)), typeof(Fx.FixtureJson).GetMethod(nameof(Fx.FixtureJson.Serialize))!),
        (Generic(typeof(Fx.FixtureJson)), typeof(Fx.FixtureJson).GetMethod(nameof(Fx.FixtureJson.Serialize))!),
        (Generic(typeof(Harness), nameof(Harness.FixtureReply)), typeof(Fx.FixtureJson).GetMethod(nameof(Fx.FixtureJson.Serialize))!),
        (Generic(typeof(FixtureRecordJson), nameof(FixtureRecordJson.Args)), typeof(Fx.FixtureJson).GetMethod(nameof(Fx.FixtureJson.Serialize))!),
        (Generic(typeof(ServedOps), nameof(ServedOps.Read), nameof(ServedOps.Reply), nameof(ServedOps.Succeeded)),
            typeof(Fx.FixtureJson).GetMethod(nameof(Fx.FixtureJson.Serialize))!),
    ];

    [Fact]
    public void EveryTypeAcceptanceCodeSerializesIsRegistered()
    {
        var unregistered = new SortedSet<string>(StringComparer.Ordinal);
        var checkedCount = 0;
        foreach (var assembly in new[] { typeof(Harness).Assembly, typeof(ServedOps).Assembly })
        {
            foreach (var call in Instantiations(assembly))
            {
                var definition = call.GetGenericMethodDefinition();
                var entry = EntryPoints.FirstOrDefault(e => e.Definitions.Any(d => d.Module == definition.Module && d.MetadataToken == definition.MetadataToken));
                if (entry.Serialize is null)
                {
                    continue;
                }

                var type = call.GetGenericArguments()[0];
                checkedCount++;
                try
                {
                    // A registration probe must use a named enum value: zero need not be a supported wire case.
                    var sample = type.IsEnum ? Enum.GetValues(type).GetValue(0) : null;
                    _ = entry.Serialize.MakeGenericMethod(type).Invoke(null, [sample]);
                }
                catch (TargetInvocationException e) when (e.InnerException is ArgumentNullException)
                {
                    unregistered.Add($"{type} via {definition.DeclaringType!.Name}.{definition.Name} in {assembly.GetName().Name}");
                }
            }
        }

        Assert.True(checkedCount > 0, "the scan found no entry-point call");
        Assert.True(unregistered.Count == 0, "unregistered:\n" + string.Join('\n', unregistered));
    }

    private static MethodInfo[] Generic(Type type, params string[] names) =>
        [.. type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.IsGenericMethodDefinition && (names.Length == 0 || names.Contains(m.Name)))];

    /// <summary>The closed generic method instantiations (MethodSpec rows) the assembly's IL calls.</summary>
    private static List<MethodInfo> Instantiations(Assembly assembly)
    {
        using var pe = new PEReader(File.OpenRead(assembly.Location));
        var rows = pe.GetMetadataReader().GetTableRowCount(TableIndex.MethodSpec);
        var module = assembly.ManifestModule;
        var found = new List<MethodInfo>();
        for (var row = 1; row <= rows; row++)
        {
            MethodBase? method;
            try
            {
                method = module.ResolveMethod(MetadataTokens.GetToken(MetadataTokens.MethodSpecificationHandle(row)));
            }
            catch (ArgumentException)
            {
                continue; // An instantiation over the caller's own type parameters: open, so nothing to check here.
            }

            if (method is MethodInfo { IsGenericMethod: true, ContainsGenericParameters: false } closed)
            {
                found.Add(closed);
            }
        }

        return found;
    }
}
