using System.Reflection;
using System.Text.Json.Serialization;
using RimGovernor.Brain.Bridge;
using Operations = RimGovernor.Contracts.Wire.Operations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>apply/actions</c> (Go <c>actions.go</c>): every arm of the Action oneof, sent with bogus ids, reaches a native
/// handler and comes back refused, and a resent key replays its first result.
/// </summary>
internal static class ApplyActions
{
    internal static TypedCase Case() => new()
    {
        Name = "apply/actions",
        Scope = "Actions/Apply contract (#856): every arm of the Action oneof, sent with bogus ids, reaches a native handler "
            + "and comes back refused, and a resent key replays its first result. A unit test cannot see which arms native handles.",
        Ticks = 0,
        Tier = CaseTier.OnDemand,
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var arms = Arms();
        if (arms.Count == 0)
        {
            throw new AcceptanceException("the Action oneof has no arms");
        }

        foreach (var (arm, intent) in arms)
        {
            var key = "probe-" + arm;
            var request = new Operations.ApplyRequest { Identity = s.Identity, Actions = [new Operations.Action { Key = key, Intent = intent }] };
            Operations.ActionResult? first = null;
            foreach (var attempt in new[] { "send", "resend" })
            {
                var reply = await s.CallAsync(attempt + "-" + arm, NativeOps.Apply, request, cancellationToken).ConfigureAwait(false);
                if (reply.Results.ToList() is not [{ Outcome.Refused: { } refused } result] || result.Key != key)
                {
                    throw new AcceptanceException($"{arm}: expected one refusal under the sent key, got {reply.BatchFailure} {string.Join("; ", reply.Results)}");
                }

                if (first is null)
                {
                    first = result;
                    s.RecordWire("refused_" + arm, refused);
                }
                else if (result != first)
                {
                    throw new AcceptanceException($"{arm}: resent key returned {result}, first was {first}");
                }
            }
        }
    }

    /// <summary>
    /// Every arm of the Action oneof in ordinal order, each carrying its payload with the top-level string fields named
    /// <c>*Id</c> set to an id no game holds (Go <c>bogusIDs</c>). The arms are the derived cases of
    /// <see cref="Operations.ActionIntent"/>, discriminated by the arm's wire name.
    /// </summary>
    private static List<(string Arm, Operations.ActionIntent Intent)> Arms()
    {
        var arms = new List<(string, Operations.ActionIntent)>();
        foreach (var derived in typeof(Operations.ActionIntent).GetCustomAttributes<JsonDerivedTypeAttribute>(inherit: false))
        {
            if (derived.TypeDiscriminator is not string arm
                || derived.DerivedType.GetProperty("Value")?.PropertyType is not { } payloadType
                || Activator.CreateInstance(payloadType) is not { } payload)
            {
                continue;
            }

            foreach (var property in payloadType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.PropertyType == typeof(string) && property.CanWrite && property.Name.Length > 2
                    && property.Name.EndsWith("Id", StringComparison.Ordinal) && char.IsLower(property.Name[^3]))
                {
                    property.SetValue(payload, "Thing_ActionsProbe" + char.ToLowerInvariant(property.Name[0]) + property.Name[1..]);
                }
            }

            arms.Add((arm, (Operations.ActionIntent)Activator.CreateInstance(derived.DerivedType, payload)!));
        }

        arms.Sort((a, b) => string.CompareOrdinal(a.Item1, b.Item1));
        return arms;
    }
}
