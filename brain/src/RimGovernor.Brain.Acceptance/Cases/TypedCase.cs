using System.Globalization;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// One acceptance case on the typed API (the charter in docs/developers/testing/acceptance-guide.md): one native claim,
/// a precondition staged by a typed stage spec, a budget in game ticks and a declared tier. The body receives an
/// <see cref="ITypedSession"/>: typed calls and reads, waits on ticks, signatures and events, telemetry by kind. The
/// runner drives it through <see cref="ToCase"/>.
/// </summary>
public sealed record TypedCase
{
    /// <summary>The tick cap of a case that opens on the lab or stages its precondition (charter rule 3).</summary>
    public const int StagedTickCap = 30_000;

    /// <summary>The tick cap of every other case (charter rule 3).</summary>
    public const int TickCap = 60_000;

    /// <summary>The slow-machine game speed the hang guard assumes.</summary>
    public const int HangGuardTicksPerSecond = 300;

    /// <summary>What the hang guard adds to the declared ticks at <see cref="HangGuardTicksPerSecond"/>.</summary>
    public static readonly TimeSpan HangGuardMargin = TimeSpan.FromMinutes(1);

    /// <summary>What the hang guard adds per game a <see cref="StartClaim"/> body launches: a launch and a world generation are long events outside the tick loop.</summary>
    public static readonly TimeSpan LaunchAllowance = TimeSpan.FromMinutes(5);

    /// <summary>"&lt;area&gt;/&lt;case&gt;", unique across the registry.</summary>
    public required string Name { get; init; }

    /// <summary>The one native claim (rule 1) and why a unit test cannot prove it; the report's description of what passing proves.</summary>
    public required string Scope { get; init; }

    /// <summary>The game ticks the claim needs, at most <see cref="Cap"/>; every wait spends from it, and the hang guard derives from it.</summary>
    public required int Ticks { get; init; }

    /// <summary>The pass that runs the case (rule 8).</summary>
    public required CaseTier Tier { get; init; }

    /// <summary>How the game opens before staging: the lab (the default), a debug colony, a prepared save or a scenario; an <see cref="OwnedStart"/> only with <see cref="ClaimsStart"/>.</summary>
    public Start Start { get; init; } = new LabStart();

    /// <summary>
    /// Declares the start itself the case's claim (a new-colony or reusable-game claim): the only way a typed case owns its
    /// process lifecycle (<see cref="OwnedStart"/>, through <see cref="ITypedSession.LaunchAsync"/>). Null for every other case.
    /// </summary>
    public StartClaim? ClaimsStart { get; init; }

    /// <summary>The precondition (rule 2), applied by <c>test/stage</c> on the opened <see cref="Start"/>; its reply is <see cref="ITypedSession.Staged"/>.</summary>
    public Fx.StageSpec? Stage { get; init; }

    /// <summary>The fixture ops the body calls (<see cref="ITypedSession.FixtureAsync"/>, or <c>test/stage</c> for <see cref="ITypedSession.StageAsync"/>).</summary>
    public IReadOnlyList<string> Ops { get; init; } = [];

    /// <summary>The colonists the case runs with: superpawns unless Ordinary, which the Scope's claim must need.</summary>
    public Crew Crew { get; init; } = new() { Size = 3 };

    /// <summary>The NeedDef names (Food, Rest, Joy...) left unfrozen; everything else is frozen before the body.</summary>
    public IReadOnlyList<string> Keep { get; init; } = [];

    /// <summary>Marks the game quiet-world: wild plants and animals outside the home area stop ticking. Not for farm, hunting or husbandry claims.</summary>
    public bool QuietWorld { get; init; }

    /// <summary>A DLC profile the claim requires; the case then owns a fresh process.</summary>
    public IReadOnlyList<string> Expansions { get; init; } = [];

    /// <summary>The body leaves the process on a game the next case did not open (an unload, a reload, unpatched process state), so the runner does not keep it; an expansion profile implies it.</summary>
    public bool NoKeep { get; init; }

    /// <summary>The service <see cref="ITypedSession.ServeUntilAsync"/> hands the clock to; null for a native-only case.</summary>
    public ServeSpec? Serve { get; init; }

    /// <summary>The assertion: production ops and reads over the staged precondition.</summary>
    public required Func<ITypedSession, CancellationToken, Task> Run { get; init; }

    /// <summary>The read-and-assert phase, called after Run returns with every service stopped.</summary>
    public Func<ITypedSession, CancellationToken, Task>? Postmortem { get; init; }

