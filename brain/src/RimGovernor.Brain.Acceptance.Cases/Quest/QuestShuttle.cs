using Operations = RimGovernor.Contracts.Wire.Operations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>quest/shuttle-*</c>: the QuestShuttleIntent arm of operations_apply on an accepted quest's waiting shuttle, one
/// case per loading mode. Autoload on a shuttle naming its pawn, or an explicit pawn list on one taking any colonist,
/// has vanilla EnterTransporter work board exactly that pawn, as native's quest shuttle row reads; a launch then sends
/// the loaded shuttle off the map, and its row leaves the quest.
/// </summary>
internal static class QuestShuttle
{
    private const int BoardTicks = 6000;
    private const int DepartTicks = 3000;

    internal static IEnumerable<TypedCase> Cases() =>
    [
        Case("quest/shuttle-autoload", "autoload on a shuttle naming its pawn", named: true),
        Case("quest/shuttle-explicit", "an explicit pawn list on a shuttle taking any colonist", named: false),
    ];

    private static TypedCase Case(string name, string mode, bool named) => new()
    {
        Name = name,
        Scope = $"Native QuestShuttleIntent: {mode} boards exactly that pawn by vanilla EnterTransporter work, and a launch sends the loaded shuttle off the map.",
        Ticks = BoardTicks + DepartTicks,
        Tier = CaseTier.OnDemand,
        NoKeep = true,
        Expansions = ["ludeon.rimworld.royalty"],
        Ops = [QuestKit.ShuttleTool],
        Run = (s, ct) => RunAsync(s, named, ct),
    };

    private static async Task RunAsync(TypedSession s, bool named, CancellationToken cancellationToken)
    {
        var prepared = await s.FixtureAsync("prepare", QuestKit.Shuttle, NoArgs.Value, cancellationToken).ConfigureAwait(false);
        var (quest, pawn) = named ? (prepared.NamedQuest, prepared.NamedPawn) : (prepared.CountedQuest, prepared.CountedPawn);
        await TypedAuthority.GrantAutoAsync(s, cancellationToken).ConfigureAwait(false);

        async Task Apply(string key, Operations.QuestShuttleIntent intent)
        {
            var effect = TradeKit.Applied(key, await TradeKit.ApplyAsync(s, key, intent, cancellationToken).ConfigureAwait(false)).QuestShuttle;
            var loading = intent.Launch != true;
            if (effect?.QuestId != quest || (loading && named && effect.Autoload != true) || (loading && !named && !effect.PawnIds.Contains(pawn)))
            {
                throw new AcceptanceException($"{key}: expected a quest shuttle effect on {quest}{(named ? " with autoload on" : $" loading {pawn}")}, got {effect}");
            }
        }

        await Apply("shuttle-load", new Operations.QuestShuttleIntent
        {
            QuestId = quest,
            Loading = named ? new Operations.QuestShuttleIntentLoading.AutoloadCase(true) : new Operations.ShuttlePawnList { PawnIds = [pawn] }, Launch = false }).ConfigureAwait(false);

        _ = await s.RunUntilAsync("shuttle-board", BoardTicks, async ct =>
        {
            var row = await QuestKit.QuestAsync(s, "shuttle-board-read", quest, ct).ConfigureAwait(false);
            var shuttle = row?.Shuttles.Count == 1 ? row.Shuttles[0] : throw new AcceptanceException($"shuttle-board: expected the quest's one shuttle, got {row}");
            s.RecordWire("shuttle", shuttle);
            if (shuttle.AllRequiredLoaded && !shuttle.LoadedPawnIds.Contains(new RimGovernor.Brain.Domain.PawnId(pawn)))
            {
                throw new AcceptanceException($"shuttle-board: loaded without {pawn}: {shuttle}");
            }

            return ($"{shuttle.AllRequiredLoaded} {string.Join(",", shuttle.LoadedPawnIds.Select(p => p.Value))}", shuttle.AllRequiredLoaded);
        }, cancellationToken).ConfigureAwait(false);

        await Apply("shuttle-launch", new Operations.QuestShuttleIntent { QuestId = quest, Launch = true }).ConfigureAwait(false);
        _ = await s.RunUntilAsync("shuttle-depart", DepartTicks, async ct =>
        {
            var row = await QuestKit.QuestAsync(s, "shuttle-depart-read", quest, ct).ConfigureAwait(false);
            var shuttles = row?.Shuttles.Count ?? 0;
            return ($"{shuttles} shuttles", shuttles == 0);
        }, cancellationToken).ConfigureAwait(false);
    }
}
