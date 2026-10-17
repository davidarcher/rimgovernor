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
        var row = new JsonObject { ["op"] = request.Op, ["args"] = request.Args.DeepClone(), ["output"] = result.Output };
        try
        {
            await FixtureCommand.ExecuteAsync(options, row, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            result.Success = row["success"].AsBool() == true;
            result.Response = row["response"] as JsonObject;
        }
    }

    /// <inheritdoc />
    public Task PrintFailureAsync(TextWriter writer, ProfileFixtureResult result, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(result);
        var row = new JsonObject
        {
            ["op"] = result.Op,
            ["output"] = result.Output,
            ["success"] = result.Success,
            ["response"] = result.Response?.DeepClone(),
        };
        if (result.Error.Length > 0)
        {
            row["error"] = result.Error;
        }

        return FixtureCommand.PrintAsync(writer, false, row);
    }
}
