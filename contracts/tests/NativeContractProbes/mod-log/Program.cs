using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimGovernor.Host.Sdk;

// The mod's diagnostic log (#2058): a bounded ring that holds entries until a
// subscriber exists, replays them in order as late, counts what it dropped,
// rate limits per call site, refuses reentrant writes and never makes a
// caller wait on the publisher. The last block drives the real GabpServer and
// event manager over TCP. Waits are hang guards, not latency assertions.
internal static class ModLogProbe
{
    private static int checks;
    private static void Check(bool value, string name) { checks++; if (!value) throw new Exception(name); }

    private sealed class Capture
    {
        internal readonly List<Dictionary<string, object>> Sent = new List<Dictionary<string, object>>();
        internal Func<string, object, DateTimeOffset?, Task> Emit => (channel, payload, at) =>
        {
            Check(channel == ModLogPublisher.Channel, "events go out on rimgovernor.log");
            lock (Sent) Sent.Add((Dictionary<string, object>)payload);
            return Task.FromResult(0);
        };
    }

    internal static void Invoke()
    {
        Ring();
        Overflow();
        RateLimit();
        Reentrancy();
        NeverBlocks();
        OverTheWire();
        Console.WriteLine("mod-log: " + checks + " checks passed");
    }

    private static void Reset()
    {
        ModLog.ResetForProbe();
        ModLog.TickSource = null;
        ModLog.CurrentTrace = null;
    }

    private static void Ring()
    {
        Reset();
        var tick = 100L;
        ModLog.TickSource = () => tick;
        ModLog.CurrentTrace = "t1/s1";
        ModLog.Info("probe", "while nobody listens", key: "a");
        tick = 101;
        ModLog.Warn("probe", "explicit trace", trace: "t2/s2", key: "b");
        ModLog.CurrentTrace = null;
        var capture = new Capture();
        var subscribers = false;
        var publisher = new ModLogPublisher(capture.Emit, () => subscribers);

        Check(publisher.PumpOnce() == 0 && capture.Sent.Count == 0, "nothing is sent with no subscriber");
        subscribers = true;
        Check(publisher.PumpOnce() == 2, "both held entries are replayed on subscribe");
        Check((long)capture.Sent[0]["seq"] == 1 && (long)capture.Sent[1]["seq"] == 2, "replay keeps write order");
        Check((bool)capture.Sent[0]["late"] && (bool)capture.Sent[1]["late"], "replayed entries are late");
        Check((long)capture.Sent[0]["tick"] == 100 && (long)capture.Sent[1]["tick"] == 101, "entries carry the tick they were written at");
        Check((string)capture.Sent[0]["trace"] == "t1/s1" && (string)capture.Sent[1]["trace"] == "t2/s2", "ambient and explicit traces are carried");
        Check((string)capture.Sent[0]["level"] == "info" && (string)capture.Sent[1]["level"] == "warn" && (string)capture.Sent[0]["component"] == "probe", "level and component are carried");

        ModLog.Info("probe", "live", key: "c");
        Check(publisher.PumpOnce() == 1 && !(bool)capture.Sent[2]["late"], "a line written with a subscriber is not late");
        Check(publisher.PumpOnce() == 0, "published entries leave the ring");
    }

    private static void Overflow()
    {
        Reset();
        for (var i = 0; i < ModLog.RingCapacity + 10; i++) ModLog.Info("probe", "n" + i, key: "k" + i);
        var capture = new Capture();
        var publisher = new ModLogPublisher(capture.Emit, () => true);
        Check(publisher.PumpOnce() == ModLog.RingCapacity + 1, "a full ring publishes its entries plus one summary");
        Check((string)capture.Sent[0]["type"] == "overflow" && (long)capture.Sent[0]["dropped"] == 10, "the summary counts the dropped entries once");
        Check((long)capture.Sent[0]["seq"] == 10 && (long)capture.Sent[1]["seq"] == 11, "the summary sits before the oldest survivor");
        Check((string)capture.Sent[1]["msg"] == "n10" && (string)capture.Sent[ModLog.RingCapacity]["msg"] == "n" + (ModLog.RingCapacity + 9), "the oldest entries were dropped, the rest stay in order");
        Check(publisher.PumpOnce() == 0, "the overflow is reported once");
    }

    private static void RateLimit()
    {
        Reset();
        var now = 0L;
        ModLog.ClockMs = () => now;
        try
        {
            for (var i = 0; i < 8; i++) ModLog.Info("probe", "same site " + i, key: "site");
            ModLog.Info("probe", "other site", key: "other");
            var capture = new Capture();
            var publisher = new ModLogPublisher(capture.Emit, () => true);
            Check(publisher.PumpOnce() == ModLog.RateBurst + 1, "a site logs its burst; the rest is dropped; another site is independent");
            now += ModLog.RateWindowMs;
            ModLog.Info("probe", "next window", key: "site");
            publisher.PumpOnce();
            var last = capture.Sent[capture.Sent.Count - 1];
            Check((long)last["suppressed"] == 3, "the next line from the site carries how many were dropped");
        }
        finally { ModLog.ClockMs = () => System.Diagnostics.Stopwatch.GetTimestamp() * 1000 / System.Diagnostics.Stopwatch.Frequency; }
    }

