#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// Unchanged singleton sections are left out of a snapshot frame (#1347).
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

        internal static void Elide(Obs.BundleSnapshot frame, bool keyframe)
        {
            var tick = frame.Context?.Tick ?? 0;
            frame.Emergency = Section(frame, "emergency", frame.Emergency, keyframe, tick);
            frame.ColonyFacts = Section(frame, "colony_facts", frame.ColonyFacts, keyframe, tick);
            frame.Population = Section(frame, "population", frame.Population, keyframe, tick);
            frame.Research = Section(frame, "research", frame.Research, keyframe, tick);
            frame.Traders = Section(frame, "traders", frame.Traders, keyframe, tick);
            frame.WorldProgression = Section(frame, "world_progression", frame.WorldProgression, keyframe, tick);
            frame.Pawns = Keyed(frame, "pawns", frame.Pawns, t => t.Pawns, r => r.Pawn?.Id, t => t.Removed, keyframe, tick);
            frame.Buildings = Keyed(frame, "buildings", frame.Buildings, t => t.Buildings, r => r.Building?.Id, t => t.Removed, keyframe, tick);
            frame.Things = Keyed(frame, "things", frame.Things, t => t.Things, r => r.Thing_?.Id, t => t.Removed, keyframe, tick);
        }

        // Per-row state of a keyed table (#1348): the row hashes, the hash of
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
        private static T? Keyed<T, R>(Obs.BundleSnapshot frame, string name, T? section, Func<T, Google.Protobuf.Collections.RepeatedField<R>> rows,
            Func<R, string?> id, Func<T, Google.Protobuf.Collections.RepeatedField<string>> removed, bool keyframe, long tick)
            where T : class, IMessage<T> where R : class, IMessage<R>
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
            var shellCopy = section.Clone();
            rows(shellCopy).Clear();
            removed(shellCopy).Clear();
            var shell = RowDiff.Hash(shellCopy);
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
            frame.Watermarks.Add(watermark);
            if (!changed && !keyframe) return null;
            if (changed && !whole)
            {
                list.Clear();
                foreach (var row in byId)
                    if (step.Changed.Contains(row.Key)) list.Add(row.Value);
                removed(section).AddRange(step.Removed);
                watermark.Delta = true;
                watermark.BaseSeq = baseSeq;
            }
            if (whole && changed) table.NeedFull = !keyed;
            return section;
        }

        private static T? Section<T>(Obs.BundleSnapshot frame, string name, T? section, bool keyframe, long tick) where T : class, IMessage<T>
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
            frame.Watermarks.Add(new Obs.SectionWatermark { Section = name, Seq = h.Seq, CapturedTick = h.Tick });
            return unchanged && !keyframe ? null : section;
        }

        // The section's bytes with every nested context tick cleared: a
        // section whose content holds still encodes the same at every tick.
        internal static byte[] Normalized<T>(T section) where T : class, IMessage<T>
        {
            var copy = section.Clone();
            ClearTicks(copy);
            return copy.ToByteArray();
        }

        private static void ClearTicks(IMessage message)
        {
            if (message is Common.ObservationContext context) { context.ClearTick(); return; }
            foreach (var field in message.Descriptor.Fields.InFieldNumberOrder())
            {
                if (field.FieldType != FieldType.Message && field.FieldType != FieldType.Group) continue;
                var value = field.Accessor.GetValue(message);
                if (value == null) continue;
                if (field.IsMap)
                {
                    foreach (DictionaryEntry entry in (IDictionary)value)
                        if (entry.Value is IMessage nested) ClearTicks(nested);
                }
                else if (field.IsRepeated)
                {
                    foreach (var item in (IList)value)
                        if (item is IMessage nested) ClearTicks(nested);
                }
                else if (value is IMessage nested) ClearTicks(nested);
            }
        }
    }
}
