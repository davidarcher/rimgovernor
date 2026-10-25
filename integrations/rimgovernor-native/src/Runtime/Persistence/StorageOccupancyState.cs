#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using RimWorld;
using Verse;

namespace RimGovernor.Native
{
    // Storage occupancy: a ring of hourly samples (2500-tick hours, 15 days)
    // of the item stacks each of the player's storage groups holds (stockpile
    // zones and storage buildings, by load id), saved with the game as one
    // packed, versioned string like ConsumptionState. A sample is taken at an
    // hour's first tick (StorageOccupancySampler); a group present then has a
    // row, zero included, so a group created mid-window starts its own
    // samples and a deleted one stops. 15 days, not the consumption ring's 60:
    // every group is sampled every hour (dense, unlike sparse consumption),
    // and the inflow reader (policy StoreInflow) reads days, not weeks.
    // Game-thread only: the sampler, the read tool and Scribe.
    public sealed class StorageOccupancyState : GameComponent
    {
        public const int HourTicks = ConsumptionState.HourTicks;
        public const int WindowHours = 15 * 24;
        private const byte FormatVersion = 1;

        public sealed class Sample
        {
            public int Hour;
            public readonly Dictionary<string, int> Stacks = new Dictionary<string, int>(StringComparer.Ordinal);
        }

        private string? packed;
        // Ascending by hour.
        public readonly List<Sample> Samples = new List<Sample>();

        public StorageOccupancyState(Game game) { }

        // Bridge assemblies can load after Verse cached GameComponent types, so
        // attach on first use (older saves carry none).
        public static StorageOccupancyState For(Game game)
        {
            var state = game.GetComponent<StorageOccupancyState>();
            if (state == null)
            {
                state = new StorageOccupancyState(game);
                game.components.Add(state);
            }
            return state;
        }

        public static int HourOf(int ticks) => ticks / HourTicks;

        // Records the map's groups into hour's sample (several home maps share one).
        public void Record(int hour, IEnumerable<KeyValuePair<string, int>> groups)
        {
            var at = Samples.Count;
            while (at > 0 && Samples[at - 1].Hour > hour) at--;
            Sample sample;
            if (at > 0 && Samples[at - 1].Hour == hour) sample = Samples[at - 1];
            else
            {
                sample = new Sample { Hour = hour };
                Samples.Insert(at, sample);
            }
            foreach (var pair in groups) sample.Stacks[pair.Key] = pair.Value;
            var cutoff = hour - WindowHours;
            var drop = 0;
            while (drop < Samples.Count && Samples[drop].Hour <= cutoff) drop++;
            if (drop > 0) Samples.RemoveRange(0, drop);
        }

        // The requested group's samples after sinceHour, ascending; empty when it has none.
        public List<KeyValuePair<int, int>> After(string group, int sinceHour)
        {
            var result = new List<KeyValuePair<int, int>>();
            foreach (var sample in Samples)
                if (sample.Hour > sinceHour && sample.Stacks.TryGetValue(group, out var stacks))
                    result.Add(new KeyValuePair<int, int>(sample.Hour, stacks));
            return result;
        }

        // The stacks every player storage group on map holds: the items on its cells.
        public static Dictionary<string, int> GroupStacks(Map map)
        {
            var groups = new Dictionary<string, int>(StringComparer.Ordinal);
            var player = Faction.OfPlayerSilentFail;
            foreach (var group in map.haulDestinationManager.AllGroupsListInPriorityOrder)
            {
                var parent = group.parent;
                if (!(parent is ILoadReferenceable owner)) continue;
                if (parent is Thing thing && (player == null || thing.Faction != player)) continue;
                var stacks = 0;
                foreach (var cell in group.CellsList)
                {
                    var things = map.thingGrid.ThingsListAtFast(cell);
                    for (var i = 0; i < things.Count; i++) if (things[i].def.category == ThingCategory.Item) stacks++;
                }
                groups[owner.GetUniqueLoadID()] = stacks;
            }
            return groups;
        }

        public override void ExposeData()
        {
            if (Scribe.mode == LoadSaveMode.Saving) packed = Pack();
            Scribe_Values.Look(ref packed, "rimgovernorStorageOccupancy");
            if (Scribe.mode == LoadSaveMode.LoadingVars) Unpack(packed);
        }

