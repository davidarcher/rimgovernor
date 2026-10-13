namespace RimGovernor.Brain.Policy;

// Go: herd_master.go

public static partial class PolicyFunctions
{
    /// <summary>
    /// Assigns one animal a master by the bond-first rule: (1) a bonded animal is mastered by its bonded
    /// colonist, the first by id among its bond partners on the roster (a roster partner already mastering is
    /// kept; bonds give +5 mood as master, -3 otherwise); (2) an unbonded animal with a wanted, available,
    /// unlearned trainable gets the handling-capable colonist with the best Animals skill, one colonist may
    /// master any number of animals, follow flags stay as they are; (3) an unbonded war animal gets a
    /// front-line handler and follow_drafted true; (4) an unbonded haul animal gets no master and no follow
    /// flag. follow_fieldwork is never written. An animal is skipped while it is not obedient (native refuses
    /// master and follow without learned Obedience), is marked for release or slaughter, has an unread fact,
    /// or its race is unplanned or retiring. A master who fits is kept and reassigned only when empty or not
    /// fitting. One write per call: master first, then follow_drafted.
    /// </summary>
    public static HusbandryChoice HerdMasterChoice(Fact<EquatableArray<UpkeepAnimal>> animals, HerdPolicy herd, Fact<EquatableArray<PawnProfile>> profiles)
    {
        var none = new HusbandryChoice { Reason = HusbandryPlanReason.NoDeficit };
        if (!animals.TryGetValue(out var rows) || !profiles.TryGetValue(out var roster))
        {
            return none;
        }

        var sorted = rows.OrderBy(a => a.Id).ToList();
        var (front, _) = FrontLine(roster);
        foreach (var a in sorted)
        {
            if (!herd.Roles.TryGetValue(a.Definition, out var role) || role.Retiring)
            {
                continue;
            }

            var ok = a.Obedient.TryGetValue(out var obedient);
            var rk = a.Release.TryGetValue(out var release);
            var sk = a.Slaughter.TryGetValue(out var slaughter);
            var mk = a.Master.TryGetValue(out var master);
            if (!ok || !obedient || !rk || !sk || release || slaughter || !mk)
            {
                continue;
            }

            var (fit, bonded, found) = BondedMasters(roster, a.BondedPawns.Items);
            var best = bonded;
            var war = false;
            if (!found)
            {
                var (training, trainingKnown) = HerdUnlearned(herd, a);
                if (!trainingKnown)
                {
                    continue;
                }

                if (training)
                {
                    fit = HerdMasters(roster, null);
                }
                else if (role.Job == HerdJob.War)
                {
                    fit = HerdMasters(roster, front);
                    war = true;
                }
                else
                {
                    continue;
                }
            }

            if (!fit.Contains(new PawnId(master ?? "")))
            {
                if (!found)
                {
                    var pick = HerdBestMaster(roster, fit);
                    found = pick is not null;
                    best = pick ?? default;
                }

                if (!found)
                {
                    continue;
                }

                return new HusbandryChoice { Animal = a.Id, Method = HusbandryMethod.Master, Argument = best.Value };
            }

            if (war && a.FollowDrafted.TryGetValue(out var drafted) && !drafted)
            {
                return new HusbandryChoice { Animal = a.Id, Method = HusbandryMethod.FollowDrafted, Argument = "true" };
            }
        }

        return none;
    }

    /// <summary>Reports whether the animal has a wanted, available trainable it has not learned; known is false when one of those facts is unread.</summary>
    private static (bool Unlearned, bool Known) HerdUnlearned(HerdPolicy herd, UpkeepAnimal a)
    {
        var unlearned = false;
        foreach (var t in HerdTrainQueue(herd, a))
        {
            var ak = t.Available.TryGetValue(out var avail);
            var lk = t.Learned.TryGetValue(out var learned);
            if (!ak || (avail && !lk))
            {
                return (false, false);
            }

            if (avail && !learned)
            {
                unlearned = true;
            }
        }

        return (unlearned, true);
    }

    /// <summary>The handling-capable colonists, limited to <paramref name="only"/> when it is not null (the front line for a war animal).</summary>
    private static HashSet<PawnId> HerdMasters(IReadOnlyList<PawnProfile> roster, IReadOnlyList<PawnId>? only)
    {
        var allowed = only is null ? [] : new HashSet<PawnId>(only);
        var fit = new HashSet<PawnId>();
        foreach (var p in roster)
        {
            if (p.Capable(WorkType.Handling, 0) && (only is null || allowed.Contains(p.Id)))
            {
                fit.Add(p.Id);
            }
        }

        return fit;
    }

    /// <summary>The fitting colonist with the highest Animals skill.</summary>
    private static PawnId? HerdBestMaster(IReadOnlyList<PawnProfile> roster, HashSet<PawnId> fit)
    {
        var candidates = new List<RoleCandidate>();
        foreach (var p in roster)
        {
            if (fit.Contains(p.Id))
            {
                candidates.Add(new RoleCandidate(p.Id, p.SkillFor(WorkType.Handling).Level));
            }
        }

        return BestRole(candidates);
    }

    /// <summary>The set of an animal's bond partners on the roster and the first of them by id.</summary>
    private static (HashSet<PawnId> Fit, PawnId First, bool Found) BondedMasters(IReadOnlyList<PawnProfile> roster, IReadOnlyList<string> bonded)
    {
        var onRoster = new HashSet<PawnId>(roster.Select(p => p.Id));
        var fit = new HashSet<PawnId>();
        PawnId first = default;
        foreach (var id in bonded)
        {
            var pid = new PawnId(id);
            if (onRoster.Contains(pid))
            {
                fit.Add(pid);
                if (first.IsEmpty || pid < first)
                {
                    first = pid;
                }
            }
        }

        return (fit, first, !first.IsEmpty);
    }
}
