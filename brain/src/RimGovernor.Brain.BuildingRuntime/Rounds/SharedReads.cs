using System.Runtime.ExceptionServices;
namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// Reads a review starts together so their native hops share frames (#3566): each read's failure is captured where it
/// happens and thrown where the sequential code read the reply, so the first failure in the old order still names the
/// abstain and no started read faults unobserved.
/// </summary>
internal static class SharedReads
{
    /// <summary>The read's reply, or the exception it threw (a cancellation propagates).</summary>
    internal static async Task<(T Value, Exception? Error)> Attempt<T>(Task<T> read)
    {
        try
        {
            return (await read.ConfigureAwait(false), null);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return (default!, e);
        }
    }

    /// <summary>The attempted read's reply; its captured exception is thrown again, with its stack, where the reply is read.</summary>
    internal static T Reply<T>((T Value, Exception? Error) attempt)
    {
        if (attempt.Error is { } error)
        {
            ExceptionDispatchInfo.Capture(error).Throw();
        }
        return attempt.Value;
    }
}
