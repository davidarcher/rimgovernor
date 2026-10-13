using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// campaign/foothold: the eight tribal colonists, played through the player control path for three game days with no
/// harness hand after setup, must end sheltered (indoor sleeping capacity for every colonist), fed (no malnutrition
/// past 0.3, a known food runway) and alive, with a goal progress record advanced on native evidence and none stalled
/// past its review deadline (Go <c>campaign/foothold.go</c>).
/// </summary>
internal static class CampaignFootholdCase
{
    internal static AcceptanceCase Case()
    {
        var window = CampaignRun.FootholdWindow();
        return CampaignRun.CampaignCase(
            "campaign/foothold",
            "Unassisted campaign: shelter and food maintained over three game days on the player control path.",
            "three game days at Ultrafast on the player path with every family is 20-25 minutes of play plus the reload",
            TimeSpan.FromMinutes(45),
            async (s, cancellationToken) =>
            {
                var c = CampaignRun.Create(s);
                var initial = await CampaignRun.InitialColonistsAsync(s.Harness, c.Report, cancellationToken).ConfigureAwait(false);
                c.SetupDone();
                var p = await c.PlayAsync(
                    "foothold",
                    new WatchConfig
                    {
                        Watch = TimeSpan.FromMinutes(35),
                        Window = window,
                        PollTicks = 5000,
                        Concern = PolicyFunctions.EnsureFoodSupply.Value,
                        Extra = CampaignRun.CampaignConcerns,
                        FailFast = new FailFast { Disabled = true },
                    },
                    cancellationToken).ConfigureAwait(false);
                c.Milestone("foothold_window_end", p.LastTick());
                var failures = new List<Exception?>
                {
                    p.AssertPlaying(window),
                    p.AssertProgress(),
                    await SustainedFoodCase.CaptureAsync(() => c.AuditColonyAsync(s.Harness, "end_state", initial, cancellationToken)).ConfigureAwait(false),
                    c.AssertUnassisted(),
                };
                if (SustainedFoodCase.Join(failures) is { } error)
                {
                    throw error;
                }
            });
    }
}
