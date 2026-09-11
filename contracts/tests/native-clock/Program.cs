using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Google.Protobuf;
using HomeBridge.BridgeTools;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Authority = RimGovernor.Protocol.Authority;
using Clock = RimGovernor.Protocol.Clock;

internal static class Program
{
    private static int checks, nextAttempt;
    private static NativeControlAuthority authority;
    private static NativeControlSnapshot lease;
    private static readonly NativeClockTools Tools = new();
    private static Common.Identity Identity => new() { ColonyId = Current.Game.Identity.ColonyId, LoadToken = Current.Game.Identity.LoadToken, MapId = Find.CurrentMap.uniqueID };
    private static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
    private sealed class Context : RimBridgeServer.Sdk.IRimBridgeContext, RimBridgeServer.Sdk.IMainThread
    {
        public Dictionary<string, object> Arguments { get; set; } = new();
        public RimBridgeServer.Sdk.IMainThread MainThread => this;
        public int Invocations;
        public Task<T> InvokeAsync<T>(Func<T> apply, CancellationToken token) { token.ThrowIfCancellationRequested(); Invocations++; return Task.FromResult(apply()); }
    }
    private static T Call<T>(IMessage request, Func<Context, string, Task<object>> call, MessageParser<T> parser) where T : IMessage<T>
    {
        var ctx = new Context(); var text = JsonFormatter.Default.Format(request); ctx.Arguments["request"] = text;
        var reply = parser.ParseJson((string)((Dictionary<string, object>)call(ctx, text).GetAwaiter().GetResult())["payload"]);
        Check(ctx.Invocations == 1, "typed request dispatch count"); Check(reply.Equals(parser.ParseFrom(reply.ToByteArray())), "official binary echo");
        return reply;
    }
    private static Clock.ControlReply Start(Clock.StartRequest request) => Call(request, (ctx, json) => Tools.Start(ctx, default, json), Clock.ControlReply.Parser);
    private static Clock.ControlReply Renew(Clock.RenewRequest request) => Call(request, (ctx, json) => Tools.Renew(ctx, default, json), Clock.ControlReply.Parser);
    private static Clock.ControlReply Speed(Clock.SpeedRequest request) => Call(request, (ctx, json) => Tools.ChangeSpeed(ctx, default, json), Clock.ControlReply.Parser);
    private static Clock.StatusReply Status() => Call(new Clock.StatusRequest { Identity = Identity }, (ctx, json) => Tools.ReadStatus(ctx, default, json), Clock.StatusReply.Parser);
    private static Clock.StatusReply Pause(Clock.OwnedRequest request) => Call(request, (ctx, json) => Tools.Pause(ctx, default, json), Clock.StatusReply.Parser);
    private static Clock.EventsReply Events(long after = 0, uint limit = 128) => Call(new Clock.EventsRequest { Identity = Identity, AfterCursor = after, Limit = limit }, (ctx, json) => Tools.ReadEvents(ctx, default, json), Clock.EventsReply.Parser);
    private static Authority.WritePrecondition Pre() => new() { Identity = Identity, ExpectedGeneration = lease.Generation, LeaseId = lease.Lease.LeaseId,
        Attempt = new Common.AttemptKey { ControllerSessionId = "controller", ActionId = "clock/" + ++nextAttempt, AttemptId = 1 } };
    private static Clock.StartRequest Request() => new() { Authority = Pre(), Speed = Clock.Speed.Normal, LeaseMs = 1000, MaxTicks = 10,
        Policy = new Clock.WatchPolicy { Mode = Clock.WatchMode.Colony, HealthDropFraction = .1f, MinHealthFraction = .5f, HostileWithin = 30, InjuryStopCooldownMs = 0 } };
    private static void Reset()
    {
        Current.Game = new Game(); Find.CurrentMap = new Map(); Find.TickManager = new TickManager();
        GenFilePaths.SaveDataFolderPath = Path.Combine(Environment.CurrentDirectory, ".rimgovernor", "clock-tests-" + Guid.NewGuid().ToString("N"));
        Supervisor.FixtureReset(); HarmonyLib.Harmony.Healthy = true;
        authority = NativeControlAuthority.ForGame(Current.Game); authority.SetHookHealth(true);
        lease = authority.Acquire(authority.Status().Generation, "controller", 42, 30000).Snapshot;
        Check(lease.Active, "authority setup");
    }
    private static void Boundaries()
    {
        Reset(); var ctx = new Context();
        var result = (Dictionary<string, object>)Tools.ReadStatus(ctx, default).GetAwaiter().GetResult();
        Check(Clock.StatusReply.Parser.ParseJson((string)result["payload"]).Failure.Code == Common.FailureCode.InvalidRequest && ctx.Invocations == 0, "missing request dispatched");
        foreach (var json in new[] { "null", "{", "{\"unexpected\":1}" })
        { ctx.Arguments["request"] = json; result = (Dictionary<string, object>)Tools.Start(ctx, default, json).GetAwaiter().GetResult(); Check(Clock.ControlReply.Parser.ParseJson((string)result["payload"]).Failure != null && ctx.Invocations == 0, "malformed Start dispatched"); }
        Check(Status().Status.NeverStarted != null && Status().Status.ActualPaused, "actual never-started clock");
        Check(Status().Status.NativeTickBoundary, "clock readiness requires an owned start");
        Check(!Directory.Exists(GenFilePaths.SaveDataFolderPath), "status initialized journal");
        var initial = Events();
        Check(initial.Page != null && initial.Page.NewestCursor == 0 && initial.Page.Events.Count == 0, "initial event read requires clock start");
        Check(Status().Status.NeverStarted != null && Find.TickManager.Paused && authority.Status().Generation == lease.Generation, "initial event read changed clock or authority");
        foreach (var pair in new[] { (-1L, 1u), (0L, 0u), (0L, 129u), (long.MaxValue, 128u) }) Check(Events(pair.Item1, pair.Item2).Failure != null, "invalid/unavailable cursor admitted");
        foreach (Action<Clock.StartRequest> mutation in new Action<Clock.StartRequest>[] { r => r.Speed = Clock.Speed.Unspecified, r => r.LeaseMs = 999,
            r => r.LeaseMs = 30001, r => r.MaxTicks = 0, r => r.Policy = null, r => r.Policy.HealthDropFraction = float.NaN,
            r => r.Policy.ClearHostileWithin(), r => r.Policy.Mode = (Clock.WatchMode)99, r => r.Policy.InjuryStopCooldownMs = 1800001 })
        { var r = Request(); mutation(r); Check(Start(r).Failure != null && !Supervisor.IsActiveForFixture(), "invalid Start affected clock"); }
    }
    private static void OwnedLifecycle()
    {
        Reset(); var request = Request(); var reply = Start(request); var epoch = reply.Receipt.Applied.Status.Running.Epoch;
        Check(epoch.Origin.Identity.Equals(Identity) && epoch.Origin.NativeGeneration == lease.Generation && epoch.Owner.ControllerSessionId == "controller" && epoch.StartTick == 0 && epoch.TickDeadline == 10, "owned origin not captured");
        Check(!Status().Status.ActualPaused && Status().Status.ObservedSpeed == Clock.ObservedSpeed.Normal, "actual running facts");
        var owned = new Clock.OwnedRequest { Identity = Identity, Owner = epoch.Owner.Clone() };
        var ledger = NativeOperationState.ForAdmission(Identity).Ledger;
        Check(ledger.Count == 1 && Start(request).Equals(reply) && ledger.Count == 1, "exact start replay dispatched");
        var altered = request.Clone(); altered.MaxTicks++;
        Check(Start(altered).Failure.Code == Common.FailureCode.AttemptConflict, "altered attempt reused");
        var renew = new Clock.RenewRequest { Epoch = owned.Clone(), Authority = Pre(), LeaseMs = 30000 };
        var renewed = Renew(renew).Receipt.Applied.Status.Running.Epoch;
        Check(renewed.TickDeadline == epoch.TickDeadline && renewed.Owner.Equals(epoch.Owner) && renewed.LeaseRemainingMs > 29000, "renew budget/owner/deadline");
        Supervisor.WallTime += 1000000000; Supervisor.OnUpdate();
        Check(Status().Status.Running != null, "UTC jump expired monotonic lease");
        var speed = new Clock.SpeedRequest { Epoch = owned.Clone(), Authority = Pre(), Speed = Clock.Speed.Fast };
        Check(Speed(speed).Receipt.Applied.Status.Running.Epoch.RequestedSpeed == Clock.Speed.Fast && Status().Status.Running.Epoch.TickDeadline == 10, "speed extended budget");
        var first = Events(0, 1).Page; var second = Events(first.NextCursor, 128).Page;
        Check(first.Events.Count == 1 && first.Events[0].Started != null && first.NextCursor == 1 && second.Events.Count == 1 && second.Events[0].SpeedChanged != null && !first.Gap, "event paging/cursors");
        authority.Revoke(authority.Status().Generation, NativeControlRevocationReason.Manual);
        Check(Start(request).Equals(reply), "replay after revocation changed receipt");
        var paused = Pause(owned).Status;
        Check(paused.Stopped != null && paused.Stopped.Reason == Clock.StopReason.RequestedPause && paused.Stopped.PauseVerified && paused.ActualPaused, "owned cleanup after revoke");
        Check(Pause(owned).Status.Stopped.Epoch.Owner.Equals(epoch.Owner), "exact pause not idempotent");
        Check(Renew(new Clock.RenewRequest { Epoch = owned, Authority = Pre(), LeaseMs = 1000 }).Failure != null, "renew restarted stopped clock");
        var lookup = Call(new Clock.AttemptRequest { Identity = Identity, Attempt = request.Authority.Attempt }, (ctx, json) => Tools.ReadAttempt(ctx, default, json), Clock.AttemptReply.Parser);
        Check(lookup.Receipt.Equals(reply.Receipt), "clock lookup lost original receipt");
    }
    private static void StopsAndContext()
    {
        Reset(); Supervisor.InitialStop = "hostile"; // Missing pawn evidence remains explicit unavailable.
        var immediate = Start(Request()); Check(immediate.Receipt.Applied.Status.Stopped?.Reason == Clock.StopReason.Hostile, "initial safety stop not owned");
        Reset(); var request = Request(); var epoch = Start(request).Receipt.Applied.Status.Running.Epoch;
        var owned = new Clock.OwnedRequest { Identity = Identity, Owner = epoch.Owner.Clone() };
        Supervisor.FixtureExpire(); Supervisor.OnUpdate();
        Check(Status().Status.Stopped.Reason == Clock.StopReason.LeaseExpired && Status().Status.ActualPaused, "expired owned lease not stopped");
        Check(Renew(new Clock.RenewRequest { Epoch = owned, Authority = Pre(), LeaseMs = 1000 }).Failure != null, "expired renew reacquired");
        Reset(); epoch = Start(Request()).Receipt.Applied.Status.Running.Epoch; owned = new() { Identity = Identity, Owner = epoch.Owner.Clone() };
        Supervisor.RefusePause = true;
        Check(Pause(owned).Status.Stopping != null && !Status().Status.ActualPaused, "failed pause disarmed");
        Supervisor.RefusePause = false; Supervisor.OnUpdate(); Check(Status().Status.Stopped != null && Status().Status.ActualPaused, "pending pause not retried");
        Reset(); Start(Request()); Find.TickManager.TicksGame = 4; Supervisor.OnUpdate();
        var original = Identity; Find.CurrentMap = new Map { uniqueID = 99 }; Find.TickManager.TicksGame = 100; Supervisor.OnUpdate();
        var page = Events().Page; var stopped = page.Events.Last();
        Check(page.Context.Identity.MapId == 99 && stopped.Context.Identity.Equals(original) && stopped.Context.Tick == 4, "old event borrowed current map/tick");
        Check(!Find.TickManager.Paused, "old epoch paused replacement map");
        var repeated = Events().Page; Check(repeated.Events.Last().Equals(stopped), "event context changed on read");
        Supervisor.FixtureLegacyEvent(); Check(Events(stopped.Cursor).Failure.Code == Common.FailureCode.Unavailable, "legacy row fabricated typed context");
        Check(Events(long.MaxValue).Failure.Code == Common.FailureCode.InvalidRequest, "cursor overflow accepted");
        Reset(); Start(Request());
        File.Delete(Path.Combine(GenFilePaths.SaveDataFolderPath, "RimGovernorClockEvents", "00000000000000000001.xml"));
        var lost = Events(0, 1).Page;
        Check(lost.Gap && lost.LostCount == 1 && lost.NextCursor == 1 && lost.Events.Count == 0 && !lost.HasOldestCursor, "missing event was silently dropped or oldest cursor fabricated");
        Check(!Events(1, 1).Page.Gap, "gap repeated outside requested window");
    }
    private static void EventProjection()
    {
        Reset(); Start(Request()); Find.CurrentMap.mapPawns.AllPawns.Add(new Pawn { thingIDNumber = 7 });
        HomePlayUntilEventTools.LiveLetters.Clear();
        HomePlayUntilEventTools.LiveLetters.Add(new Letter { Id = "Letter_1", Label = "Ancient danger", def = new LetterDef { defName = "ThreatBig" } });
        Supervisor.FixtureEvent("letter_pause", new() { ["letterId"] = "Letter_1", ["source"] = "LetterStack.ReceiveLetter" });
        var letter = Events().Page.Events.Last().Stopped.Pause.Letter;
        Check(letter.Id == "Letter_1" && letter.Label == "Ancient danger" && letter.DefName == "ThreatBig", "trigger letter attribution");
        Supervisor.FixtureEvent("notification_new", new() { ["id"] = "Letter_2", ["label"] = "News", ["letterDef"] = "PositiveEvent", ["negative"] = false });
        Check(Events().Page.Events.Last().Notification.Letter.HasNegative && !Events().Page.Events.Last().Notification.Letter.Negative, "known false notification lost");
        Supervisor.FixtureEvent("notification_new", new() { ["id"] = "Message_3", ["text"] = "Message", ["messageType"] = "PositiveEvent", ["startingTick"] = 0, ["negative"] = false });
        Check(Events().Page.Events.Last().Notification.Message.HasStartingTick, "zero notification tick omitted");
        Supervisor.FixtureEvent("alert_new", new() { ["alertKey"] = "lowfood", ["label"] = "Low food", ["priority"] = "High" });
        Check(Events().Page.Events.Last().Alert.Key == "lowfood", "alert evidence lost");
        var injury = new Dictionary<string, object> { ["pawnId"] = 7, ["pawnName"] = "Pawn", ["position"] = new Dictionary<string, object> { ["x"] = 1, ["z"] = 2 },
            ["injuryCountBefore"] = 0, ["injuryCountAfter"] = 1, ["severityBefore"] = 0f, ["severityAfter"] = .1f, ["bleedRateBefore"] = 0f,
            ["bleedRateAfter"] = 0f, ["bloodLossBefore"] = 0f, ["bloodLossAfter"] = 0f, ["healthAtStart"] = 1f, ["healthNow"] = .9f,
            ["suppressedBy"] = "acknowledged", ["cooldownRemainingMs"] = 0L, ["newWound"] = true };
        Supervisor.FixtureEvent("injury_observed", injury);
        var observed = Events().Page.Events.Last().InjuryObserved;
        Check(observed.Pawn.PawnId == "Pawn_7" && observed.Before.InjuryCount == 0 && observed.After.InjuryCount == 1 && observed.Suppression == Clock.InjurySuppression.Acknowledged, "injury identity or evidence lost");
        Supervisor.FixtureEvent("hostiles_cleared", new() { ["consciousHostilesBefore"] = 1, ["acrossRestart"] = false,
            ["downedHostiles"] = new List<Dictionary<string, object>> { new() { ["thingId"] = 7, ["name"] = "Pawn", ["x"] = 1, ["z"] = 2 } },
            ["draftedColonists"] = new List<Dictionary<string, object>>() });
        Check(Events().Page.Events.Last().HostilesCleared.DownedHostiles.Single().PawnId == "Pawn_7", "hostile ID suffix coercion");
        Supervisor.FixtureEvent("long_event", new() { ["longEvent"] = true, ["graceMs"] = 15000, ["requestedSpeed"] = "Normal" });
        Check(Events().Page.Events.Last().ForcePauseWaiting.Pause.LongEventPending, "force pause waiting evidence");
        Supervisor.FixtureEvent("force_pause_cleared", new() { ["waitedMs"] = 100L, ["forcePauseKind"] = "long_event", ["speedRestored"] = true });
        Check(Events().Page.Events.Last().ForcePauseCleared.WaitedMs == 100, "force pause cleared evidence");
        var before = Events().Page.NewestCursor;
        try { injury["pawnId"] = 99; Supervisor.FixtureEvent("injury_observed", injury); throw new Exception("unresolved ID accepted"); }
        catch (InvalidOperationException) { Check(Events().Page.NewestCursor == before, "failed event published a cursor"); }
        Check(NativeClockEventProjection.Text(new string('x', 4095) + "😀").EndsWith("😀"), "diagnostic split Unicode scalar");
        Reset(); LongEventHandler.AnyEventNowOrWaiting = true;
        Check(Start(Request()).LongEventPending != null && NativeOperationState.ForAdmission(Identity).Ledger.Count == 0, "long event admitted clock attempt");
        LongEventHandler.AnyEventNowOrWaiting = false;
        Start(Request()); Find.TickManager.Forced = true; authority.Revoke(authority.Status().Generation, NativeControlRevocationReason.Manual); Supervisor.OnUpdate();
        Check(Status().Status.Stopped.Reason == Clock.StopReason.ExternalPause && Find.TickManager.CurTimeSpeed == TimeSpeed.Paused, "revocation during force pause retained automatic play");
    }
    private static void ReplacementGrantCannotAdoptEpoch()
    {
        Reset();
        var epoch = Start(Request()).Receipt.Applied.Status.Running.Epoch;
        var owned = new Clock.OwnedRequest { Identity = Identity, Owner = epoch.Owner.Clone() };
        var renewedAuthority = authority.Renew(lease.Generation, lease.Lease.LeaseId, "controller", 30000);
        Check(renewedAuthority.Success, "ordinary authority renewal failed");
        Check(Renew(new Clock.RenewRequest { Epoch = owned, Authority = Pre(), LeaseMs = 1000 }).Receipt != null,
            "same-grant authority renewal blocked clock renewal");
        var admitted = NativeOperationState.ForAdmission(Identity).Ledger.Count;
        authority.Revoke(authority.Status().Generation, NativeControlRevocationReason.Manual);
        lease = authority.Acquire(authority.Status().Generation, "controller", 43, 30000).Snapshot;
        Check(lease.Active && lease.Generation != epoch.Origin.NativeGeneration, "replacement grant setup failed");
        // No watcher update runs between revocation, reacquisition and these requests.
        var renewal = Renew(new Clock.RenewRequest { Epoch = owned, Authority = Pre(), LeaseMs = 30000 });
        Check(renewal.Failure?.Code == Common.FailureCode.StaleGeneration, "new grant renewed invalidated epoch before watcher update");
        var speed = Speed(new Clock.SpeedRequest { Epoch = owned, Authority = Pre(), Speed = Clock.Speed.Superfast });
        Check(speed.Failure?.Code == Common.FailureCode.StaleGeneration, "new grant changed invalidated epoch speed before watcher update");
        Check(NativeOperationState.ForAdmission(Identity).Ledger.Count == admitted, "replacement-grant refusal admitted an attempt");
        var retained = Status().Status.Running.Epoch;
        Check(retained.Origin.NativeGeneration == epoch.Origin.NativeGeneration && retained.RequestedSpeed == Clock.Speed.Normal
            && retained.LeaseRemainingMs <= 1000, "replacement request changed epoch grant, speed or deadline");
        Supervisor.OnUpdate();
        Check(Status().Status.Stopped != null && Find.TickManager.CurTimeSpeed == TimeSpeed.Paused,
            "replacement grant masked original-grant invalidation from watcher");
        Check(Pause(owned).Status.Stopped != null, "safe exact cleanup rejected after grant replacement");
    }
    private static void LostHooksCannotExtendEpoch()
    {
        Reset(); var epoch = Start(Request()).Receipt.Applied.Status.Running.Epoch;
        var owned = new Clock.OwnedRequest { Identity = Identity, Owner = epoch.Owner.Clone() };
        var count = NativeOperationState.ForAdmission(Identity).Ledger.Count;
        HarmonyLib.Harmony.Healthy = false;
        Check(Renew(new Clock.RenewRequest { Epoch = owned, Authority = Pre(), LeaseMs = 30000 }).Failure?.Code == Common.FailureCode.Unavailable,
            "clock renewal ignored removed enforcement hooks");
        Check(Speed(new Clock.SpeedRequest { Epoch = owned, Authority = Pre(), Speed = Clock.Speed.Superfast }).Failure?.Code == Common.FailureCode.Unavailable,
            "clock speed ignored removed enforcement hooks");
        Check(NativeOperationState.ForAdmission(Identity).Ledger.Count == count && Find.TickManager.CurTimeSpeed == TimeSpeed.Normal,
            "missing-hook refusal admitted or changed native speed");
        Check(Pause(owned).Status.Stopped?.PauseVerified == true, "lost hooks blocked safe exact pause cleanup");
        HarmonyLib.Harmony.Healthy = true;
    }
    private static void CorruptStoredEvents()
    {
        foreach (Action<Clock.Event> corrupt in new Action<Clock.Event>[] {
            e => e.Context = new Common.ObservationContext(), e => e.Owner = new Clock.EpochOwner(), e => e.ClearEvent(),
            e => e.Context.Identity.ClearLoadToken(), e => e.Context.Identity.MapId = -1, e => e.Context.ClearTick(), e => e.Context.Tick = -1,
            e => e.Context.NativeGeneration = 0, e => e.Owner.ControllerSessionId = " ", e => e.Owner.Epoch = 0,
            e => e.ClearObservedAtUnixMs(), e => e.ObservedAtUnixMs = -1 })
        {
            Reset(); Start(Request());
            Supervisor.FixtureEvent("alert_new", new() { ["alertKey"] = "food", ["label"] = "Low food", ["priority"] = "High" });
            var path = Path.Combine(GenFilePaths.SaveDataFolderPath, "RimGovernorClockEvents", "00000000000000000002.xml");
            var file = XElement.Load(path);
            var encoded = file.Elements("item").Single(item => (string)item.Attribute("key") == "canonicalClockEvent").Elements().Single();
            var value = Clock.Event.Parser.ParseJson(encoded.Value); corrupt(value); encoded.Value = JsonFormatter.Default.Format(value); file.Save(path);
            var reply = Events();
            Check(reply.Failure?.Code == Common.FailureCode.Unavailable && reply.Page == null,
                "semantic-invalid stored event published a partial or fabricated page");
        }
    }
    private static void ReadRecoveredHistoryBeforeStart()
    {
        Reset(); var epoch = Start(Request()).Receipt.Applied.Status.Running.Epoch;
        Pause(new Clock.OwnedRequest { Identity = Identity, Owner = epoch.Owner.Clone() });
        var recorded = Events().Page;
        Supervisor.FixtureReset();
        var recovered = Events().Page;
        Check(recovered != null && recovered.Events.SequenceEqual(recorded.Events) && recovered.NewestCursor == recorded.NewestCursor,
            "read before start failed to recover durable history");
        Check(Status().Status.NeverStarted != null && Find.TickManager.Paused && authority.Status().Generation == lease.Generation,
            "history recovery changed clock or authority");
    }
    public static void Main()
    {
        Boundaries(); OwnedLifecycle(); StopsAndContext(); EventProjection(); ReplacementGrantCannotAdoptEpoch(); LostHooksCannotExtendEpoch(); CorruptStoredEvents(); ReadRecoveredHistoryBeforeStart();
        Console.WriteLine($"Native clock: {checks} checks; production typed runtime/adapter/ledger/journal, controlled native watcher and SDK seams.");
    }
}
