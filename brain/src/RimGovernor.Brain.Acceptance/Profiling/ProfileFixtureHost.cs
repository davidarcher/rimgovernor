using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// The fixture machinery profile-capture borrows (Go <c>executeFixture</c>, <c>printFixture</c> and <c>preflight</c> of
/// <c>cmd/acceptance</c>): the doctor/heal pass over a case whose start is the fixture op, then the same save-load and op
/// call <c>acceptance fixture</c> makes, on the root's kept game.
/// </summary>
public static class ProfileFixtureHost
{
    /// <summary>The doctor/heal pass over a case whose start is the fixture op (Go <c>preflight</c> with one <c>cases.Fixture{Op}</c> case); false means the preflight refused and has printed why to <paramref name="info"/>.</summary>
    public static async Task<bool> PreflightAsync(ProfileFixtureCall request, TextWriter info, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(info);
        var options = new RunOptions
        {
            Root = request.Root,
            Output = request.Output,
            GameId = request.GameId,
            Headless = request.Headless,
            NoHeal = request.NoHeal,
        };
        var result = await PreflightPass.RunAsync([], [request.Op], options, info, cancellationToken).ConfigureAwait(false);
        return result.Ok;
    }

    /// <summary>Loads the save, calls the op and fills <paramref name="result"/>. A failure to run the call at all throws <see cref="AcceptanceException"/>; an op that ran and failed returns with Success false.</summary>
    public static async Task ExecuteAsync(ProfileFixtureCall request, ProfileFixtureResult result, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);
        var options = new FixtureOptions
        {
            Op = request.Op,
            Args = request.Args,
            Save = request.Save,
            Root = request.Root,
            Output = request.Output,
            GameId = request.GameId,
            Headless = request.Headless,
            Timeout = request.Timeout,
            IgnoreMods = request.IgnoreMods,
        };
        var run = new FixtureRun(request.Op, request.Args, result.Output);
        try
        {
            await FixtureCommand.ExecuteAsync(options, run, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            result.Success = run.Success == true;
            result.Response = run.Response;
        }
    }

    /// <summary>Prints a failed call's result (Go <c>printFixture</c>).</summary>
    public static Task PrintFailureAsync(TextWriter writer, ProfileFixtureResult result, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(result);
        var run = new FixtureRun(result.Op, result.Args, result.Output)
        {
            Success = result.Success,
            Response = result.Response,
            Error = result.Error.Length > 0 ? result.Error : null,
        };
        return FixtureCommand.PrintAsync(writer, false, run);
    }
}
