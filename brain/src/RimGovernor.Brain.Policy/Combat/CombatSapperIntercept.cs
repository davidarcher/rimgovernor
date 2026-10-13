namespace RimGovernor.Brain.Policy;

public static partial class PolicyFunctions
{
    /// <summary>The share of its weapon range an interceptor stands off the digger.</summary>
    internal const double InterceptReach = 0.75;

    /// <summary>The stand-off, in cells, for an unknown range.</summary>
    internal const double InterceptDefaultRange = 20.0;

    /// <summary>
    /// The sapper tactic's intercept step. Sappers use no cover while they dig, so while a live humanlike
    /// sapper is digging (a Mine job) and our gunners are at least as many as the live hostiles, every gunner
    /// goes out: it moves to the cell on the line from its nearest digger toward itself at InterceptReach of
    /// its weapon range, and attacks that digger. A mech breacher is never intercepted: breach raids are never
    /// fought in the open. Once no one digs, or we are outnumbered, the gunners return to the breach posts.
    /// </summary>
    internal static CombatMemory SapperIntercept(CombatView view, CombatMemory m)
    {
        if (m.Tactic != CombatTactic.Sapper)
        {
            return m with { Intercept = false };
        }
        var humanlike = new Dictionary<PawnId, bool>();
        foreach (var t in view.Threats)
        {
            humanlike[t.Id] = Positive(t.Humanlike);
        }
        var diggers = new List<CombatPawnState>();
        foreach (var h in LiveSappers(view))
        {
            if (h.Job == "Mine" && humanlike.GetValueOrDefault(h.Id) && h.Cell.IsKnown)
            {
                diggers.Add(h);
            }
        }
        var gunners = m.Roles.Count(r => r.Ranged);
        if (diggers.Count == 0 || gunners < RankThreats(view).Count)
        {
            if (m.Intercept)
            {
                m = m with { Intercept = false };
                var breach = PredictBreach(view);
                if (breach is not null)
                {
                    m = m with { Roles = EquatableArray.From(SapperFormation(view, breach)) };
                }
            }
            return m;
        }
        var state = new Dictionary<PawnId, CombatPawnState>();
        foreach (var p in view.Pawns)
        {
            state[p.Id] = p;
        }
        var roles = m.Roles.ToList();
        for (var i = 0; i < roles.Count; i++)
        {
            var r = roles[i];
            if (!r.Ranged)
            {
                continue;
            }
            state.TryGetValue(r.Pawn, out var s);
            var known = s is not null && s.Cell.TryGetValue(out var at);
            at = known ? s!.Cell.Value : default;
            var digger = diggers[0];
            if (known)
            {
                foreach (var d in diggers.Skip(1))
                {
                    d.Cell.TryGetValue(out var c);
                    digger.Cell.TryGetValue(out var b);
                    if (Distance2(c, at) < Distance2(b, at))
                    {
                        digger = d;
                    }
                }
            }
            Cell? cell = null;
            if (known)
            {
                cell = StandOff(digger, at, s!.WeaponRange);
            }
            roles[i] = r with { Duty = CombatDuty.Interceptor, Target = digger.Id, Cell = cell, Retreat = false };
        }
        return m with { Intercept = true, Roles = EquatableArray.From(roles) };
    }

    /// <summary>The cell on the line from the digger toward <paramref name="from"/>, at InterceptReach of <paramref name="reach"/> (InterceptDefaultRange when unknown), or <paramref name="from"/> itself when it is already that close.</summary>
    internal static Cell StandOff(CombatPawnState digger, Cell from, double reach)
    {
        var want = InterceptDefaultRange;
        if (reach > 0)
        {
            want = InterceptReach * reach;
        }
        return StandOffAt(digger, from, want);
    }

    /// <summary>The cell on the line from <paramref name="target"/> toward <paramref name="from"/>, <paramref name="want"/> cells from target, or <paramref name="from"/> itself when it is already that close.</summary>
    internal static Cell StandOffAt(CombatPawnState target, Cell from, double want)
    {
        target.Cell.TryGetValue(out var d);
        double dx = from.X - d.X;
        double dz = from.Z - d.Z;
        var dist = Math.Sqrt((dx * dx) + (dz * dz));
        if (dist <= want)
        {
            return from;
        }
        return new Cell(d.X + (int)Math.Round(dx / dist * want, MidpointRounding.AwayFromZero), d.Z + (int)Math.Round(dz / dist * want, MidpointRounding.AwayFromZero));
    }
}
