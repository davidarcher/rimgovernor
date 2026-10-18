using RimGovernor.Brain.PlayerApi;

namespace RimGovernor.Brain.Host;

/// <summary>
/// A new colony started from a theme (#3511) adopts that theme into its goals. The request's theme is remembered by
/// request id, and when a poll first reads the colony completed, <paramref name="adopt"/> seeds the new world's goals
/// (after its rebuild from the save, so the save's empty goals blob cannot overwrite them) and the governor state is
/// flushed into the game, so the next save carries the theme. A failed or superseded request forgets its theme. Every
/// other call passes straight through.
/// </summary>
/// <remarks>
/// The adoption rides the completed poll, which the launcher and any player client make before using the colony; a
/// colony nobody polls to completion starts without the theme's goals.
/// </remarks>
internal sealed class ThemedLifecycle(ILifecycleWriter inner, Func<PbCommon.ObservationContext, string, CancellationToken, Task> adopt) : ILifecycleWriter
{
    private readonly object _mu = new();
    private readonly Dictionary<string, string> _themes = new(StringComparer.Ordinal);

    public async Task<PbLifecycle.NewColonyReply> NewColonyAsync(PbLifecycle.NewColonyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var reply = await inner.NewColonyAsync(request, cancellationToken).ConfigureAwait(false);
        if (request.Spec?.Theme is { Length: > 0 } theme && request.RequestId is { Length: > 0 } id)
        {
            lock (_mu)
            {
                _themes[id] = theme;
            }
        }
        return await SettleAsync(request.RequestId ?? "", reply, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PbLifecycle.NewColonyReply> ReadNewColonyAsync(string requestId, CancellationToken cancellationToken)
    {
        var reply = await inner.ReadNewColonyAsync(requestId, cancellationToken).ConfigureAwait(false);
        return await SettleAsync(requestId, reply, cancellationToken).ConfigureAwait(false);
    }

    private async Task<PbLifecycle.NewColonyReply> SettleAsync(string requestId, PbLifecycle.NewColonyReply reply, CancellationToken cancellationToken)
    {
        if (reply.Outcome?.Pending is not null)
        {
            return reply;
        }
        // Removed before adopting, so a concurrent poll of the same completion adopts nothing twice.
        string? theme;
        lock (_mu)
        {
            _ = _themes.Remove(requestId, out theme);
        }
        if (theme is not null && reply.Outcome?.Completed is { Context: { } context })
        {
            await adopt(context, theme, cancellationToken).ConfigureAwait(false);
        }
        return reply;
    }

    public Task<PbLifecycle.SaveReply> SaveAsync(PbLifecycle.SaveRequest request, CancellationToken cancellationToken) => inner.SaveAsync(request, cancellationToken);

    public Task<PbLifecycle.SaveReply> ReadSaveAsync(string requestId, CancellationToken cancellationToken) => inner.ReadSaveAsync(requestId, cancellationToken);

    public Task<PbLifecycle.LoadReply> LoadAsync(PbLifecycle.LoadRequest request, CancellationToken cancellationToken) => inner.LoadAsync(request, cancellationToken);

    public Task<PbLifecycle.LoadReply> ReadLoadAsync(string requestId, CancellationToken cancellationToken) => inner.ReadLoadAsync(requestId, cancellationToken);

    public Task<PbLifecycle.MenuReply> MenuAsync(PbLifecycle.MenuRequest request, CancellationToken cancellationToken) => inner.MenuAsync(request, cancellationToken);

    public Task<PbLifecycle.MenuReply> ReadMenuAsync(string requestId, CancellationToken cancellationToken) => inner.ReadMenuAsync(requestId, cancellationToken);
}
