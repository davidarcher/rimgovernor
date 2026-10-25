using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>The named states that replaced the unread options on an action's progress, payloads and encounter evidence (#3960).</summary>
public sealed class ActionStatesTests
{
    [Fact]
    public void AnAttemptWithoutAReceiptOrEffectNamesBothStates()
    {
        var view = new ProgressView();

        Assert.IsType<ReceiptState.NoReceipt>(view.Receipt);
        Assert.IsType<EffectState.NotObserved>(view.Effect);
        Assert.False(view.Received(Receipt.Accepted));
        Assert.False(view.Observes(Effect.Completed));
    }

    [Fact]
    public void ARecordedReceiptAndEffectAreReadByName()
    {
        var view = new ProgressView
        {
            Receipt = new ReceiptState.Received(Receipt.Unconfirmed),
            Effect = new EffectState.Observed(Effect.Inconclusive),
        };

        Assert.True(view.Received(Receipt.Unconfirmed));
        Assert.False(view.Received(Receipt.Accepted));
        Assert.True(view.Observes(Effect.Inconclusive));
        Assert.False(view.Observes(Effect.Absent));
    }

    [Fact]
    public void ARefusedReceiptWithoutAReasonIsAnIntentRefusal()
    {
        var view = new ProgressView
        {
            Stage = Stage.Unsuccessful,
            Receipt = new ReceiptState.Received(Receipt.Refused),
        };

        Assert.Equal(UnsuccessfulReason.IntentRefused, view.UnsuccessfulCause());
    }

    [Fact]
    public void AStockpileZoneKeepsItsGroundAsDraggedOrListed()
    {
        var filter = StockpileFilter.GeneralFilter();
        var dragged = ZoneCreate.NewFilteredStockpileZone(
            filter,
            StockpilePriority.Normal,
            new GroundRect(new Cell(1, 1), 3, 2)
        );
        var listed = ZoneCreate.NewFilteredStockpileCellsZone(
            filter,
            StockpilePriority.Normal,
            [new Cell(1, 1), new Cell(2, 1)]
        );

        Assert.IsType<StockpileGround.Dragged>(dragged.Stockpile!.Ground);
        Assert.IsType<StockpileGround.Listed>(listed.Stockpile!.Ground);
        Assert.Equal(filter, dragged.Stockpile.Filter);
        Assert.Null(ZoneCreate.NewFishingZone([new Cell(1, 1)]).Stockpile);
    }

    [Fact]
    public void AnOwnedPawnSettingIsOneArmPerKind()
    {
        var pawn = new PawnId("pawn-1");

        var carry = Assert.IsType<PawnSettings.MedicineCarrySetting>(
            PawnSettings.NewMedicineCarrySetting(pawn, 2)
        );
        var hostility = Assert.IsType<PawnSettings.HostilitySetting>(
            PawnSettings.NewHostilitySetting(pawn, HostilityResponse.Flee)
        );
        var selfTend = Assert.IsType<PawnSettings.SelfTendSetting>(
            PawnSettings.NewSelfTendSetting(pawn, true)
        );

        Assert.Equal(2, carry.Count);
        Assert.Equal(SettingKind.MedicineCarry, carry.Kind);
        Assert.Equal(HostilityResponse.Flee, hostility.Mode);
        Assert.Equal(SettingKind.Hostility, hostility.Kind);
        Assert.True(selfTend.On);
        Assert.Equal(pawn, selfTend.Pawn);
    }

    [Fact]
    public void AnEncounterActorThatNeverActedCarriesNoTick()
    {
        var actor = new EncounterActorOutcome(
            "a",
            false,
            false,
            1,
            0,
            false,
            [],
            [],
            [],
            null,
            new EncounterTick(0),
            null,
            null,
            null
        );

        Assert.Equal(0, actor.FirstAttackTick!.Tick);
        Assert.Null(actor.LastShotTick);
        var prediction = new EncounterPrediction
        {
            Status = EncounterStatus.Evidence,
            RequestFingerprint = "f",
            Reason = "r",
            ColdStartMilliseconds = new MeasuredMilliseconds(1.5),
        };
        Assert.Equal(1.5, prediction.ColdStartMilliseconds!.Value);
        Assert.Null(prediction.RequestMilliseconds);
    }
}