        // Layout (all integers LEB128 varints): version byte, group table, then
        // samples as (hour delta from the previous, changed row count, rows of
        // (group, stacks), gone count, gone groups). A row is written only when
        // the group's stacks differ from the previous sample, so a quiet store
        // costs nothing per hour; a group the previous sample had and this one
        // lacks is listed as gone.
        public string Pack()
        {
            var names = new List<string>();
            var index = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var sample in Samples)
                foreach (var group in sample.Stacks.Keys)
                    if (!index.ContainsKey(group)) { index[group] = names.Count; names.Add(group); }
            using (var stream = new MemoryStream())
            {
                stream.WriteByte(FormatVersion);
                Write(stream, names.Count);
                foreach (var name in names)
                {
                    var bytes = System.Text.Encoding.UTF8.GetBytes(name);
                    Write(stream, bytes.Length);
                    stream.Write(bytes, 0, bytes.Length);
                }
                Write(stream, Samples.Count);
                var previousHour = 0;
                var previous = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var sample in Samples)
                {
                    Write(stream, sample.Hour - previousHour);
                    previousHour = sample.Hour;
                    var changed = new List<KeyValuePair<string, int>>();
                    foreach (var pair in sample.Stacks)
                        if (!previous.TryGetValue(pair.Key, out var had) || had != pair.Value) changed.Add(pair);
                    var gone = new List<string>();
                    foreach (var group in previous.Keys) if (!sample.Stacks.ContainsKey(group)) gone.Add(group);
                    Write(stream, changed.Count);
                    foreach (var pair in changed) { Write(stream, index[pair.Key]); Write(stream, pair.Value); }
                    Write(stream, gone.Count);
                    foreach (var group in gone) Write(stream, index[group]);
                    previous = new Dictionary<string, int>(sample.Stacks, StringComparer.Ordinal);
                }
                return Convert.ToBase64String(stream.ToArray());
            }
        }

        // A string this build cannot read (a future version, damage) restarts
        // the ring empty: saves regenerate, nothing here is worth a shim.
        public void Unpack(string? text)
        {
            Samples.Clear();
            if (string.IsNullOrEmpty(text)) return;
            try
            {
                var data = Convert.FromBase64String(text);
                var at = 0;
                if (data[at++] != FormatVersion) return;
                var names = new string[ReadU(data, ref at)];
                for (var i = 0; i < names.Length; i++)
                {
                    var length = (int)ReadU(data, ref at);
                    names[i] = System.Text.Encoding.UTF8.GetString(data, at, length);
                    at += length;
                }
                var count = (int)ReadU(data, ref at);
                var hour = 0;
                var current = new Dictionary<string, int>(StringComparer.Ordinal);
                for (var i = 0; i < count; i++)
                {
                    hour += (int)ReadU(data, ref at);
                    var changed = (int)ReadU(data, ref at);
                    for (var j = 0; j < changed; j++)
                    {
                        var group = names[ReadU(data, ref at)];
                        current[group] = (int)ReadU(data, ref at);
                    }
                    var gone = (int)ReadU(data, ref at);
                    for (var j = 0; j < gone; j++) current.Remove(names[ReadU(data, ref at)]);
                    var sample = new Sample { Hour = hour };
                    foreach (var pair in current) sample.Stacks[pair.Key] = pair.Value;
                    Samples.Add(sample);
                }
            }
            catch (Exception)
            {
                Samples.Clear();
            }
        }

        private static void Write(Stream stream, long value)
        {
            var v = (ulong)value;
            while (v >= 0x80) { stream.WriteByte((byte)(v | 0x80)); v >>= 7; }
            stream.WriteByte((byte)v);
        }

        private static ulong ReadU(byte[] data, ref int at)
        {
            ulong result = 0;
            for (var shift = 0; ; shift += 7)
            {
                var b = data[at++];
                result |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return result;
                if (shift > 63) throw new InvalidDataException("varint too long");
            }
        }
    }

    // Takes the hourly storage sample of each player home map at the hour's
    // first tick. Holds nothing: the samples live in StorageOccupancyState.
    public sealed class StorageOccupancySampler : MapComponent
    {
        public StorageOccupancySampler(Map map) : base(map) { }

        public override void MapComponentTick()
        {
            var tick = Find.TickManager.TicksGame;
            if (tick % StorageOccupancyState.HourTicks != 0 || !map.IsPlayerHome || Current.Game == null) return;
            StorageOccupancyState.For(Current.Game).Record(StorageOccupancyState.HourOf(tick), StorageOccupancyState.GroupStacks(map));
        }
    }
}
