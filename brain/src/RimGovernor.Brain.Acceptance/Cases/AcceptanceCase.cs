using System.Text.RegularExpressions;

namespace RimGovernor.Brain.Acceptance;

/// <summary>A case's declared crew: the colonists it holds at least. Ordinary leaves the pawns as the start made them, which the case's claim must need.</summary>
public sealed record Crew
{
    public int Size { get; init; }

    public bool Ordinary { get; init; }
}

/// <summary>
/// The runner's record of one acceptance case, which only <see cref="TypedCase.ToCase"/> produces: what the shared
/// runner needs to boot, keep or reuse the game, quiet and freeze it, staff it, time the body and report it. Cases are
/// authored as <see cref="TypedCase"/>; nothing outside the runner constructs this record.
/// </summary>
public sealed record AcceptanceCase
{
    // "<area>/<case>": lowercase words joined by hyphens on each side of one slash (case names also accept underscores).
    internal static readonly Regex NameShape = new(
        "^[a-z0-9]+(-[a-z0-9]+)*/[a-z0-9]+([-_][a-z0-9]+)*$",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        TimeSpan.FromSeconds(1));

    internal AcceptanceCase()
    {
    }

    /// <summary>Explicitly selects a DLC profile; such a case owns a fresh process.</summary>
    public IReadOnlyList<string> Expansions { get; init; } = [];

    /// <summary>"&lt;area&gt;/&lt;case&gt;", unique across the registry.</summary>
    public string Name { get; init; } = "";

    /// <summary>The report's one-line description of what passing proves.</summary>
    public string Scope { get; init; } = "";

    /// <summary>How the game reaches the case's precondition.</summary>
    public Start? Start { get; init; }

    /// <summary>Fixture operations called by Run or service hooks.</summary>
    public IReadOnlyList<string> RequiredOps { get; init; } = [];

    /// <summary>Required unless the case's start claims its own storyteller (<see cref="StartClaim.KeepsStoryteller"/>).</summary>
    public QuietMode Quiet { get; init; } = QuietMode.Required;

    /// <summary>Marks the game quiet-world: under test acceleration, wild plants and animals outside the home area stop ticking and the wild spawners stop.</summary>
    public bool QuietWorld { get; init; }

    /// <summary>The NeedDef names (Food, Rest, Joy...) left unfrozen; everything else is frozen before Run.</summary>
    public IReadOnlyList<string> Keep { get; init; } = [];

    /// <summary>Null for a native-only case.</summary>
    public ServeSpec? Serve { get; init; }

    /// <summary>The colonists the case runs with, declared upfront; the runner staffs the opened game with it before Run.</summary>
    public Crew Crew { get; init; } = new();

    /// <summary>The hang guard (<see cref="TypedCase.HangGuard"/>): the wall time after which the body is cut as hung.</summary>
    public TimeSpan Budget { get; init; }

    /// <summary>The assertion over the runner's session (<see cref="TypedSession"/> around it).</summary>
    internal Func<CaseSession, CancellationToken, Task>? Run { get; init; }

    /// <summary>The case's read-and-assert phase, called after Run returns with every service stopped.</summary>
    internal Func<CaseSession, CancellationToken, Task>? Postmortem { get; init; }

    /// <summary>Stops the process after Run instead of leaving it at the main menu for the next case.</summary>
    public bool NoKeep { get; init; }

    /// <summary>The pass that runs the case (charter rule 8).</summary>
    public CaseTier Tier { get; init; }

    /// <summary>The typed case this record adapts for the runner.</summary>
    public required TypedCase Typed { get; init; }

    /// <summary>Whether the runner staffs the case's game with its crew.</summary>
    public bool Staffed => Start is not OwnedStart && !Crew.Ordinary;

    /// <summary><see cref="Keep"/> as the lifecycle's need names (Go <c>keepNeeds</c>).</summary>
    public IReadOnlyList<NeedDef> KeepNeeds() => [.. Keep.Select(need => new NeedDef(need))];

    /// <summary>
    /// The test ops the case's Start calls, outermost last: what the installed build must register for the case to start,
    /// followed by RequiredOps used by Run or service hooks.
    /// </summary>
    public IReadOnlyList<string> FixtureOps()
    {
        var ops = new List<string>();
        for (Start? start = Start; start is FixtureStart f; start = f.On)
        {
            ops.Insert(0, f.Op);
        }

        ops.AddRange(RequiredOps);
        return ops;
    }

    /// <summary>The shape check Register applies; throws <see cref="AcceptanceException"/> on the first violation.</summary>
    public void Validate()
    {
        foreach (string expansion in Expansions)
        {
            _ = Acceptance.Expansions.ExpansionPackage(expansion);
        }

        if (Start is null)
        {
            throw new AcceptanceException($"case {Name} has no Start");
        }

        if (Run is null)
        {
            throw new AcceptanceException($"case {Name} has no Run");
        }

        if (Start is not OwnedStart && Crew.Size <= 0)
        {
            throw new AcceptanceException($"case {Name}: Crew is missing: every case declares the colonists it runs with");
        }

        if (Serve is not null && Start is not (SaveStart or FixtureStart or LabStart or ScenarioStart))
        {
            throw new AcceptanceException($"case {Name}: Serve declared on a bare DebugStart: a serve-driven case opens on a committed save, the lab or a staged precondition");
        }
    }
}
