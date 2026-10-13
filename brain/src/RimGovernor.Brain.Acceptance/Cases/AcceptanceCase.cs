using System.Globalization;
using System.Text.RegularExpressions;

namespace RimGovernor.Brain.Acceptance;

/// <summary>A case's declared crew: the colonists it holds at least. Ordinary leaves the pawns as the start made them, with a Reason.</summary>
public sealed record Crew
{
    public int Size { get; init; }

    public bool Ordinary { get; init; }
}

/// <summary>
/// One acceptance case (Go <c>cases.AcceptanceCase</c>): a case declares what it needs (how the game starts, whether the
/// storyteller is quiet, which needs stay unfrozen, whether a service runs, its budget) and the shared runner owns
/// boot, keep/reuse, quiet and freeze defaults, timing and the report. The Run body is the assertion only.
/// </summary>
public sealed record AcceptanceCase
{
    /// <summary>The largest Budget a case may declare without a Reason: past ~15 minutes the precondition is not staged well enough or the assertion covers too much.</summary>
    public static readonly TimeSpan MaxBudget = TimeSpan.FromMinutes(15);

    /// <summary>A lab contract case's Budget: 10-30 s of assertion on a kept process, plus a cold boot and the cached start's first generation.</summary>
    public static readonly TimeSpan LabBudget = TimeSpan.FromMinutes(3);

    // "<area>/<case>": lowercase words joined by hyphens on each side of one slash (case names also accept underscores).
    private static readonly Regex NameShape = new(
        "^[a-z0-9]+(-[a-z0-9]+)*/[a-z0-9]+([-_][a-z0-9]+)*$",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        TimeSpan.FromSeconds(1));

    /// <summary>Explicitly selects a DLC profile for a programmatic fixture; such a case owns a fresh process.</summary>
    public IReadOnlyList<string> Expansions { get; init; } = [];

    /// <summary>"&lt;area&gt;/&lt;case&gt;", unique across the registry.</summary>
    public string Name { get; init; } = "";

    /// <summary>The report's one-line description of what passing proves.</summary>
    public string Scope { get; init; } = "";

    /// <summary>How the game reaches the case's precondition.</summary>
    public Start? Start { get; init; }

    /// <summary>Fixture operations called by Run or service hooks.</summary>
    public IReadOnlyList<string> RequiredOps { get; init; } = [];

    /// <summary>Defaults to <see cref="QuietMode.Required"/>; a Loud (or IfAvailable) case declares it and says why in Reason.</summary>
    public QuietMode Quiet { get; init; } = QuietMode.Required;

    /// <summary>Why the case departs from the checklist's defaults: a storyteller that is not quieted, a DebugStart bigger than the default or a Budget past MaxBudget. Lint requires it for any of the three.</summary>
    public string Reason { get; init; } = "";

    /// <summary>Marks the game quiet-world: under test acceleration, wild plants and animals outside the home area stop ticking and the wild spawners stop.</summary>
    public bool QuietWorld { get; init; }

    /// <summary>The NeedDef names (Food, Rest, Joy...) left unfrozen; everything else is frozen before Run.</summary>
    public IReadOnlyList<string> Keep { get; init; } = [];

    /// <summary>Null for a native-only case.</summary>
    public ServeSpec? Serve { get; init; }

    /// <summary>Marks a case whose Run hosts the in-mod brain service itself (a Serve spec implies it): the suite schedules it after the native-only cases.</summary>
    public bool Service { get; init; }

    /// <summary>The colonists the case runs with, declared upfront; the runner staffs the opened game with it before Run.</summary>
    public Crew Crew { get; init; } = new();

    /// <summary>Fails the run when exceeded, distinct from the -timeout safety net. Every case declares one, at most <see cref="MaxBudget"/>.</summary>
    public TimeSpan Budget { get; init; }

    /// <summary>Replaces the shared default stall for the case's waits; zero is the shared budget.</summary>
    public TimeSpan Stall { get; init; }

    /// <summary>
    /// The interruption letters the case expects, as {label, letterDef} pairs in AdvanceGame's order. Null leaves the
    /// lenient default; a non-null list, even empty, makes every advance a strict window over exactly these letters.
    /// </summary>
    public IReadOnlyList<(string Label, string LetterDef)>? Letters { get; init; }

    /// <summary>The staging blocks the Run body wraps in a stage call, in order; every name is unique and non-empty; an Owned case declares none.</summary>
    public IReadOnlyList<string> Stages { get; init; } = [];

    /// <summary>The assertion, or its scenario when <see cref="Postmortem"/> is set.</summary>
    public Func<ISession, CancellationToken, Task>? Run { get; init; }

    /// <summary>The case's read-and-assert phase, called after Run returns with every service stopped.</summary>
    public Func<ISession, CancellationToken, Task>? Postmortem { get; init; }

    /// <summary>Stops the process after Run instead of leaving it at the main menu for the next case.</summary>
    public bool NoKeep { get; init; }

