using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>medical/surgery-elective-rich</c> and <c>medical/surgery-elective-poor</c> (Go <c>electiveshare.go</c>): the same
/// hospital, doctors and stock run twice, once in a colony holding 6000 gold and once in one stripped of every loose
/// item but wood, medicine, the prosthetic leg, the two bionic parts and eight meals (test/medical_management_setup
/// condition elective, wealth rich or poor). The third colonist is missing a leg with a SimpleProstheticLeg stocked (the
/// one served operation); a BionicEye and a BionicArm are stocked for healthy colonists (the electives). The pair
/// diverges only through the colonists' personal shares: the rich colony installs an elective, the poor one installs
/// none and MaintainSurgery recovers.
/// Shared asserts: served operations win (no elective intent exists before the leg's bill, and the leg is installed by
/// the end), and one elective at a time (the first elective intent seen is the only one, and no colonist ends with two
/// queued bills).
/// </summary>
internal static class ElectiveShare
{
    // The stocked bionics' install recipes and the item each spawns.
    private static readonly Dictionary<string, string> ElectiveRecipes = new(StringComparer.Ordinal)
    {
        ["InstallBionicEye"] = "BionicEye",
        ["InstallBionicArm"] = "BionicArm",
    };

    internal static IEnumerable<AcceptanceCase> All()
    {
        foreach (var (name, wealth, scope) in new[]
        {
            ("medical/surgery-elective-rich", "rich",
                "Elective share gate (#1848): in a rich colony the served leg restore is queued first, then exactly one stocked bionic (eye or arm) is queued as an elective and a native doctor installs it."),
            ("medical/surgery-elective-poor", "poor",
                "Elective share gate (#1848): in a poor colony the served leg restore is queued and installed, no stocked bionic is ever queued or installed (the shares do not cover them), and MaintainSurgery recovers."),
        })
        {
            var kind = wealth;
            yield return new AcceptanceCase
            {
                Name = name,
                Scope = scope,
                Start = new FixtureStart
                {
                    Op = "test/medical_management_setup",
                    On = new LabStart(),
                    Args = new JsonObject { ["disease"] = false, ["condition"] = "elective", ["wealth"] = kind },
                },
                Service = true,
                Budget = TimeSpan.FromMinutes(12),
                Crew = new Crew { Size = 3 },
                Run = (s, ct) => RunAsync(s, kind, ct),
            };
        }
    }

    // The distinct elective intents in the history.
    private static List<SurgeryIntent> ElectiveIntents(IEnumerable<SurgeryIntent> surgeries)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<SurgeryIntent>();
        foreach (var s in surgeries)
        {
            var key = string.Create(CultureInfo.InvariantCulture, $"{s.Pawn}/{s.Part}/{s.Recipe}");
            if (ElectiveRecipes.ContainsKey(s.Recipe) && seen.Add(key))
            {
                result.Add(s);
            }
        }

