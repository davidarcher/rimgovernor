using System.Globalization;
using System.Text;

namespace RimGovernor.Brain.Domain;

/// <summary>One work type and its manual priority (0 disabled, 1 first to <see cref="DomainConstants.MaxPriority"/> last).</summary>
public readonly record struct WorkSetting(string Definition, int Priority);

public static partial class DomainConstants
{
    /// <summary>The timetable length: one TimeAssignmentDef per hour. Kept: a game day is fixed at 24 hours.</summary>
    public const int ScheduleHours = 24;

    /// <summary>
    /// The top of the 0-4 priority range: RimWorld's manual work priorities run 1 (first) to 4 (last) with 0
    /// disabled, and Concern priorities (Standard, Incident) share the scale. Kept as the one range constant.
    /// </summary>
    public const int MaxPriority = 4;
}

/// <summary>
/// An immutable work-settings write. An assignment may carry work priorities, an allowed-area assignment
/// (named or an explicit clear), a 24-hour timetable, or any of them together; the native WorkSettingsIntent
/// admits the fields alone or combined, so this type mirrors that at the domain boundary rather than splitting
/// into further action kinds. Settings are sorted by definition and free of duplicates.
/// </summary>
public sealed record WorkAssignment : IActionPayload
{
    private WorkAssignment(PawnId pawn, IEnumerable<WorkSetting> settings, bool hasArea, bool areaClear, string areaId, IReadOnlyList<string> schedule)
    {
        DomainException.Require(pawn.IsValid, "invalid work assignment");
        var rows = settings.OrderBy(s => s.Definition, StringComparer.Ordinal).ToList();
        DomainException.Require(rows.Count != 0 || hasArea || schedule.Count != 0, "invalid work assignment");
        if (schedule.Count > 0)
        {
            DomainException.Require(schedule.Count == DomainConstants.ScheduleHours && schedule.All(Identity.IsValidText), "invalid schedule assignment");
        }
        if (hasArea)
        {
            DomainException.Require(!(areaClear && areaId.Length != 0), "invalid area assignment");
            DomainException.Require(areaClear || Identity.IsValidText(areaId), "invalid area assignment");
        }
        else
        {
            DomainException.Require(!areaClear && areaId.Length == 0, "invalid area assignment");
        }
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            DomainException.Require(
                Identity.IsValidText(row.Definition)
                    && row.Priority >= 0
                    && row.Priority <= DomainConstants.MaxPriority
                    && !(i > 0 && string.Equals(rows[i - 1].Definition, row.Definition, StringComparison.Ordinal)),
                "invalid work priority"
            );
        }
        Pawn = pawn;
        Settings = EquatableArray.From(rows);
        HasArea = hasArea;
        AreaClear = hasArea && areaClear;
        Area = areaId;
        Schedule = EquatableArray.From(schedule);
    }

    /// <summary>Go <c>NewWorkAssignment</c>.</summary>
    public static WorkAssignment NewWorkAssignment(PawnId pawn, IEnumerable<WorkSetting> settings) => new(pawn, settings, false, false, "", []);

    /// <summary>
    /// Go <c>NewScheduleAssignment</c>: builds a timetable write, alone or beside work priorities (settings may
    /// be empty); <paramref name="schedule"/> holds one TimeAssignmentDef name per hour, hour 0 first.
    /// </summary>
    public static WorkAssignment NewScheduleAssignment(PawnId pawn, IEnumerable<WorkSetting> settings, IReadOnlyList<string> schedule)
    {
        DomainException.Require(schedule.Count != 0, "invalid schedule assignment");
        return new WorkAssignment(pawn, settings, false, false, "", schedule);
    }

    /// <summary>
    /// Go <c>NewAreaAssignment</c>: builds a work-free allowed-area assignment. <paramref name="clear"/> requests
    /// removing any area restriction; otherwise <paramref name="area"/> names the target area's identity.
    /// </summary>
    public static WorkAssignment NewAreaAssignment(PawnId pawn, bool clear, string area) => new(pawn, [], true, clear, area, []);

    public PawnId Pawn { get; }

    public EquatableArray<WorkSetting> Settings { get; }

    /// <summary>Whether this assignment carries an allowed-area change.</summary>
    public bool HasArea { get; }

    /// <summary>Whether that change clears the restriction (only meaningful when <see cref="HasArea"/>).</summary>
    public bool AreaClear { get; }

    /// <summary>The target area (empty when clearing or when there is no area change).</summary>
    public string Area { get; }

    /// <summary>Whether this assignment writes the timetable.</summary>
    public bool HasSchedule => Schedule.Count > 0;

    /// <summary>The hour-indexed TimeAssignmentDef list (empty otherwise).</summary>
    public EquatableArray<string> Schedule { get; }
}

public sealed partial record DomainAction
{
    public static DomainAction NewWorkAssignmentAction(ActionId id, WorkAssignment work) => Create(id, ActionKind.WorkAssignment, work);

    public WorkAssignment? WorkAssignment => PayloadOf<WorkAssignment>(ActionKind.WorkAssignment);
}
