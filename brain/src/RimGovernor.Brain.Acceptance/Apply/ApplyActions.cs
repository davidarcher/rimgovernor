using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using RimGovernor.Contracts.Wire.Operations;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// <c>apply/actions</c> (Go <c>actions.go</c>): every arm of the Action oneof, sent with bogus ids, reaches a native
/// handler and comes back refused, and a resent key replays its first result.
/// </summary>
internal static class ApplyActions
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "apply/actions",
        Scope = "Actions/Apply contract (#856): every arm of the Action oneof, sent with bogus ids, reaches a native handler "
            + "and comes back refused, and a resent key replays its first result. A Go test cannot see which arms native handles.",
        Start = new LabStart(),
        Budget = TimeSpan.FromMinutes(5),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        var report = s.Report;
        if (!Dyn.Contains(s.Names, "rimgovernor/operations_apply"))
        {
            throw new AcceptanceException("missing rimgovernor/operations_apply in discovery");
        }

        var arms = Arms();
        if (arms.Count == 0)
        {
            throw new AcceptanceException("the Action oneof has no arms");
        }

        foreach (var (arm, idFields) in arms)
        {
            var key = "probe-" + arm;
            var intent = new JsonObject();
            foreach (var field in idFields)
            {
                intent[field] = "Thing_ActionsProbe" + field;
            }

            var action = new JsonObject { ["key"] = key, [arm] = intent };
            var request = new JsonObject
            {
                ["identity"] = s.Identity.DeepClone(),
                ["actions"] = new JsonArray(action),
            };
            JsonObject? first = null;
            foreach (var attempt in new[] { "send", "resend" })
            {
                var reply = await h.WireAsync(attempt + "-" + arm, "operations_apply", request.DeepClone(), cancellationToken).ConfigureAwait(false);
                var result = ApplyCases.OneResult(arm, reply);
                var refused = result["refused"].AsMap();
                if (refused is null || result["key"].AsString() != key)
                {
                    throw new AcceptanceException($"{arm}: expected a refusal under the sent key, got {result.ToJson()}");
                }

                if (first is null)
                {
                    first = result;
                    report["refused_" + arm] = refused;
                }
                else if (!Dyn.DeepEqual(first, result))
                {
                    throw new AcceptanceException($"{arm}: resent key returned {result.ToJson()}, first was {first.ToJson()}");
                }
            }
        }
    }

    /// <summary>
    /// Every arm of the Action oneof, in ordinal order, with the top-level string fields named <c>*Id</c> (Go
    /// <c>bogusIDs</c>: each is set to an id no game holds). The arms are the derived cases of
    /// <see cref="ActionIntent"/>, discriminated by the arm's wire name.
    /// </summary>
    private static List<(string Arm, List<string> IdFields)> Arms()
    {
        var arms = new List<(string, List<string>)>();
        foreach (var derived in typeof(ActionIntent).GetCustomAttributes<JsonDerivedTypeAttribute>(inherit: false))
        {
            if (derived.TypeDiscriminator is not string arm)
            {
                continue;
            }

            var ids = new List<string>();
            var payload = derived.DerivedType.GetProperty("Value")?.PropertyType;
            if (payload is not null)
            {
                foreach (var property in payload.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (property.PropertyType == typeof(string) && property.Name.Length > 2
                        && property.Name.EndsWith("Id", StringComparison.Ordinal) && char.IsLower(property.Name[^3]))
                    {
                        ids.Add(string.Concat(char.ToLowerInvariant(property.Name[0]).ToString(), property.Name.AsSpan(1)));
                    }
                }
            }

            ids.Sort(StringComparer.Ordinal);
            arms.Add((arm, ids));
        }

        arms.Sort((a, b) => string.CompareOrdinal(a.Item1, b.Item1));
        return arms;
    }
}