        return result;
    }

    // Whether the pawn wears an added part that spawns the item.
    private static bool HasInstalled(JsonObject health, string item) =>
        health["installedParts"].Rows().Any(part => part["spawnThingDefName"].AsString() == item);

    private static async Task RunAsync(ISession s, string wealth, CancellationToken cancellationToken)
    {
        var prepared = s.Prepared ?? [];
        var report = s.Report;
        var (leg, legPart) = MedicalKit.SurgicalFixture(s);
        report["wealth"] = wealth;
        report["wealth_items"] = prepared["wealthItems"].Copy();
        var pawns = new List<string> { leg };
        pawns.AddRange(prepared["patients"].AsSlice().Select(raw => raw.AsString()));

        var run = await SurgeryRun.StartAsync(s, "surgery-elective-" + wealth, cancellationToken).ConfigureAwait(false);
        try
        {
            // Served first: the leg's bill must be journaled before any elective, and no elective may appear while it has not been.
            await run.UntilAsync(
                TimeSpan.FromMinutes(4),
                (surgeries, _) =>
                {
                    var served = SurgeryRun.Find(surgeries, leg, legPart, "InstallSimpleProstheticLeg") is not null;
                    var early = ElectiveIntents(surgeries);
                    if (!served && early.Count > 0)
                    {
                        throw new AcceptanceException($"elective {early[0].Recipe} queued on {early[0].Pawn} before the served leg restore");
                    }

                    return Task.FromResult((Wait.Signature(surgeries.Count), served));
                },
                cancellationToken).ConfigureAwait(false);
            report["leg_intent"] = true;

            ulong from = 0;
            Task Follow(string label, TimeSpan limit, Func<ulong, IReadOnlyList<SurgeryIntent>, CancellationToken, Task<bool>> done) =>
                run.UntilAsync(
                    limit,
                    async (surgeries, ct) =>
                    {
                        var tick = await run.TickAsync(ct).ConfigureAwait(false);
                        if (from == 0)
                        {
                            from = tick;
                        }

                        bool ok;
                        try
                        {
                            ok = await done(tick, surgeries, ct).ConfigureAwait(false);
                        }
                        catch (AcceptanceException e)
                        {
                            throw new AcceptanceException($"{label}: {e.Message}", e);
                        }

                        return (Wait.Signature(tick), ok);
                    },
                    cancellationToken);

            if (wealth == "rich")
            {
                List<SurgeryIntent> first = [];
                try
                {
                    await run.UntilAsync(
                        TimeSpan.FromMinutes(8),
                        (surgeries, _) =>
                        {
                            first = ElectiveIntents(surgeries);
                            return Task.FromResult((Wait.Signature(surgeries.Count), first.Count > 0));
                        },
                        cancellationToken).ConfigureAwait(false);
                }
                catch (AcceptanceException e)
                {
                    throw new AcceptanceException($"no elective was queued in the rich colony: {e.Message}", e);
                }

                // One at a time: the first review that books an elective books one.
                if (first.Count != 1)
                {
                    throw new AcceptanceException($"{first.Count} elective intents at first sight, want 1: {string.Join(", ", first)}");
                }

                report["elective_intent"] = new JsonObject { ["pawn"] = first[0].Pawn, ["part"] = first[0].Part, ["recipe"] = first[0].Recipe };
                from = 0;
                await Follow("rich served window", TimeSpan.FromMinutes(4), (tick, _, _) => Task.FromResult(tick >= from + SurgeryRun.ServedTicks)).ConfigureAwait(false);
            }
            else
            {
                // Unaffordable: no elective for a served window past the leg, and the goal recovers once the leg is done.
                await Follow(
                    "poor served window",
                    TimeSpan.FromMinutes(6),
                    async (tick, surgeries, ct) =>
                    {
                        var got = ElectiveIntents(surgeries);
                        if (got.Count > 0)
                        {
                            throw new AcceptanceException($"elective {got[0].Recipe} queued on {got[0].Pawn} in the poor colony");
                        }

                        if (tick < from + SurgeryRun.ServedTicks)
                        {
                            return false;
                        }

                        return await run.RecoveredAsync(PolicyFunctions.MaintainSurgery.Value, ct).ConfigureAwait(false);
                    }).ConfigureAwait(false);
            }
        }
        finally
        {
            await run.StopAsync(cancellationToken).ConfigureAwait(false);
        }

        _ = s.Reattach();
        var installed = 0;
        var queued = 0;
        foreach (var pawn in pawns)
        {
            var health = await MedicalKit.PawnHealthAsync(s, "surgery-elective-" + pawn, pawn, cancellationToken).ConfigureAwait(false);
            if (pawn == leg && MedicalKit.PartMissing(health, legPart))
            {
                throw new AcceptanceException($"the served leg restore was not installed on {leg}");
            }

            foreach (var item in ElectiveRecipes.Values)
            {
                if (HasInstalled(health, item))
                {
                    installed++;
                }
            }

            var electiveQueued = health["surgeryBills"].Rows().Count(bill => ElectiveRecipes.ContainsKey(bill["recipe"].AsString()));
            if (electiveQueued > 1)
            {
                throw new AcceptanceException($"{pawn} has {electiveQueued} queued elective bills");
            }

            queued += electiveQueued;
        }

        report["elective_installed"] = installed;
        report["elective_queued"] = queued;
        if (queued > 1)
        {
            throw new AcceptanceException($"{queued} elective bills queued colony-wide, want at most 1");
        }

        if (wealth == "rich")
        {
            if (installed < 1)
            {
                throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"rich colony installed no elective bionic after {SurgeryRun.ServedTicks} served ticks"));
            }
        }
        else if (installed != 0 || queued != 0)
        {
            throw new AcceptanceException($"poor colony installed {installed} and queued {queued} elective bionics");
        }
    }
}
