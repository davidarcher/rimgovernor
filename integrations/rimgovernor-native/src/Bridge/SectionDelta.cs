#nullable enable

using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    // Changed-since reads of whole snapshots (#773): the colony facts, the
    // pawn list and the bundle. The contract is Obs.SectionDelta's; this is
    // the native half.
    //
    // One tracker per (tool, identity, request shape) remembers, for every
    // part of the reply tree it tracks, a digest of that part and the
    // watermark at which it last changed. A part is a message-typed field or
    // a keyed element of a repeated message field, down to MaxDepth levels
    // and at least MinBlock serialized bytes; smaller parts ride inside
    // their parent. Change detection is by digest at read time, as for the
    // entity lists (#358): the reply is projected whole anyway, so comparing
    // each part against the last visit costs one serialization per level and
    // misses no field, where hooks would have to name every game path that
    // moves a need, a hediff or a work priority. Continuous values that move
    // (needs, positions) make only their small part changed; the large parts
    // beside them (health, gear, settings, biography, acquisition rows,
    // planning sections) are held.
    //
    // A change is stamped with the watermark of the read that saw it: the
    // tick, then a sequence within that tick, so two reads at one paused
    // tick are ordered exactly. A part is held only if this tracker saw it
    // in every one of its reads since the caller's (continuity), so the
    // caller's copy exists and equals it; an ask names its tracker, so a
    // copy from another tracker (another shape, or one evicted) is never
    // held against. Contexts equal to the root's are stripped before
    // digesting, so a part that says the same thing at a later tick is
    // unchanged; the caller restamps them. Tombstones follow the entity
    // lists' bounds (EntityTracking: one game day, a count cap). An ask this
    // tracker cannot answer (another tracker's, older than the window or a
    // dropped tombstone, or not before this read) gets a full reply inline,
    // without since, so the caller replaces its copy in one round trip
    // (#795).
    internal sealed class SectionDelta
    {
        private const int MinBlock = 96;
        private const int MaxDepth = 4;
        private const int ForgetAfter = 8;
        private const int MaxTrackers = 64;

        private static readonly Dictionary<string, SectionDelta> Trackers = new Dictionary<string, SectionDelta>();
        private static long nextId = Stopwatch.GetTimestamp();
        private static long watermarkTick = long.MinValue;
        private static ulong watermarkSeq;

        // A watermark as one comparable value: the tick, then seq.
        private struct Mark
        {
            public long Tick; public ulong Seq;
            public int CompareTo(Mark other) => Tick != other.Tick ? Tick.CompareTo(other.Tick) : Seq.CompareTo(other.Seq);
        }

        private struct Entry { public ulong Digest; public Mark Changed; public int Seen; }

        private readonly string id;
        private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>();
        private readonly Dictionary<string, HashSet<string>> members = new Dictionary<string, HashSet<string>>();
        private readonly Dictionary<string, Mark> removed = new Dictionary<string, Mark>();
        private Mark forgot = new Mark { Tick = long.MinValue }; // newest tombstone the cap dropped
        private int visits; // this tracker's own read count, for continuity
        private Mark now;
        private long used;

        private SectionDelta(string id) { this.id = id; }

        // Shape is the tracker key of a request: the tool, the identity and
        // the request's bytes with the changed_since ask (and whatever else
        // the caller cleared from its copy) removed.
        internal static string Shape(string tool, Common.Identity? identity, IMessage request)
            => tool + "|" + (identity == null ? "" : Hex(Fnv(identity.ToByteArray()))) + "|" + Hex(Fnv(request.ToByteArray()));

        // Apply tracks root (a snapshot about to be encoded) under shape and,
        // when ask names this tracker, strips the parts unchanged since its
        // watermark; since is set exactly when it did (a delta). Safe on any
        // thread: the
        // snapshot must be detached from the game.
        internal static Obs.SectionDelta Apply(string shape, Obs.SectionDeltaAsk? ask, IMessage root, Common.ObservationContext context)
        {
            var watch = Stopwatch.StartNew();
            SectionDelta tracker;
            Mark mark;
            lock (Trackers)
            {
                if (context.Tick != watermarkTick) { watermarkTick = context.Tick; watermarkSeq = 0; }
                mark = new Mark { Tick = watermarkTick, Seq = ++watermarkSeq };
                if (!Trackers.TryGetValue(shape, out tracker))
                {
                    if (Trackers.Count >= MaxTrackers)
                        foreach (var stale in Trackers.OrderBy(pair => pair.Value.used).Take(Trackers.Count - MaxTrackers + 1).Select(pair => pair.Key).ToList()) Trackers.Remove(stale);
                    Trackers[shape] = tracker = new SectionDelta((++nextId).ToString("x", CultureInfo.InvariantCulture));
                }
                tracker.used = Stopwatch.GetTimestamp();
            }
            var delta = new Obs.SectionDelta { Tracker = tracker.id, AsOf = new Obs.Watermark { Tick = mark.Tick, Seq = mark.Seq } };
            lock (tracker)
            {
                var stripped = new List<KeyValuePair<IMessage, FieldDescriptor>>();
                Strip(root, context, stripped);
                tracker.visits++;
                tracker.now = mark;
                tracker.Visit(root, "", 0);
                foreach (var gone in tracker.removed.Where(pair => mark.Tick - pair.Value.Tick > EntityTracking.TombstoneWindow).Select(pair => pair.Key).ToList()) tracker.removed.Remove(gone);
                if (tracker.removed.Count > EntityTracking.MaxTombstones)
                    foreach (var pair in tracker.removed.OrderBy(pair => pair.Value.Tick).ThenBy(pair => pair.Value.Seq).Take(tracker.removed.Count - EntityTracking.MaxTombstones).ToList())
                    {
                        tracker.removed.Remove(pair.Key);
                        if (pair.Value.CompareTo(tracker.forgot) > 0) tracker.forgot = pair.Value;
                    }
                if (ask != null && ask.Tracker == tracker.id && ask.Since != null)
                {
                    var since = new Mark { Tick = ask.Since.Tick, Seq = ask.Since.Seq };
                    if (since.CompareTo(mark) < 0 && mark.Tick - since.Tick <= EntityTracking.TombstoneWindow && since.CompareTo(tracker.forgot) > 0)
                    {
                        delta.Since = ask.Since.Clone();
                        tracker.Hold(root, "", 0, since, delta.Held);
                        delta.Removed.Add(tracker.removed.Where(pair => pair.Value.CompareTo(since) > 0).Select(pair => pair.Key).OrderBy(key => key, System.StringComparer.Ordinal));
                    }
                }
                foreach (var pair in stripped) pair.Value.Accessor.SetValue(pair.Key, context.Clone());
                foreach (var gone in tracker.entries.Where(pair => tracker.visits - pair.Value.Seen > ForgetAfter).Select(pair => pair.Key).ToList()) tracker.entries.Remove(gone);
                delta.Tracked = (uint)tracker.entries.Count;
            }
            delta.DigestMs = watch.Elapsed.TotalMilliseconds;
            return delta;
        }

        // Visit records every tracked part of message as seen now.
        private void Visit(IMessage message, string path, int depth)
        {
            foreach (var field in message.Descriptor.Fields.InFieldNumberOrder())
            {
                if (field.FieldType != FieldType.Message || field.IsMap) continue;
                var at = Join(path, field.FieldNumber);
                if (!field.IsRepeated)
                {
                    if (!(field.Accessor.GetValue(message) is IMessage child) || child is Common.ObservationContext) continue;
                    if (Note(at, child.ToByteArray()) && depth + 1 < MaxDepth) Visit(child, at, depth + 1);
                    continue;
                }
                var list = (IList)field.Accessor.GetValue(message);
                var keys = Keys(field.MessageType, list);
                Members(at, keys);
                if (list.Count == 0) continue;
                if (keys == null) { Note(at, Concat(list)); continue; }
                for (var i = 0; i < list.Count; i++)
                {
                    var element = (IMessage)list[i];
                    var item = at + "[" + keys[i] + "]";
                    if (Note(item, element.ToByteArray()) && depth + 1 < MaxDepth) Visit(element, item, depth + 1);
                }
            }
        }

        // Hold strips from message every tracked part unchanged since since,
        // listing its path, and descends into the changed ones as Visit did.
        private void Hold(IMessage message, string path, int depth, Mark since, ICollection<string> held, FieldDescriptor? key = null)
        {
            foreach (var field in message.Descriptor.Fields.InFieldNumberOrder())
            {
                if (field.FieldType != FieldType.Message || field.IsMap) continue;
                var at = Join(path, field.FieldNumber);
                if (!field.IsRepeated)
                {
                    if (!(field.Accessor.GetValue(message) is IMessage child) || child is Common.ObservationContext) continue;
                    if (!entries.TryGetValue(at, out var entry) || entry.Seen != visits) continue;
                    if (entry.Changed.CompareTo(since) <= 0 && field != key) { field.Accessor.Clear(message); held.Add(at); }
                    else if (depth + 1 < MaxDepth) Hold(child, at, depth + 1, since, held);
                    continue;
                }
                var list = (IList)field.Accessor.GetValue(message);
                if (list.Count == 0) continue;
                var keys = Keys(field.MessageType, list);
                if (keys == null)
                {
                    if (entries.TryGetValue(at, out var whole) && whole.Seen == visits && whole.Changed.CompareTo(since) <= 0) { list.Clear(); held.Add(at); }
                    continue;
                }
                for (var i = 0; i < list.Count; i++)
                {
                    var item = at + "[" + keys[i] + "]";
                    if (!entries.TryGetValue(item, out var entry) || entry.Seen != visits) continue;
                    if (entry.Changed.CompareTo(since) <= 0) { list[i] = Stub(field.MessageType, (IMessage)list[i]); held.Add(item); }
                    else if (depth + 1 < MaxDepth) Hold((IMessage)list[i], item, depth + 1, since, held, KeyField(field.MessageType));
                }
            }
        }

        // Members records the keys a keyed list holds now: a key gone since
        // the last visit is a tombstone stamped now, one back again is no
        // longer removed. A list of keyless elements has no members.
        private void Members(string path, string[]? keys)
        {
            var current = keys == null ? new HashSet<string>() : new HashSet<string>(keys);
            if (members.TryGetValue(path, out var before))
                foreach (var key in before) if (!current.Contains(key)) removed[path + "[" + key + "]"] = now;
            foreach (var key in current) removed.Remove(path + "[" + key + "]");
            if (current.Count == 0) members.Remove(path); else members[path] = current;
        }

        // Note records one part's bytes at this visit; it reports whether the
        // part is tracked (large enough to hold alone).
        private bool Note(string path, byte[] bytes)
        {
            if (bytes.Length < MinBlock) return false;
            var digest = Fnv(bytes);
            if (entries.TryGetValue(path, out var entry) && entry.Digest == digest && entry.Seen == visits - 1)
                entries[path] = new Entry { Digest = digest, Changed = entry.Changed, Seen = visits };
            else entries[path] = new Entry { Digest = digest, Changed = now, Seen = visits };
            return true;
        }

        // Keys are a repeated field's element keys (Obs.SectionDelta), or
        // null when any element lacks one or two share one: such a list is
        // tracked whole.
        private static string[]? Keys(MessageDescriptor type, IList list)
        {
            var keys = new string[list.Count];
            var seen = new HashSet<string>();
            for (var i = 0; i < list.Count; i++)
            {
                var key = Key(type, (IMessage)list[i]);
                if (key == null || key.Length == 0 || key.IndexOfAny(new[] { '.', '[', ']' }) >= 0 || !seen.Add(key)) return null;
                keys[i] = key;
            }
            return keys;
        }

        private static string? Key(MessageDescriptor type, IMessage element)
        {
            var direct = IdField(type);
            if (direct != null) return direct.Accessor.GetValue(element) as string;
            var nested = KeyField(type);
            if (nested == null) return null;
            return nested.Accessor.GetValue(element) is IMessage child ? IdField(nested.MessageType)!.Accessor.GetValue(child) as string : null;
        }

        // KeyField is the first (by number) singular message field whose type
        // has a string id.
        private static FieldDescriptor? KeyField(MessageDescriptor type) =>
            type.Fields.InFieldNumberOrder().FirstOrDefault(field => field.FieldType == FieldType.Message && !field.IsRepeated && !field.IsMap && IdField(field.MessageType) != null);

        private static FieldDescriptor? IdField(MessageDescriptor type)
        {
            var field = type.FindFieldByName("id");
            return field != null && field.FieldType == FieldType.String && !field.IsRepeated ? field : null;
        }

        // Stub is element reduced to its key.
        private static IMessage Stub(MessageDescriptor type, IMessage element)
        {
            var stub = type.Parser.ParseFrom(new byte[0]);
            var direct = IdField(type);
            if (direct != null) { direct.Accessor.SetValue(stub, direct.Accessor.GetValue(element)); return stub; }
            var nested = KeyField(type)!;
            var id = IdField(nested.MessageType)!;
            var child = nested.MessageType.Parser.ParseFrom(new byte[0]);
            id.Accessor.SetValue(child, id.Accessor.GetValue((IMessage)nested.Accessor.GetValue(element)));
            nested.Accessor.SetValue(stub, child);
            return stub;
        }

        // Strip clears every ObservationContext field equal to context in the
        // tree under message, recording each for restoring.
        private static void Strip(IMessage message, Common.ObservationContext context, List<KeyValuePair<IMessage, FieldDescriptor>> stripped)
        {
            foreach (var field in message.Descriptor.Fields.InFieldNumberOrder())
            {
                if (field.FieldType != FieldType.Message || field.IsMap) continue;
                if (field.IsRepeated)
                {
                    foreach (var item in (IList)field.Accessor.GetValue(message)) if (item is IMessage nested) Strip(nested, context, stripped);
                    continue;
                }
                var value = field.Accessor.GetValue(message) as IMessage;
                if (value == null) continue;
                if (value is Common.ObservationContext found)
                {
                    if (found.Equals(context)) { field.Accessor.Clear(message); stripped.Add(new KeyValuePair<IMessage, FieldDescriptor>(message, field)); }
                    continue;
                }
                Strip(value, context, stripped);
            }
        }

        private static string Join(string path, int number) => path.Length == 0 ? number.ToString(CultureInfo.InvariantCulture) : path + "." + number.ToString(CultureInfo.InvariantCulture);

        private static byte[] Concat(IList list)
        {
            var output = new System.IO.MemoryStream();
            foreach (var item in list) ((IMessage)item).WriteDelimitedTo(output);
            return output.ToArray();
        }

        private static ulong Fnv(byte[] bytes)
        {
            ulong hash = 14695981039346656037UL;
            foreach (var b in bytes) { hash ^= b; hash *= 1099511628211UL; }
            return hash;
        }

        private static string Hex(ulong value) => value.ToString("x16", CultureInfo.InvariantCulture);
    }
}
