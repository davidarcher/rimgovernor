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
        private static byte[] Normalized<T>(T section) where T : class, IMessage<T>
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
