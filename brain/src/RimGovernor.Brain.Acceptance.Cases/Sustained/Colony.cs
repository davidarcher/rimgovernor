using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// The sustained colony diagnostics (Go <c>sustained/colony.go</c>): every routine family on the baseline save for
/// <see cref="ColonyWindow"/> game ticks, and the nutrition and re-acquisition audits the colony cases share.
/// </summary>
internal static class SustainedColonyCase
{
    /// <summary>
    /// The colony diagnostics' sample window in game ticks: five game days by default (60000 ticks a day), the
    /// shortest stretch on which the foothold goals settle and the maintenance tier gets a turn.
    /// </summary>
    internal const ulong ColonyWindowTicks = 5 * 60000;

    /// <summary>The environment variable that overrides <see cref="ColonyWindowTicks"/> with a tick count (e.g. 900000 for fifteen days).</summary>
    internal const string ColonyWindowTicksEnv = "RIMGOVERNOR_ACCEPT_WINDOW_TICKS";

    /// <summary>The worst Malnutrition severity the window may leave on any colonist: 0.3 is "hungry" on the health tab.</summary>
    private const double MalnutritionLimit = 0.3;

    /// <summary><see cref="ColonyWindowTicksEnv"/> when set and valid, else <see cref="ColonyWindowTicks"/>.</summary>
    internal static ulong ColonyWindow()
    {
        var raw = Environment.GetEnvironmentVariable(ColonyWindowTicksEnv);
        if (!string.IsNullOrEmpty(raw) && ulong.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0)
        {
            return n;
        }

        return ColonyWindowTicks;
    }

    /// <summary>
    /// The Concerns every colony sample reads beside the food/supply Need: the foothold gates and the first
    /// maintenance-tier projects, so the timeline shows which one stalls, thrashes or starves the others.
    /// </summary>
    internal static readonly IReadOnlyList<string> ColonyConcerns =
    [
        PolicyFunctions.MaintainHousing.Value,
        PolicyFunctions.CookingNeed.Value,
        PolicyFunctions.EnsureTemperatureSafety.Value,
        PolicyFunctions.MaintainResource.Value,
        PolicyFunctions.EnsureWorkAssignments.Value,
        MilitaryFamily.Defense.Value,
        EconomyFamily.Research.Value,
        PolicyFunctions.EnsureComfort.Value,
        HomeUpkeepFamily.Repairs.Value,
    ];

    internal static AcceptanceCase Colony(string name, QuietMode quiet, string scope) => new()
    {
        Name = name,
        Scope = scope,
        Start = new SaveStart { Name = SustainedFoodCase.BaselineSave },
        Keep = [NeedDef.LiveNeeds.Value],
        Quiet = quiet,
        Serve = new ServeSpec
        {
            NativeTimeout = TimeSpan.FromSeconds(15),
            StepStall = TimeSpan.FromSeconds(90),
            Prefix = "sustained-colony",
        },

        // The whole point is a long window with every family live: the checklist's minute-scale, single-family,
        // frozen-needs defaults are what this diagnostic exists to look past. Budget covers the wall ceiling plus
        // load, acquire, stop and reattach.
        Reason = "sustained multi-day diagnostic over every routine family with live needs" + (quiet == QuietMode.Loud ? " and the save's storyteller" : ""),
        Budget = SustainedFoodCase.Window() + TimeSpan.FromMinutes(7),
        Crew = new Crew { Size = 3 },
        Run = async (s, cancellationToken) =>
        {
            _ = await SustainedFood.ObserveAsync(
                ObservedSession.Of(s),
                new SustainedObservation
                {
                    Watch = new WatchConfig
                    {
                        Watch = SustainedFoodCase.Window(),
                        Window = ColonyWindow(),
                        Poll = TimeSpan.FromSeconds(10),
                        Concern = PolicyFunctions.FoodSupplyNeed.Value,
                        Extra = ColonyConcerns,

                        // A diagnostic, not a gate: a refusal is part of what the long window records, never a
                        // reason to cut it short.
                        FailFast = new FailFast { Disabled = true },
                    },
                    Audit = async (h, report, ct) =>
                    {
                        AuditReacquisitions(report);
                        var failure = SustainedFoodCase.Join(
                        [
                            await SustainedFoodCase.CaptureAsync(() => AuditNutrition(h, report, ct)).ConfigureAwait(false),
                            await SustainedFoodCase.CaptureAsync(() => SustainedStockpiles.AuditAsync(s, report, ct)).ConfigureAwait(false),
                        ]);
                        if (failure is not null)
                        {
                            throw failure;
                        }
                    },
                },
                cancellationToken).ConfigureAwait(false);
        },
    };

    /// <summary>
    /// The food gate the diagnostic keeps: the tribal8 baseline holds under two days of pemmican, so a window that
    /// ends with a colonist past hungry means the foothold food methods (butcher spot, bill, hunting, fields) did not
    /// stack in time. The report keeps every colonist's worst nutrition hediff either way.
    /// </summary>
    internal static async Task AuditNutrition(Harness h, Report report, CancellationToken cancellationToken)
    {
        var listed = await Colonists.ListColonistsAsync(h, "audit-nutrition", false, true, cancellationToken).ConfigureAwait(false);
        var rows = new JsonArray();
        var over = new List<string>();
        foreach (var row in listed)
        {
            var worst = 0.0;
            foreach (var entry in row["health"].Member("hediffs").AsSlice())
            {
                if (entry.AsMap() is { } hediff && Colonists.HediffDef(hediff) == "Malnutrition")
                {
                    worst = Math.Max(worst, hediff["severity"].AsNumber());
                }
            }

            var id = Colonists.RowID(row);
            rows.Add(new JsonObject { ["pawn"] = id, ["malnutrition"] = worst });
            if (worst > MalnutritionLimit)
            {
                over.Add(string.Create(CultureInfo.InvariantCulture, $"{id} {worst:F2}"));
            }
        }

        report["colonist_nutrition"] = new JsonObject { ["colonists"] = rows.Count, ["limit"] = MalnutritionLimit, ["rows"] = rows };
        if (over.Count > 0)
        {
            throw new AcceptanceException(string.Create(
                CultureInfo.InvariantCulture,
                $"{over.Count} colonist(s) ended the window past Malnutrition {MalnutritionLimit:F1}: [{string.Join(' ', over)}]"));
        }
    }

    /// <summary>
    /// The one gate the colony diagnostic keeps: every keep-alive resume from an acknowledged hold re-acquires in
    /// place, one native generation each, not the Manual-to-Auto pair that rebound every prepared action to a new
    /// generation.
    /// </summary>
    internal static void AuditReacquisitions(Report report)
    {
        if (report["authority_reacquisitions"].AsMap() is not { } keep)
        {
            return;
        }

        var over = keep["generations_over_one"].AsNumber();
        if (over > 0)
        {
            throw new AcceptanceException(
                $"{Num(over)} of {keep["reacquired"].ToJson()} keep-alive resumes cost more than one native generation (advance {keep["generation_advance"].ToJson()} over {keep["attempts"].ToJson()} attempts, {keep["acknowledged"].ToJson()} holds acknowledged)");
        }
    }

    private static string Num(double value) => value.ToString(CultureInfo.InvariantCulture);
}
