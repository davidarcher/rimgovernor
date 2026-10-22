using System.Threading;
using System.Threading.Tasks;

namespace HomeBridge.BridgeTools;

internal sealed partial class NativeOpTable
{
    // Both typed native reads and registry fixtures are harness activity. The
    // autonomous brain and health endpoint bypass this boundary deliberately:
    // neither proves that an external harness still owns this warm game.
    public async Task<string> InvokeAsync(string op, string body, CancellationToken cancellationToken)
    {
        IdleExit.Begin();
        try
        {
            return await (Ops.TryGetValue(op, out var run)
                ? run(body, cancellationToken)
                : InvokeRegisteredAsync(op, body, cancellationToken)).ConfigureAwait(false);
        }
        finally { IdleExit.End(); }
    }
}