    private static void Reentrancy()
    {
        Reset();
        // A sink that logs while it sends, and a tick source that logs: neither recurses.
        var sent = 0;
        var publisher = new ModLogPublisher((channel, payload, at) => { sent++; ModLog.Warn("probe", "from the sender", key: "inner"); return Task.FromResult(0); }, () => true);
        ModLog.TickSource = () => { ModLog.Warn("probe", "from the tick source", key: "tick"); return 5; };
        ModLog.Info("probe", "outer", key: "outer");
        Check(ModLog.ReentrantDrops == 1, "a write from inside the helper is dropped");
        ModLog.TickSource = null;
        Check(publisher.PumpOnce() == 1 && sent == 1, "the entry published once");
        Check(ModLog.ReentrantDrops == 2, "a write from inside the publisher is dropped");
        Check(publisher.PumpOnce() == 0, "the dropped write left nothing behind");
    }

    private static void NeverBlocks()
    {
        Reset();
        using (var entered = new ManualResetEventSlim(false))
        using (var release = new ManualResetEventSlim(false))
        {
            var publisher = new ModLogPublisher((channel, payload, at) =>
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(60))) throw new TimeoutException("send never released");
                return Task.FromResult(0);
            }, () => true);
            publisher.Start();
            try
            {
                ModLog.Info("probe", "first", key: "first");
                Check(entered.Wait(TimeSpan.FromSeconds(60)), "the publisher thread reached the blocked send");
                var writer = Task.Run(() =>
                {
                    for (var i = 0; i < ModLog.RingCapacity * 2; i++) ModLog.Info("probe", "w" + i, key: "w" + i);
                });
                Check(writer.Wait(TimeSpan.FromSeconds(60)), "writes complete while the publisher is stuck in a send");
            }
            finally
            {
                release.Set();
                publisher.Stop();
            }
        }
    }

    private static void OverTheWire()
    {
        Reset();
        const string token = "probe-token";
        // Written before any client exists: held in the ring.
        ModLog.Warn("probe", "before connect", trace: "tt/ss", key: "pre");
        ModLog.Info("probe", "second before connect", key: "pre2");
        var server = new RimGovernor.Host.Gab.Server.GabpServer(new RimGovernor.Host.Gab.Server.GabpServerConfig { Port = 0, Token = token });
        server.Events.RegisterChannel(ModLogPublisher.Channel, ModLogPublisher.Description);
        server.Events.ChannelSubscribed += channel => { if (channel == ModLogPublisher.Channel) ModLog.Poke(); };
        var publisher = new ModLogPublisher(server.Events.EmitEventAsync, () => server.Events.GetSubscriberCount(ModLogPublisher.Channel) > 0);
        publisher.Start();
        server.StartAsync().GetAwaiter().GetResult();
        try
        {
            using (var client = new GabDispatchProbe.Client(server.Port))
            {
                var hello = GabDispatchProbe.Settle(client.Send("session/hello", new Dictionary<string, object> { ["token"] = token, ["bridgeVersion"] = "probe", ["platform"] = "probe", ["launchId"] = "probe" }), "hello");
                var channels = (JArray)hello["result"]["capabilities"]["events"];
                Check(channels.ToString().Contains(ModLogPublisher.Channel), "the welcome advertises rimgovernor.log");
                Check(!client.Events.TryTake(out _, 200), "nothing arrives before subscribing");

                var subscribed = GabDispatchProbe.Settle(client.Send("events/subscribe", new Dictionary<string, object> { ["channels"] = new[] { ModLogPublisher.Channel } }), "subscribe");
                Check(subscribed["error"] == null || subscribed["error"].Type == JTokenType.Null, "subscribe accepted");
                var first = Next(client);
                var second = Next(client);
                Check((string)first["channel"] == ModLogPublisher.Channel && (string)first["payload"]["msg"] == "before connect" && (bool)first["payload"]["late"], "the first held entry is replayed late");
                Check((string)first["payload"]["trace"] == "tt/ss" && (string)second["payload"]["msg"] == "second before connect", "replay keeps order and trace");
                ModLog.Info("probe", "live line", key: "live");
                var live = Next(client);
                Check((string)live["payload"]["msg"] == "live line" && !(bool)live["payload"]["late"], "a line written after subscribing arrives live");
                Check((int)live["seq"] > (int)first["seq"], "channel sequence increases");
            }
        }
        finally
        {
            publisher.Stop();
            server.Dispose();
        }
    }

    private static JObject Next(GabDispatchProbe.Client client)
    {
        JObject message;
        Check(client.Events.TryTake(out message, (int)GabDispatchProbe.Guard.TotalMilliseconds), "an event arrives within the hang guard");
        return message;
    }
}
