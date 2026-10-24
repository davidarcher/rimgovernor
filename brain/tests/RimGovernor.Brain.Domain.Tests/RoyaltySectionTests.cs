using RimGovernor.Contracts.Wire;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using C = RimGovernor.Contracts.Wire.Common;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The Royalty colony section and pawn block are total (#3773): the section is a named state (<see cref="RoyaltySection.Active"/>
/// or <see cref="RoyaltySection.Inactive"/> without Royalty), every member is <c>required</c>, a title, a throne owner and a
/// psylink are relations (null for none), and both round-trip through <c>WireJsonContext</c> exactly.
/// </summary>
public sealed class RoyaltySectionTests
{
    /// <summary>The section of a game without Royalty: the section every test without royalty carries.</summary>
    internal static readonly RoyaltySection NoRoyalty = new RoyaltySection.Inactive();

    [Fact]
    public void ColonySectionJsonRoundTripIsExact()
    {
        RoyaltySection section = new RoyaltySection.Active(new RoyaltyColonyFacts
        {
            Neuroformers = [new NeuroformerStock { DefName = "PsychicAmplifier", Held = 1, Craftable = false }],
            Ceremonies = [new BestowingCeremony { Quest = "Quest_7", Pawn = new PawnId("Thing_Human3"), Title = null, Accepted = true, BestowerWaiting = true, Started = false }],
            Thrones =
            [
                new RoyalThrone { Thing = new C.Ref { Id = "Thing_Throne4" }, Owner = new C.Ref { Id = "Thing_Human3" } },
                new RoyalThrone { Thing = new C.Ref { Id = "Thing_Throne5" }, Owner = null },
            ],
        });

        var json = WireJson.Serialize(section);

        Assert.Equal(
            """{"type":"active","facts":{"neuroformers":[{"defName":"PsychicAmplifier","held":1,"craftable":false}],"ceremonies":[{"quest":"Quest_7","pawn":"Thing_Human3","title":null,"accepted":true,"bestowerWaiting":true,"started":false}],"thrones":[{"thing":{"id":"Thing_Throne4"},"owner":{"id":"Thing_Human3"}},{"thing":{"id":"Thing_Throne5"},"owner":null}]}}""",
            json);
        Assert.Equal(section, WireJson.Deserialize<RoyaltySection>(json));
        Assert.Equal("""{"type":"inactive"}""", WireJson.Serialize(NoRoyalty));
        Assert.Equal(NoRoyalty, WireJson.Deserialize<RoyaltySection>("""{"type":"inactive"}"""));
    }

    [Fact]
    public void PawnBlockJsonRoundTripIsExact()
    {
        var royalty = new PawnRoyalty
        {
            Holdings =
            [
                new PawnRoyalHolding
                {
                    FactionDef = "Empire",
                    Title = "Knight",
                    Favor = 4,
                    PermitPoints = 1,
                    Permits = ["CallLaborers"],
                    PermitCooldowns = [new PermitCooldown { Permit = "CallLaborers", CooldownRemainingTicks = 0 }],
                },
            ],
            Psycasts = [new PawnPsycast { DefName = "Stun", CooldownRemainingTicks = 120 }],
            Psylink = new PawnPsylink { Psyfocus = 0.5, Entropy = 10, EntropyMax = 30 },
        };

        var json = WireJson.Serialize(royalty);

        Assert.Equal(
            """{"holdings":[{"factionDef":"Empire","title":"Knight","favor":4,"permitPoints":1,"permits":["CallLaborers"],"permitCooldowns":[{"permit":"CallLaborers","cooldownRemainingTicks":0}]}],"psycasts":[{"defName":"Stun","cooldownRemainingTicks":120}],"psylink":{"psyfocus":0.5,"entropy":10,"entropyMax":30}}""",
            json);
        Assert.Equal(royalty, WireJson.Deserialize<PawnRoyalty>(json));
    }
}