    /// <summary>The tick cap for this case's start: <see cref="StagedTickCap"/> on the lab or a staged start, <see cref="TickCap"/> otherwise.</summary>
    public int Cap => Start is LabStart || Stage is not null ? StagedTickCap : TickCap;

    /// <summary>
    /// The wall time after which the body is cut as hung: Ticks at <see cref="HangGuardTicksPerSecond"/> plus
    /// <see cref="HangGuardMargin"/>, plus <see cref="LaunchAllowance"/> per game a <see cref="ClaimsStart"/> body launches.
    /// A passing claim never fails on wall time.
    /// </summary>
    public TimeSpan HangGuard => TimeSpan.FromSeconds((double)Ticks / HangGuardTicksPerSecond) + HangGuardMargin + (LaunchAllowance * (ClaimsStart?.Launches ?? 0));

    /// <summary>The shape check registration applies; throws <see cref="AcceptanceException"/> on the first violation.</summary>
    public void Validate()
    {
        if (!AcceptanceCase.NameShape.IsMatch(Name))
        {
            throw new AcceptanceException($"case \"{Name}\": Name is not <area>/<case> (lowercase words and hyphens on each side of one slash)");
        }

        if (string.IsNullOrWhiteSpace(Scope))
        {
            throw new AcceptanceException($"case {Name}: Scope must name the one native claim");
        }

        if (Ticks < 0 || Ticks > Cap)
        {
            throw new AcceptanceException(string.Create(
                CultureInfo.InvariantCulture,
                $"case {Name} declares {Ticks} ticks; its start's cap is {Cap}: re-stage the precondition or delete the case"));
        }

        if (StartProblem() is { } problem)
        {
            throw new AcceptanceException($"case {Name}: {problem}");
        }
    }

    /// <summary>Why <see cref="Start"/> and <see cref="ClaimsStart"/> do not fit together, null when they do (the catalog test reads it too).</summary>
    public string? StartProblem() => (Start, ClaimsStart) switch
    {
        (FixtureStart, _) => "a typed case stages its precondition through Stage, not a fixture start",
        (OwnedStart, null) => "a typed case owns its process lifecycle only when the start is its claim (ClaimsStart)",
        (OwnedStart, { Launches: < 1 }) => "an owned start's ClaimsStart declares the games it launches (Launches at least 1)",
        (OwnedStart, { KeepsStoryteller: true }) => "an owned start picks its storyteller in the new-colony spec; KeepsStoryteller is for a ScenarioStart",
        (ScenarioStart, { Launches: > 0 }) => "the runner opens a ScenarioStart; its ClaimsStart launches nothing",
        (not OwnedStart and not ScenarioStart, not null) => "only an owned or scenario start can be the claim",
        _ => null,
    };

    /// <summary>The runner's record of this case, its Run and Postmortem over a <see cref="TypedSession"/>.</summary>
    public AcceptanceCase ToCase() => new()
    {
        Name = Name,
        Scope = Scope,
        Start = Stage is null ? Start : FixtureStage.Start(Stage, Start),
        RequiredOps = Ops,
        QuietWorld = QuietWorld,
        Keep = Keep,
        Crew = Crew,
        Serve = Serve,
        Expansions = Expansions,
        NoKeep = NoKeep || Expansions.Count > 0 || Start is OwnedStart,
        Quiet = ClaimsStart is { KeepsStoryteller: true } ? QuietMode.Loud : QuietMode.Required,

        // The ring skips a slow precondition; a typed case stages its precondition instead of playing into it.
        NoCheckpoint = true,
        Budget = HangGuard,
        Tier = Tier,
        Typed = this,
        Run = (s, ct) => Run(new TypedSession(s, this), ct),
        Postmortem = Postmortem is null ? null : (s, ct) => Postmortem(new TypedSession(s, this), ct),
    };
}

/// <summary>
/// The marker that the start itself is a <see cref="TypedCase"/>'s one claim (charter rule 1): a production new-colony
/// start, a scenario start's parts or the reusable-game lifecycle. It is what lets a typed case declare an
/// <see cref="OwnedStart"/>; the tick cap stays <see cref="TypedCase.TickCap"/>.
/// </summary>
public sealed record StartClaim
{
    /// <summary>The fresh games an owned body launches (<see cref="ITypedSession.LaunchAsync"/>); each adds <see cref="TypedCase.LaunchAllowance"/> to the hang guard. Zero on a ScenarioStart, which the runner opens.</summary>
    public int Launches { get; init; }

    /// <summary>The scenario start's own storyteller is part of the claim, so the runner skips the quiet-storyteller swap.</summary>
    public bool KeepsStoryteller { get; init; }
}
