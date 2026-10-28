namespace RimGovernor.Brain.Domain;

/// <summary>One currently legal native work-giver job, never a promise of future inputs.</summary>
public sealed record OrderedWorkStep(string Job, string Target);

/// <summary>
/// A finite intervention. Sequence is monotonically increasing per pawn across saves.
/// Repeating the same request reads its outcome; cancellation names that same sequence.
/// </summary>
public sealed record OrderedWork(PawnId Pawn, ulong Sequence, string Purpose,
    EquatableArray<OrderedWorkStep> Jobs, bool Cancel = false) : IActionPayload
{
    public void Validate()
    {
        DomainException.Require(IdText.IsValidText(Pawn.Value) && Sequence > 0 && IdText.IsNativeText(Purpose, allowEmpty: false), "ordered work needs a pawn, sequence and physical purpose");
        DomainException.Require(Jobs.Count is > 0 and <= 16, "ordered work needs one to sixteen jobs");
        foreach (var job in Jobs)
            DomainException.Require(IdText.IsNativeText(job.Job, allowEmpty: false)
                && IdText.IsNativeText(job.Target, allowEmpty: false) && job.Target != Pawn.Value,
                "ordered work needs exact native jobs and targets");
        DomainException.Require(Jobs.Distinct().Count() == Jobs.Count, "duplicate ordered work job");
    }

    public bool SameRequest(OrderedWork other) => Pawn == other.Pawn && Sequence == other.Sequence
        && Purpose == other.Purpose && Jobs.SequenceEqual(other.Jobs);
}

public enum OrderedWorkStatus { Queued, Running, Succeeded, Failed, Gone, Cancelled }

/// <summary>Exact native identity and observed outcome. Gone never implies success.</summary>
public sealed record OrderedWorkJob(int JobId, OrderedWorkStatus Status);

public sealed record OrderedWorkObservation(OrderedWork Request, EquatableArray<OrderedWorkJob> Jobs);

public enum OrderedWorkOperation { Install, Read, Cancel, Refuse }

public static class OrderedWorkProtocol
{
    public static OrderedWorkOperation Operation(OrderedWork request, OrderedWorkObservation? latest)
    {
        request.Validate();
        if (latest is null) return request.Sequence == 1 && !request.Cancel ? OrderedWorkOperation.Install : OrderedWorkOperation.Refuse;
        if (request.Sequence == latest.Request.Sequence)
            return !request.SameRequest(latest.Request) ? OrderedWorkOperation.Refuse
                : request.Cancel ? OrderedWorkOperation.Cancel : OrderedWorkOperation.Read;
        return !request.Cancel && latest.Request.Sequence < ulong.MaxValue && request.Sequence == latest.Request.Sequence + 1
            && latest.Jobs.All(j => Terminal(j.Status)) ? OrderedWorkOperation.Install : OrderedWorkOperation.Refuse;
    }

    public static bool Terminal(OrderedWorkStatus status) => status is OrderedWorkStatus.Succeeded
        or OrderedWorkStatus.Failed or OrderedWorkStatus.Gone or OrderedWorkStatus.Cancelled;

    public static OrderedWorkObservation Observe(OrderedWorkObservation latest, int? current, IReadOnlyCollection<int> queued) => latest with
    {
        Jobs = EquatableArray.From(latest.Jobs.Select(j => Terminal(j.Status) ? j : j with
        {
            Status = current == j.JobId ? OrderedWorkStatus.Running
                : queued.Contains(j.JobId) ? OrderedWorkStatus.Queued : OrderedWorkStatus.Gone,
        })),
    };

    public static OrderedWorkObservation Ended(OrderedWorkObservation latest, int job, bool succeeded) => latest with
    {
        Jobs = EquatableArray.From(latest.Jobs.Select(j => j.JobId == job && !Terminal(j.Status)
            ? j with { Status = succeeded ? OrderedWorkStatus.Succeeded : OrderedWorkStatus.Failed } : j)),
    };
}

public sealed partial record DomainAction
{
    public static DomainAction NewOrderedWorkAction(ActionId id, OrderedWork value)
    {
        value.Validate();
        return Create(id, ActionKind.OrderedWork, value);
    }
    public OrderedWork? OrderedWork => PayloadOf<OrderedWork>(ActionKind.OrderedWork);
}
