namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>Remembers since when a room has had unfinished sites and when the stall was last reported. Derived, in memory only.</summary>
internal readonly record struct RoomStall(Tick Since, Tick Reported = default);

internal static partial class BuildingRuntimePackage
{
    /// <summary>
    /// How long a room's blueprints or frames may stand unfinished before the planner reports the stall, and
    /// the spacing of the repeats while it lasts: half a day.
    /// </summary>
    internal const long RoomBuildStallTicks = GameTime.TicksPerDay / 2;

    /// <summary>The room's blueprints and frames still standing on its ground.</summary>
    internal static List<Pol.ConstructionSite> RoomSites(IEnumerable<Pol.ConstructionSite> sites, Pol.Rectangle ground)
    {
        var output = new List<Pol.ConstructionSite>();
        foreach (var s in sites)
        {
            var c = s.Building.Cell;
            if (c.X >= ground.X && c.X < ground.X + ground.Width && c.Z >= ground.Z && c.Z < ground.Z + ground.Height)
            {
                output.Add(s);
            }
        }
        return output;
    }

    /// <summary>The first reason the census gives for a site not finishing.</summary>
    internal static string SiteReason(Pol.ConstructionSite s)
    {
        if (s.FinishingBlocker != "")
        {
            return s.FinishingBlocker;
        }
        if (s.ResourcesComplete.TryGetValue(out var done) && !done)
        {
            return "awaiting_materials";
        }
        if (s.EligibleFinishers.TryGetValue(out var n) && n == 0)
        {
            return "no_eligible_builder";
        }
        return "unreported";
    }

    /// <summary>Counts the sites by stage and by reason.</summary>
    internal static (int Blueprints, int Frames, Dictionary<string, int> Reasons) SiteCounts(IEnumerable<Pol.ConstructionSite> sites)
    {
        var blueprints = 0;
        var frames = 0;
        var reasons = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var s in sites)
        {
            if (s.Stage == "frame")
            {
                frames++;
            }
            else
            {
                blueprints++;
            }
            var reason = SiteReason(s);
            reasons[reason] = reasons.GetValueOrDefault(reason) + 1;
        }
        return (blueprints, frames, reasons);
    }

    /// <summary>The most common reason, ties broken by name.</summary>
    internal static string DominantReason(IReadOnlyDictionary<string, int> reasons)
    {
        var best = "";
        foreach (var (r, n) in reasons)
        {
            if (best == "" || n > reasons[best] || (n == reasons[best] && string.CompareOrdinal(r, best) < 0))
            {
                best = r;
            }
        }
        return best;
    }

    /// <summary>Names what a room's wait is on: the stage counts and the dominant reason; empty when no site stands.</summary>
    internal static string AwaitingKey(IReadOnlyList<Pol.ConstructionSite> sites)
    {
        if (sites.Count == 0)
        {
            return "";
        }
        var (blueprints, frames, reasons) = SiteCounts(sites);
        var parts = new List<string>();
        if (blueprints > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{blueprints}_blueprints"));
        }
        if (frames > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{frames}_frames"));
        }
        return "awaiting_build:" + string.Join(",", parts) + ":" + DominantReason(reasons);
    }

    /// <summary>The most common site reason, or "obstructed" when only foreign things on the room's cells hold it.</summary>
    internal static string StallReason(IReadOnlyDictionary<string, int> reasons, IReadOnlyList<string> obstructions) =>
        reasons.Count == 0 && obstructions.Count > 0 ? "obstructed" : DominantReason(reasons);

    /// <summary>
    /// Names the foreign things the room's pending claim, cut and haul operations stand on, e.g.
    /// Steel(40)@10,10: what a door or wall cell is waiting to have cleared.
    /// </summary>
    internal static List<string> RoomObstructions(IEnumerable<Pol.Operation> ops)
    {
        var output = new List<string>();
        foreach (var op in ops)
        {
            if (op.Kind != Pol.OperationKind.Claim && op.Kind != Pol.OperationKind.Cut && op.Kind != Pol.OperationKind.HaulOut)
            {
                continue;
            }
            foreach (var t in op.Targets)
            {
                var count = t.Count > 0 ? string.Create(CultureInfo.InvariantCulture, $"({t.Count})") : "";
                output.Add(string.Create(CultureInfo.InvariantCulture, $"{Wire.Name(op.Kind)}:{t.DefName}{count}@{t.Minimum.X},{t.Minimum.Z}"));
            }
        }
        return output;
    }
}

public sealed partial class Rounder
{
    internal Dictionary<string, RoomStall>? RoomStalls;

    /// <summary>
    /// Tracks the room's unfinished sites and reports a stall once they have stood RoomBuildStallTicks, again
    /// every window after.
    /// </summary>
    internal void NoteStall(string name, Tick now, IReadOnlyList<Pol.ConstructionSite> sites, IReadOnlyList<string> obstructions)
    {
        if (sites.Count == 0 && obstructions.Count == 0)
        {
            RoomStalls?.Remove(name);
            return;
        }
        RoomStalls ??= new Dictionary<string, RoomStall>(StringComparer.Ordinal);
        if (!RoomStalls.TryGetValue(name, out var st))
        {
            RoomStalls[name] = new RoomStall(now);
            return;
        }
        if (now.Value - st.Since.Value < RoomBuildStallTicks || (st.Reported.Value != 0 && now.Value - st.Reported.Value < RoomBuildStallTicks))
        {
            return;
        }
        st = st with { Reported = now };
        RoomStalls[name] = st;
        var (blueprints, frames, reasons) = SiteCounts(sites);
        var tiers = new Dictionary<string, int>(StringComparer.Ordinal);
        var eligible = 0;
        var skill = 0;
        foreach (var s in sites)
        {
            if (s.Tier.TryGetValue(out var tier))
            {
                var key = ((int)tier).ToString(CultureInfo.InvariantCulture);
                tiers[key] = tiers.GetValueOrDefault(key) + 1;
            }
            if (s.EligibleFinishers.TryGetValue(out var n))
            {
                eligible += n;
            }
            if (s.MinimumFinishingSkill.TryGetValue(out var m) && m > skill)
            {
                skill = m;
            }
        }
        var names = sites
            .Select(s => string.Create(CultureInfo.InvariantCulture, $"{s.Building.Definition}@{s.Building.Cell.X},{s.Building.Cell.Z}"))
            .Order(StringComparer.Ordinal)
            .Take(8)
            .ToList();
        TelemetryPackage.Decide(new Decision
        {
            Kind = "build_stalled",
            Component = "building-planner",
            Level = Telemetry.Level.Warn,
            Verdict = "waiting",
            Reason = StallReason(reasons, obstructions),
            Target = name,
            Attrs = new Dictionary<string, object?>
            {
                ["blueprints"] = blueprints,
                ["frames"] = frames,
                ["reasons"] = reasons,
                ["tiers"] = tiers,
                ["eligible_finishers"] = eligible,
                ["max_min_skill"] = skill,
                ["stalled_ticks"] = now.Value - st.Since.Value,
                ["sites"] = names,
                ["obstructions"] = obstructions,
            },
        });
    }
}
