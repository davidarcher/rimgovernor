#nullable enable
using System;
using System.Buffers;
using System.Text.Json;
using RimGovernor.Brain.Domain;
using RimGovernor.Contracts.Wire;
using System.Collections.Generic;
using System.Linq;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// Unchanged singleton sections are left out of a snapshot frame.
    /// Each omittable section is encoded with every nested context tick
    /// cleared and compared with the bytes last published; an equal one is
    /// omitted. Every frame lists each section's watermark, carried or not:
    /// seq counts its changes, captured_tick is the frame that last carried
    /// it. A section that failed to read gets no watermark and is sent in
    /// full once it reads again. A keyframe (an open, or a reader's keyframe
    /// request after a seq gap) carries every section.
    ///
    /// Runs on the encoder worker, one frame at a time (SnapshotStream's
    /// pending gate), so its state needs no lock.
    /// </summary>
    internal static class SnapshotSections
    {
        private sealed class Held
        {
            internal byte[]? Bytes;
            internal ulong Seq;
            internal long Tick;
        }

        private static readonly Dictionary<string, Held> held = new Dictionary<string, Held>(StringComparer.Ordinal);

        internal static Obs.BundleSnapshot Elide(Obs.BundleSnapshot frame, bool keyframe)
        {
            var tick = frame.Context?.Tick ?? 0;
            var marks = new List<Obs.SectionWatermark>();
            frame = frame with
            {
                Emergency = Section("emergency", frame.Emergency, keyframe, tick, marks),
                ColonyFacts = Section("colony_facts", frame.ColonyFacts, keyframe, tick, marks),
                Population = Section("population", frame.Population, keyframe, tick, marks),
                Research = Section("research", frame.Research, keyframe, tick, marks),
                Traders = Section("traders", frame.Traders, keyframe, tick, marks),
                WorldProgression = Section("world_progression", frame.WorldProgression, keyframe, tick, marks),
                Ideology = Section("ideology", frame.Ideology, keyframe, tick, marks),
                Pawns = Keyed("pawns", frame.Pawns, t => t.Pawns, r => r.Pawn?.Id, t => t with { Pawns = default, Removed = default },
                    (t, kept, gone) => t with { Pawns = EquatableArray.From(kept), Removed = EquatableArray.From(gone) }, keyframe, tick, marks),
                Buildings = Keyed("buildings", frame.Buildings, t => t.Buildings, r => r.Building?.Id, t => t with { Buildings = default, Removed = default },
                    (t, kept, gone) => t with { Buildings = EquatableArray.From(kept), Removed = EquatableArray.From(gone) }, keyframe, tick, marks),
                Things = Keyed("things", frame.Things, t => t.Things, r => r.Entity?.Id, t => t with { Things = default, Removed = default },
                    (t, kept, gone) => t with { Things = EquatableArray.From(kept), Removed = EquatableArray.From(gone) }, keyframe, tick, marks),
            };
            return frame with { Watermarks = [.. frame.Watermarks, .. marks] };
        }

        // Per-row state of a keyed table: the row hashes, the hash of
        // everything but the rows, and whether the next carried copy must be
        // whole (nothing published yet, or the section failed to read).
        private sealed class Table
        {
            internal readonly RowDiff Diff = new RowDiff();
            internal ulong Shell;
            internal bool NeedFull = true;
        }

        private static readonly Dictionary<string, Table> tables = new Dictionary<string, Table>(StringComparer.Ordinal);

        /// A keyed table is omitted while no row or other field changed. A
        /// changed one is carried whole in a keyframe, else as a delta: the
        /// rows whose hash changed and the ids that left, against the table
        /// published at the watermark's base_seq. Every other field of the
        /// section rides each delta.
        private static T? Keyed<T, R>(string name, T? section, Func<T, IReadOnlyList<R>> rows, Func<R, string?> id, Func<T, T> shellOf,
            Func<T, IReadOnlyList<R>, IReadOnlyList<string>, T> delta, bool keyframe, long tick, List<Obs.SectionWatermark> marks)
            where T : class where R : class
        {
            if (!held.TryGetValue(name, out var h)) held[name] = h = new Held();
            if (!tables.TryGetValue(name, out var table)) tables[name] = table = new Table();
            if (section == null)
            {
                h.Bytes = null;
                table.Diff.Reset();
                table.NeedFull = true;
                return null;
            }
            var list = rows(section);
            var byId = new List<KeyValuePair<string, R>>(list.Count);
            var unique = new HashSet<string>(StringComparer.Ordinal);
            var keyed = true;
            foreach (var row in list)
            {
                var key = id(row);
                if (string.IsNullOrEmpty(key) || !unique.Add(key!)) { keyed = false; continue; }
                byId.Add(new KeyValuePair<string, R>(key!, row));
            }
            var step = table.Diff.Step(byId);
            var shell = RowDiff.Hash(shellOf(section));
            var changed = step.Any || shell != table.Shell || h.Seq == 0 || h.Bytes == null;
            table.Shell = shell;
            var baseSeq = h.Seq;
            if (changed)
            {
                h.Bytes = Array.Empty<byte>();
                h.Seq++;
                h.Tick = tick;
            }
            var whole = keyframe || table.NeedFull || !keyed;
            var watermark = new Obs.SectionWatermark { Section = name, Seq = h.Seq, CapturedTick = h.Tick };
            if (!changed && !keyframe) { marks.Add(watermark); return null; }
            if (changed && !whole)
            {
                var kept = new List<R>();
                foreach (var row in byId)
                    if (step.Changed.Contains(row.Key)) kept.Add(row.Value);
                section = delta(section, kept, step.Removed);
                watermark = watermark with { Delta = true, BaseSeq = baseSeq };
            }
            marks.Add(watermark);
            if (whole && changed) table.NeedFull = !keyed;
            return section;
        }

        private static T? Section<T>(string name, T? section, bool keyframe, long tick, List<Obs.SectionWatermark> marks) where T : class
        {
            if (!held.TryGetValue(name, out var h)) held[name] = h = new Held();
            if (section == null)
            {
                h.Bytes = null;
                return null;
            }
            var bytes = Normalized(section);
            var unchanged = h.Bytes != null && h.Bytes.SequenceEqual(bytes);
            if (!unchanged)
            {
                h.Bytes = bytes;
                h.Seq++;
                h.Tick = tick;
            }
            marks.Add(new Obs.SectionWatermark { Section = name, Seq = h.Seq, CapturedTick = h.Tick });
            return unchanged && !keyframe ? null : section;
        }

        // The section's JSON bytes with every nested context tick cleared: a
        // section whose content holds still encodes the same at every tick.
        // An ObservationContext is the one wire object that holds both an
        // identity and a tick.
        internal static byte[] Normalized<T>(T section) where T : class
        {
            var bytes = WireJson.SerializeToUtf8Bytes(section);
            using var document = JsonDocument.Parse(bytes);
            var output = new ArrayBufferWriter<byte>(bytes.Length);
            using (var writer = new Utf8JsonWriter(output)) WriteWithoutContextTicks(document.RootElement, writer);
            return output.WrittenSpan.ToArray();
        }

        private static void WriteWithoutContextTicks(JsonElement element, Utf8JsonWriter writer)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    var context = element.TryGetProperty("identity", out _) && element.TryGetProperty("tick", out _);
                    writer.WriteStartObject();
                    foreach (var property in element.EnumerateObject())
                    {
                        if (context && property.NameEquals("tick")) continue;
                        writer.WritePropertyName(property.Name);
                        WriteWithoutContextTicks(property.Value, writer);
                    }
                    writer.WriteEndObject();
                    break;
                case JsonValueKind.Array:
                    writer.WriteStartArray();
                    foreach (var item in element.EnumerateArray()) WriteWithoutContextTicks(item, writer);
                    writer.WriteEndArray();
                    break;
                default:
                    element.WriteTo(writer);
                    break;
            }
        }
    }
}
