namespace RimGovernor.Brain.Runtime;

/// <summary>One Need owner's open field demand, kept with its journal owner only in orchestration.</summary>
internal sealed record FieldShortfall(NeedOwner Owner, IReadOnlyList<FieldBlockOption> Options);