    /// <summary>Opens the windowed profile whatever -headless says, for cases that need a drawing game.</summary>
    public bool Rendered { get; init; }

    /// <summary>Keeps a graphics device in the headless profile, for a case that renders on demand without a window; implies a fresh process.</summary>
    public bool Graphics { get; init; }

    /// <summary>Opts the case out of the runner's checkpoint ring.</summary>
    public bool NoCheckpoint { get; init; }

    /// <summary>Puts the case in the matrix class: a throughput or scheduler measurement, or a DLC-save case, run on demand, never by a tier.</summary>
    public bool Matrix { get; init; }

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
        if (Expansions.Count > 0 && !NoKeep)
        {
            throw new AcceptanceException($"case {Name} with expansions must set NoKeep");
        }

        foreach (string expansion in Expansions)
        {
            _ = Acceptance.Expansions.ExpansionPackage(expansion);
        }

        if (Name.Length == 0)
        {
            throw new AcceptanceException("case has no name");
        }

        if (Start is null)
        {
            throw new AcceptanceException($"case {Name} has no Start");
        }

        if (Run is null)
        {
            throw new AcceptanceException($"case {Name} has no Run");
        }

        if (Start is OwnedStart && !NoKeep)
        {
            throw new AcceptanceException($"case {Name} owns its process lifecycle (Owned) and must declare NoKeep");
        }

        if (Start is OwnedStart && Stages.Count > 0)
        {
            throw new AcceptanceException($"case {Name} owns its process lifecycle (Owned) and cannot declare Stages");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string name in Stages)
        {
            if (name.Length == 0 || name.AsSpan().IndexOfAny('/', '\\') >= 0 || !seen.Add(name))
            {
                throw new AcceptanceException($"case {Name}: stage \"{name}\" must be a unique, non-empty name without path separators");
            }
        }
    }

    /// <summary>
    /// Holds the case to the performance checklist in docs/developers/testing/choose-tests.md: what the runner cannot
    /// make true by construction it refuses to run. Every violation is returned, each naming the rule and the checklist
    /// item, so a new case fixes them in one round. An empty list means the case passes.
    /// </summary>
    public IReadOnlyList<string> Lint()
    {
        var errors = new List<string>();
        void Fail(string rule, int item, string title) =>
            errors.Add(string.Create(CultureInfo.InvariantCulture, $"case \"{Name}\": {rule} (checklist item {item}, \"{title}\")"));

        try
        {
            Validate();
        }
        catch (AcceptanceException e)
        {
            errors.Add(e.Message);
        }

        if (!NameShape.IsMatch(Name))
        {
            errors.Add($"case \"{Name}\": Name is not <area>/<case> (lowercase words and hyphens on each side of one slash)");
        }

        if (Budget <= TimeSpan.Zero)
        {
            Fail("Budget is missing: every case declares the wall clock it needs", 6, "Budget in minutes and say so");
        }
        else if (Budget > MaxBudget && Reason.Length == 0)
        {
            Fail(string.Create(CultureInfo.InvariantCulture, $"Budget {Budget} exceeds {MaxBudget}: stage the precondition, split the assertion or give a Reason"), 6, "Budget in minutes and say so");
        }

        if (Start is not OwnedStart)
        {
            if (Crew.Size <= 0)
            {
                errors.Add($"case \"{Name}\": Crew is missing: every case declares the colonists it runs with");
            }
            else if (Crew.Ordinary && Reason.Length == 0)
            {
                errors.Add($"case \"{Name}\": Crew is Ordinary without a Reason: superpawns are the default");
            }
        }

        if (Quiet != QuietMode.Required && Reason.Length == 0)
        {
            Fail($"Quiet is {Quiet} without a Reason: only an assertion about an interruption keeps the storyteller", 4, "Quiet by default");
        }

        if (Start is DebugStart d && Reason.Length == 0
            && (d.Size.MapSize > DebugStartSize.DefaultMapSize || d.Size.PlanetCoverage > DebugStartSize.DefaultPlanetCoverage))
        {
            Fail(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"DebugStart {d.Size.MapSize}x{d.Size.MapSize} at {d.Size.PlanetCoverage:R} planet coverage is bigger than the default {DebugStartSize.DefaultMapSize}x{DebugStartSize.DefaultMapSize} at {DebugStartSize.DefaultPlanetCoverage:R} without a Reason"),
                2,
                "Small map, tiny planet");
        }

        if (Serve is not null)
        {
            switch (Start)
            {
                case SaveStart or FixtureStart or LabStart:
                    break;
                case ScenarioStart:
                    // A fresh scenario start is the precondition of a whole-game review run, which must say why.
                    if (Reason.Length == 0)
                    {
                        Fail("Serve declared on a Scenario start without a Reason", 1, "Open on the precondition");
                    }

                    break;
                default:
                    Fail("Serve declared on a bare DebugStart: a serve-driven case opens on a committed save, the lab or a fixture op", 1, "Open on the precondition");
                    break;
            }
        }

        return errors;
    }
}
