using RimGovernor.Tools.Workflow.Commands;
using static RimGovernor.Tools.Workflow.Tests.Sandbox;

namespace RimGovernor.Tools.Workflow.Tests;

public sealed class LandIssueTests
{
    [Theory]
    [InlineData("Add staging", 0, false, "comment", "3704")]
    [InlineData("Add staging (part of #3704)", 0, false, "comment", "3704")]
    [InlineData("Finish staging\n\nFixes #3704", 0, false, "close", "3704")]
    [InlineData("Finish staging\n\nCloses #3704", 0, false, "close", "3704")]
    [InlineData("Add staging", 12, false, "close", "12")]
    [InlineData("Finish staging\n\nFixes #3704", 12, true, "", "")]
    public async Task UpdatesIssueOnlyAfterLandingAccordingToExplicitIntent(
        string message, int issue, bool noClose, string verb, string number)
    {
        using var s = new Sandbox();
        Git(s.Worktree, "branch", "-m", "codex/issue-3704-staging");
        Write(s.Worktree, "a.txt", "a\n");
        Commit(s.Worktree, message);
        var commands = new List<string[]>();

        await s.LandAsync(s.Options() with
        {
            Issue = issue,
            NoClose = noClose,
            IssueCommand = args =>
            {
                Assert.Equal(s.OriginMain(), Git(s.Worktree, "rev-parse", "HEAD"));
                commands.Add(args);
                return Task.CompletedTask;
            },
        });

        if (noClose)
        {
            Assert.Empty(commands);
            return;
        }
        var command = Assert.Single(commands);
        var landed = Git(s.Origin, "rev-parse", "--short", "main");
        Assert.Equal(new[] { "issue", verb, number, verb == "close" ? "--comment" : "--body", $"Landed on main as {landed}." }, command);
    }

    [Fact]
    public async Task UsesFinalOverrideMessageAndCommentsOnPartialBranchIssue()
    {
        using var s = new Sandbox();
        Git(s.Worktree, "branch", "-m", "codex/issue-3704-staging");
        Write(s.Worktree, "a.txt", "a\n");
        Commit(s.Worktree, "Finish staging\n\nFixes #3704");
        var commands = new List<string[]>();

        await s.LandAsync(s.Options() with
        {
            Message = "Part of #3704; resolves #3716. Fixes #3716.",
            IssueCommand = args =>
            {
                commands.Add(args);
                return Task.CompletedTask;
            },
        });

        Assert.Collection(commands,
            command => { Assert.Equal("comment", command[1]); Assert.Equal("3704", command[2]); },
            command => { Assert.Equal("close", command[1]); Assert.Equal("3716", command[2]); });
    }
}
