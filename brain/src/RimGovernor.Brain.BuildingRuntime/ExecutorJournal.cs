namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// The executor's <see cref="IJournal"/> over the session's store. Go's <c>store.Store</c> satisfied the executor's
/// Journal interface directly; the Executor project cannot be a dependency of Store, so the session binds them here
/// by delegation. Every method is the store's own.
/// </summary>
internal sealed class ExecutorJournal(StoreJournal store) : IJournal
{
    public Task<PlanState> LoadPlanAsync(PlanId plan, CancellationToken ct) => store.LoadPlanAsync(plan, ct);

    public Task<Progress> PrepareAsync(PlanId plan, ActionId action, GenerationSnapshot snapshot, Tick tick, CancellationToken ct) =>
        store.PrepareAsync(plan, action, snapshot, tick, ct);

    public Task<Progress> DispatchAsync(PlanId plan, ActionId action, GenerationSnapshot snapshot, Tick tick, CancellationToken ct) =>
        store.DispatchAsync(plan, action, snapshot, tick, ct);

    public Task<Progress> RecordReceiptAsync(PlanId plan, ActionId action, AttemptId attempt, Domain.Receipt receipt, CancellationToken ct) =>
        store.RecordReceiptAsync(plan, action, attempt, receipt, ct);

    public Task<Progress> RecordZoneReceiptAsync(PlanId plan, ActionId action, AttemptId attempt, string zone, CancellationToken ct) =>
        store.RecordZoneReceiptAsync(plan, action, attempt, zone, ct);

    public Task<Progress> RecordStockpileReceiptAsync(PlanId plan, ActionId action, AttemptId attempt, IReadOnlyList<CreatedZone> zones, CancellationToken ct) =>
        store.RecordStockpileReceiptAsync(plan, action, attempt, zones, ct);

    public Task<Progress> RecordBillReceiptAsync(PlanId plan, ActionId action, AttemptId attempt, string bill, CancellationToken ct) =>
        store.RecordBillReceiptAsync(plan, action, attempt, bill, ct);

    public Task<Progress> ObserveAsync(PlanId plan, Domain.Observation observation, GenerationSnapshot current, CancellationToken ct) =>
        store.ObserveAsync(plan, observation, current, ct);

    public Task<Progress> CancelAsync(PlanId plan, ActionId action, CancellationToken ct) => store.CancelAsync(plan, action, ct);

    public Task<Progress> HoldAsync(PlanId plan, ActionId action, IReadOnlyList<HeldReason> reasons, Tick tick, CancellationToken ct) =>
        store.HoldAsync(plan, action, reasons, tick, ct);

    public Task<IReadOnlyList<BatchResult>> PrepareBatchAsync(IReadOnlyList<BatchAttempt> attempts, CancellationToken ct) =>
        store.PrepareBatchAsync(attempts, ct);

    public Task<IReadOnlyList<BatchResult>> DispatchBatchAsync(IReadOnlyList<BatchAttempt> attempts, CancellationToken ct) =>
        store.DispatchBatchAsync(attempts, ct);

    public Task<IReadOnlyList<BatchResult>> RecordReceiptsAsync(IReadOnlyList<BatchReceipt> receipts, CancellationToken ct) =>
        store.RecordReceiptsAsync(receipts, ct);
}
