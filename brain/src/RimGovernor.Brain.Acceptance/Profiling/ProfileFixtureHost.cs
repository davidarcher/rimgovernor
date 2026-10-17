using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// The fixture machinery profile-capture borrows (Go <c>executeFixture</c>, <c>printFixture</c> and <c>preflight</c> of
/// <c>cmd/acceptance</c>): the doctor/heal pass over a case whose start is the fixture op, then the same save-load and op
/// call <c>acceptance fixture</c> makes, on the root's kept game.
/// </summary>
public sealed class ProfileFixtureHost : IProfileFixtureHost
{
    /// <summary>The shared instance <see cref="AcceptanceCommand"/> hands profile-capture.</summary>
    public static ProfileFixtureHost Instance { get; } = new();

    /// <inheritdoc />
    public async Task<bool> PreflightAsync(ProfileFixtureCall request, TextWriter info, CancellationToken cancellationToken)
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

    /// <inheritdoc />
    public async Task ExecuteAsync(ProfileFixtureCall request, ProfileFixtureResult result, CancellationToken cancellationToken)
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

    /// <inheritdoc />
    public Task PrintFailureAsync(TextWriter writer, ProfileFixtureResult result, CancellationToken cancellationToken)
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
