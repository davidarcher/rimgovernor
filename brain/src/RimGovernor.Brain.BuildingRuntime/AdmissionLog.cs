using System.Globalization;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>Port of admission_log.go: files one admission row per method admission outcome that matters.</summary>
internal static class AdmissionLog
{
    /// <summary>
    /// Runs a planner's building-method admission and files one admission row per outcome that matters: why a
    /// refused one was refused (planners map every refusal to shared_admission_refused, which alone hides a
    /// development gate from a footprint clash) and an admitted one, which commits the method to the plan. An
    /// error from the store files nothing: the planner's own step row carries it.
    /// </summary>
    public static async Task<BuildingMethodDecision> AdmitMethodAsync(JournalStore journal, BuildingMethodRequest r, CancellationToken ct)
    {
        using var span = Tel.StartSpan("buildingruntime", "admission");
        try
        {
            var decision = await journal.AdmitBuildingMethodAsync(r, ct).ConfigureAwait(false);
            Tel.Decide(MethodAdmissionDecision(r, decision));
            Metrics.RecordAdmission(decision.Admitted, decision.Refused);
            if (!decision.Admitted)
            {
                Tel.Refuse(span, decision.Refused.Select(f => Wire.Name(f.Reason)).ToArray());
            }
            Tel.EndSpan(span, null);
            return decision;
        }
        catch (Exception e)
        {
            Tel.EndSpan(span, e);
            throw;
        }
    }

    /// <summary>
    /// The admission row of a method admission: target the method, the concern in attrs, and for a refusal the
    /// first refusal's reason with every refusal listed in refused.
    /// </summary>
    internal static Decision MethodAdmissionDecision(BuildingMethodRequest r, BuildingMethodDecision decision)
    {
        var d = new Decision
        {
            Kind = "admission",
            Component = "clock-scheduler",
            Verdict = "admitted",
            Reason = "method_committed",
            Target = r.Method.Value,
            Attrs = new Dictionary<string, object?> { ["concern"] = r.Owner.Standard.Id.Value },
        };
        if (!decision.Admitted)
        {
            d.Verdict = "refused";
            d.Reason = decision.Refused.Count > 0 ? Wire.Name(decision.Refused[0].Reason) : "unspecified";
            d.Attrs["refused"] = RefusalSummary(decision.Refused);
        }
        return d;
    }

    /// <summary>Reads each refusal as reason[/resource]@action.</summary>
    internal static string RefusalSummary(IReadOnlyList<Refusal> refused)
    {
        if (refused.Count == 0)
        {
            return "none";
        }
        var parts = refused.Select(f =>
        {
            var s = Wire.Name(f.Reason);
            if (f.Resource.Value.Length != 0)
            {
                s += "/" + f.Resource.Value;
            }
            if (f.Action.Value.Length != 0)
            {
                s += "@" + f.Action.Value;
            }
            return s;
        });
        return string.Create(CultureInfo.InvariantCulture, $"[{string.Join(' ', parts)}]");
    }

    /// <summary>
    /// The verdict of a method the shared admission check turned down: it keeps the shared_admission_refused kind
    /// and carries the decision's first refusal, reason as the subject and resource as the detail, so the status
    /// line and the journal name why. A decision with no refusal listed means the policy admitted fewer candidates
    /// than it was given without refusing any; that names itself.
    /// </summary>
    public static Verdict AdmissionRefused(BuildingMethodDecision decision)
    {
        if (decision.Refused.Count == 0)
        {
            return Verdict.Refuse(Cause.SharedAdmission, "candidates_left_unadmitted", "");
        }
        var first = decision.Refused[0];
        return Verdict.Refuse(Cause.SharedAdmission, Wire.Name(first.Reason), first.Resource.Value);
    }
}
