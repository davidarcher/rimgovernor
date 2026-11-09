using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;
namespace RimGovernor.Brain.Policy.Methods.Generation;

/// <summary>
/// The rituals the coupled schedule may begin (#4058, epic #3580): one root per due held ritual (<see cref="RitualPricing.Due"/>)
/// per site the game lets it use now (<see cref="RitualSiteRead.Usable"/>), at full attendance of the free colonists the game
/// accepts there (#4057): each behavior role slot takes the accepted pawns, the bound role precept's holders first, up to the
/// slot's maximum; every other accepted pawn spectates; a pawn the game refuses is left out and named. The root's activities are
/// one external task per attendee at the site, started together when the last attendee arrives over the shared route matrix and
/// lasting the behavior's length; the root grants each attendee every outcome memory the pattern's quality model draws, its mood
/// weighted by the outcome's probability at the quality of that site and attendance (<see cref="RitualPricing.Quality"/>,
/// <see cref="RitualPricing.OutcomeWeights"/>), negative outcomes included. The roots of one ritual exclude each other: the
/// forecast picks the site. Nothing is planned while the colony is not known calm, for a ritual whose outcome the catalog does not
/// model, at a site the game refuses, or where a required role finds no accepted pawn or an attendee has no route; each is a named
/// finding. Each row carries the root's begin action and premise, what the review commits (#4059).
/// </summary>
internal sealed record RitualMethodCandidates(ForecastActivity[] Tasks, ForecastActivityOption[] Options, ForecastCommitment[] Roots,
    EquatableArray<ForecastRitualWork> Work, EquatableArray<string> Findings)
{
    internal const string HeldNotCalm = "ritual_held_emergency_or_unknown";
    internal const string NoSite = "ritual_no_site:";
    internal const string PatternMismatch = "ritual_pattern_mismatch:";
    internal const string SiteRefused = "ritual_site_refused:";
    internal const string RoleUnfilled = "ritual_role_unfilled:";
    internal const string NoAttendees = "ritual_no_attendees:";
    internal const string AttendeeRefused = "ritual_attendee_refused:";
    internal const string AttendeeUnreachable = "ritual_attendee_unreachable:";

    internal static readonly RitualMethodCandidates None = new([], [], [], [], []);

    /// <summary>The root of a held ritual at a site, and the exclusive outcome its sites share.</summary>
    internal static string Exclusive(string ritual) => "ritual/" + ritual;

    internal static RitualMethodCandidates Generate(ReviewFacts facts, RitualActivityRead read,
        IReadOnlyDictionary<(string Pawn, string From, string To), ForecastActivityRoute> routes, bool calm)
    {
        var tasks = new List<ForecastActivity>(); var options = new List<ForecastActivityOption>(); var roots = new List<ForecastCommitment>();
        var work = new List<ForecastRitualWork>(); var findings = new SortedSet<string>(StringComparer.Ordinal);
        if (facts.Ideology is not IdeologyMode.Installed { Primary: var ideo }) return None;
        foreach (var held in ideo.Facts.Rituals.OrderBy(r => r.Id, StringComparer.Ordinal))
        {
            if (!ideo.Defs.Rituals.TryGetValue(held.Pattern, out var def) || !RitualPricing.Due(def, held, facts.Tick)) continue;
            if (!calm) { findings.Add(HeldNotCalm); continue; }
            if (def.Outcome is not { } model || model.Unread.Length != 0)
            {
                findings.Add(RitualPricing.RitualUnmodelled + (def.Outcome is { Unread.Length: > 0 } unmodelled ? unmodelled.Unread : held.Pattern));
                continue;
            }
            var sites = read.Sites.Where(s => s.Ritual == held.Id).ToArray();
            if (sites.Length == 0) { findings.Add(NoSite + held.Id); continue; }
            foreach (var site in sites)
            {
                if (site.Pattern != held.Pattern) { findings.Add(PatternMismatch + held.Id + ":" + site.Site); continue; }
                if (!site.Usable) { findings.Add(SiteRefused + held.Id + ":" + site.Site + ":" + site.Refusal); continue; }
                Plan(ideo, held, def, model, site);
            }
        }
        return new(tasks.ToArray(), options.ToArray(), roots.ToArray(), EquatableArray.From(work), EquatableArray.From(findings));

        void Plan(Ideoligion ideo, IdeoRitual held, RitualDef def, RitualOutcomeModel model, RitualSiteRead site)
        {
            var root = Exclusive(held.Id) + "/" + site.Site;
            var used = new HashSet<string>(StringComparer.Ordinal);
            var filled = new List<string>();
            var fills = new List<RitualSlot>();
            foreach (var slot in def.Roles)
            {
                var holders = ideo.Facts.Roles.Where(r => r.Active && r.DefName == slot.Precept).SelectMany(r => r.Pawns).Select(p => p.Value).ToHashSet(StringComparer.Ordinal);
                var fill = site.Candidates.Where(c => !used.Contains(c.Pawn) && c.Roles.Any(v => v.Slot == slot.Id && v.Assignable))
                    .OrderByDescending(c => slot.Precept.Length != 0 && holders.Contains(c.Pawn)).ThenBy(c => c.Pawn, StringComparer.Ordinal)
                    .Take(Math.Max(1, slot.MaxCount)).Select(c => c.Pawn).ToList();
                if (fill.Count == 0)
                {
                    if (slot.Required) { findings.Add(RoleUnfilled + held.Id + ":" + site.Site + ":" + slot.Id); return; }
                    continue;
                }
                used.UnionWith(fill);
                filled.AddRange(fill);
                fills.Add(new RitualSlot(slot.Id, EquatableArray.From(fill.Select(p => new PawnId(p)))));
            }
            var spectators = new List<string>();
            foreach (var candidate in site.Candidates.Where(c => !used.Contains(c.Pawn)).OrderBy(c => c.Pawn, StringComparer.Ordinal))
            {
                if (candidate.Spectate.Assignable) spectators.Add(candidate.Pawn);
                else findings.Add(AttendeeRefused + held.Id + ":" + site.Site + ":" + candidate.Pawn + ":" + candidate.Spectate.Reason);
            }
            var attendees = filled.Concat(spectators).ToList();
            if (attendees.Count == 0) { findings.Add(NoAttendees + held.Id + ":" + site.Site); return; }
            var location = ForecastLocations.Of(site.Location);
            var start = 0d;
            foreach (var pawn in attendees)
            {
                if (routes.TryGetValue((pawn, "start", location), out var route) && route.Ticks is RouteTicks.Reachable reachable) start = Math.Max(start, reachable.Ticks);
                else { findings.Add(AttendeeUnreachable + held.Id + ":" + site.Site + ":" + pawn); return; }
            }
            var quality = RitualPricing.Quality(model, attendees.Count, site.Impressiveness, site.Cleanliness, site.Indoors);
            var weights = RitualPricing.OutcomeWeights(model, quality);
            var memories = new List<ForecastMoodMemory>();
            for (var i = 0; i < weights.Length; i++)
            {
                var chance = model.Chances[i];
                if (weights[i] <= 0 || chance.Memory.Length == 0) continue;
                var memory = new NeedMemory(chance.Memory, chance.Mood * weights[i], new MemoryLife.Expires(chance.DurationDays * GameTime.TicksPerDay), chance.Fades) { StackLimit = chance.StackLimit };
                memories.AddRange(attendees.Select(pawn => new ForecastMoodMemory(pawn, memory)));
            }
            // The begin the review commits (#4059): the organizer is the first pawn of the first filled slot, as the planner's begin was.
            var begin = Ritual.NewRitualBegin(new PawnId(filled.Count != 0 ? filled[0] : attendees[0]), held.Id, site.Location, fills, spectators.Select(p => new PawnId(p)));
            var due = held.ActiveObligations > 0 ? "obligation"
                : FormattableString.Invariant($"{(facts.Tick.Value - held.LastFinishedTick) / GameTime.TicksPerDay:0.#} days since last");
            var premise = FormattableString.Invariant($"{held.Pattern} due ({due}) at {site.Site}, {attendees.Count} attending, quality {quality:0.00}");
            var ids = new List<string>();
            foreach (var pawn in attendees)
            {
                var id = root + "/" + pawn;
                ids.Add(id);
                tasks.Add(new(id, location, location, []) { ChoiceIdentity = Exclusive(held.Id) + "/" + pawn, NotBefore = start, LatestStart = start });
                options.Add(new(id, pawn, def.DurationTicks, 0) { ServicePosition = new ServicePositionRead.Located(site.Location) });
                work.Add(new(root, id, pawn) { Begin = begin, Premise = premise });
            }
            roots.Add(new(root, []) { ActivityCompletions = EquatableArray.From(ids), ExclusiveOutcome = Exclusive(held.Id), MoodMemories = EquatableArray.From(memories) });
        }
    }
}
