using RimGovernor.Brain.Telemetry;
using RimGovernor.Brain.Telemetry.Otlp;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class LaunchSpansTests
{
    [Fact]
    public async Task BufferedPhasesRoundTripThroughTheRealOtlpReaderAsOneLaunchTree()
    {
        var directory = Path.Combine(Path.GetTempPath(), "launch-spans-" + Guid.NewGuid().ToString("N"));
        var t0 = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
        var brainStarted = t0.AddSeconds(20);
        try
        {
            await using (var writer = OtlpFileWriter.Start(new() { Dir = directory }))
            {
                // Recorded in finishing order, as the buffer holds them; ids follow start order.
                var phases = new List<LaunchInterval>
                {
                    new(1, 0, "launch.pre_mods", t0, t0.AddSeconds(2)),
                    new(4, 3, "rimworld/ThingDef resolver", t0.AddSeconds(5), t0.AddSeconds(6)),
                    new(3, 0, "rimworld/Resolve references.", t0.AddSeconds(4), t0.AddSeconds(7)),
                    new(2, 0, "rimworld/LoadModXML()", t0.AddSeconds(2.5), t0.AddSeconds(2.5)),
                    new(6, 5, "orphan", t0.AddSeconds(8), t0.AddSeconds(9)),
                    new(7, 0, "brain.host_start", t0.AddSeconds(18), brainStarted),
                };
                Assert.Equal(7, LaunchSpans.Replay(phases, brainStarted));
            }

            var spans = new List<OtlpSpan>();
            OtlpReader.ReadSpans(Path.Combine(directory, OtlpFiles.Traces), spans.Add);
            var root = Assert.Single(spans, s => s.Name == LaunchSpans.Root && s.ParentSpanId == "");
            var tree = spans.Where(s => s.TraceId == root.TraceId).ToDictionary(s => s.Name);
            Assert.Equal(7, tree.Count);
            Assert.Equal(TimeSpan.FromSeconds(20), root.Duration);

            Assert.Equal(root.SpanId, tree["launch.pre_mods"].ParentSpanId);
            Assert.Equal(root.SpanId, tree["rimworld/Resolve references."].ParentSpanId);
            Assert.Equal(tree["rimworld/Resolve references."].SpanId, tree["rimworld/ThingDef resolver"].ParentSpanId);
            Assert.Equal(root.SpanId, tree["orphan"].ParentSpanId);
            Assert.Equal(TimeSpan.FromSeconds(3), tree["rimworld/Resolve references."].Duration);
            Assert.Equal(TimeSpan.FromSeconds(2), tree["brain.host_start"].Duration);
            // A zero-length stage keeps its recorded time instead of the replay's.
            Assert.True(tree["rimworld/LoadModXML()"].Duration < TimeSpan.FromMilliseconds(1));
            Assert.Equal(root.EndUnixNano, tree["brain.host_start"].EndUnixNano);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void NoPhasesWriteNoSpans() => Assert.Equal(0, LaunchSpans.Replay([], DateTime.UtcNow));
}
