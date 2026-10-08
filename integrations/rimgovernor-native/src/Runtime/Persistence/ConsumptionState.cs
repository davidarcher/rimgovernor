#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Why colony stock was spent (#2441). The numeric values are saved: append
    // only. Wire names are Names[(int)reason].
    public enum ConsumptionReason : byte
    {
        None = 0,
        // RECURRING: the maintenance rate a resource runway reads.
        BillIngredient = 1, MedicineTend = 2, FoodEaten = 3, DrugDose = 4, AnimalFeed = 5,
        NutrientPaste = 6, FuelLoaded = 7, ShellLoaded = 8, ApparelWear = 9,
        // PROJECT.
        Construction = 10,
        // LOSS.
        Rot = 11, Deterioration = 12, Fire = 13, Sold = 14, Stolen = 15, DestroyedOther = 16,
    }

    // Realized consumption (#2441): a ring of hourly increments per
    // (ThingDef, reason) (2500-tick hours, 60 days), saved with the game as one
    // packed, versioned string so the save stays small. A count may be negative (an ejected refuel or a removed
    // shell subtracts). Hooks (ConsumptionHooks) add on the game thread; reads
    // are game-thread too, but a lock keeps a stray worker-thread destroy safe.
    public sealed class ConsumptionState : GameComponent
    {
        public const int HourTicks = 2500;
        public const int WindowHours = 60 * 24;
        private const byte FormatVersion = 2;

        public static readonly string[] Names = {
            "", "bill_ingredient", "medicine_tend", "food_eaten", "drug_dose", "animal_feed", "nutrient_paste",
            "fuel_loaded", "shell_loaded", "apparel_wear", "construction", "rot", "deterioration", "fire", "sold",
            "stolen", "destroyed_other" };

        public struct Key : IEquatable<Key>
        {
            public readonly string Def;
            public readonly byte Reason;
            public Key(string def, byte reason) { Def = def; Reason = reason; }
            public bool Equals(Key other) => Reason == other.Reason && string.Equals(Def, other.Def, StringComparison.Ordinal);
            public override bool Equals(object? obj) => obj is Key other && Equals(other);
            public override int GetHashCode() => Def.GetHashCode() * 31 + Reason;
        }

        public sealed class Hour
        {
            public int Index;
            public readonly Dictionary<Key, long> Rows = new Dictionary<Key, long>();
        }

        private readonly object gate = new object();
        private string? packed;
        // The first hour this ring covers: when the component first counted.
        public int FirstHour = -1;
        // Ascending by hour; the last entry is the hour still filling.
        public readonly List<Hour> Hours = new List<Hour>();

        public ConsumptionState(Game game) { }

        // Bridge assemblies can load after Verse cached GameComponent types, so
        // attach on first use (older saves carry none).
        public static ConsumptionState For(Game game)
        {
            var state = game.GetComponent<ConsumptionState>();
            if (state == null)
            {
                state = new ConsumptionState(game);
                game.components.Add(state);
            }
            return state;
        }

        public static int HourOf(int ticks) => ticks / HourTicks;

        public void Add(string def, ConsumptionReason reason, long count, int ticksGame)
        {
            if (count == 0 || reason == ConsumptionReason.None) return;
            var hour = HourOf(ticksGame);
            var key = new Key(def, (byte)reason);
            lock (gate)
            {
                if (FirstHour < 0) FirstHour = hour;
                var bucket = Bucket(hour);
                bucket.Rows.TryGetValue(key, out var have);
                var next = have + count;
                if (next == 0) bucket.Rows.Remove(key); else bucket.Rows[key] = next;
            }
        }

        private Hour Bucket(int hour)
        {
            var at = Hours.Count;
            while (at > 0 && Hours[at - 1].Index > hour) at--;
            if (at > 0 && Hours[at - 1].Index == hour) return Hours[at - 1];
            var created = new Hour { Index = hour };
            Hours.Insert(at, created);
            Prune(hour);
            return created;
        }

        private void Prune(int hour)
        {
            var cutoff = hour - WindowHours;
            var drop = 0;
            while (drop < Hours.Count && Hours[drop].Index <= cutoff) drop++;
            if (drop > 0) Hours.RemoveRange(0, drop);
            if (FirstHour <= cutoff) FirstHour = cutoff + 1;
        }

        // Every completed hour after sinceHour (all of them when it is
        // negative), sparse. The hour still filling is never returned.
        public List<Hour> After(int sinceHour, int currentHour, out int firstHour)
        {
            lock (gate)
            {
                firstHour = FirstHour < 0 ? currentHour : Math.Max(FirstHour, currentHour - WindowHours + 1);
                var result = new List<Hour>();
                foreach (var hour in Hours)
                {
                    if (hour.Index >= currentHour || hour.Index <= sinceHour || hour.Rows.Count == 0) continue;
                    var copy = new Hour { Index = hour.Index };
                    foreach (var pair in hour.Rows) copy.Rows[pair.Key] = pair.Value;
                    result.Add(copy);
                }
                return result;
            }
        }

        public override void ExposeData()
        {
            if (Scribe.mode == LoadSaveMode.Saving) { lock (gate) packed = Pack(); }
            Scribe_Values.Look(ref packed, "rimgovernorConsumption");
            if (Scribe.mode == LoadSaveMode.LoadingVars) { lock (gate) Unpack(packed); }
        }

        // Layout (all integers LEB128 varints, counts zig-zag): version byte,
        // first hour, def table, then hours
        // as (hour delta from the previous, row count, rows).
        public string Pack()
        {
            var defs = new List<string>();
            var index = new Dictionary<string, int>(StringComparer.Ordinal);
            int Def(string name)
            {
                if (!index.TryGetValue(name, out var at)) { at = defs.Count; index[name] = at; defs.Add(name); }
                return at;
            }
            foreach (var hour in Hours) foreach (var key in hour.Rows.Keys) Def(key.Def);
            using (var stream = new MemoryStream())
            {
                stream.WriteByte(FormatVersion);
                Write(stream, FirstHour + 1);
                Write(stream, defs.Count);
                foreach (var name in defs)
                {
                    var bytes = System.Text.Encoding.UTF8.GetBytes(name);
                    Write(stream, bytes.Length);
                    stream.Write(bytes, 0, bytes.Length);
                }
                var written = 0;
                foreach (var hour in Hours) if (hour.Rows.Count > 0) written++;
                Write(stream, written);
                var previous = 0;
                foreach (var hour in Hours)
                {
                    if (hour.Rows.Count == 0) continue;
                    Write(stream, hour.Index - previous);
                    previous = hour.Index;
                    Write(stream, hour.Rows.Count);
                    foreach (var pair in hour.Rows) Row(stream, Def(pair.Key.Def), pair.Key.Reason, pair.Value);
                }
                return Convert.ToBase64String(stream.ToArray());
            }
        }

        // A string this build cannot read (a future version, damage) restarts
        // the ring empty: saves regenerate, nothing here is worth a shim.
        public void Unpack(string? text)
        {
            Hours.Clear(); FirstHour = -1;
            if (string.IsNullOrEmpty(text)) return;
            try
            {
                var data = Convert.FromBase64String(text);
                var at = 0;
                if (data[at++] != FormatVersion) return;
                var first = (int)ReadU(data, ref at) - 1;
                var defs = new string[ReadU(data, ref at)];
                for (var i = 0; i < defs.Length; i++)
                {
                    var length = (int)ReadU(data, ref at);
                    defs[i] = System.Text.Encoding.UTF8.GetString(data, at, length);
                    at += length;
                }
                var hours = (int)ReadU(data, ref at);
                var hourIndex = 0;
                for (var i = 0; i < hours; i++)
                {
                    hourIndex += (int)ReadU(data, ref at);
                    var hour = new Hour { Index = hourIndex };
                    var rows = (int)ReadU(data, ref at);
                    for (var j = 0; j < rows; j++) { ReadRow(data, ref at, defs, out var key, out var count); hour.Rows[key] = count; }
                    Hours.Add(hour);
                }
                FirstHour = first;
            }
            catch (Exception)
            {
                Hours.Clear(); FirstHour = -1;
            }
        }

        private static void Row(Stream stream, int def, byte reason, long count)
        {
            Write(stream, def);
            stream.WriteByte(reason);
            Write(stream, (count << 1) ^ (count >> 63));
        }

        private static void ReadRow(byte[] data, ref int at, string[] defs, out Key key, out long count)
        {
            var def = defs[ReadU(data, ref at)];
            var reason = data[at++];
            var zigzag = (long)ReadU(data, ref at);
            key = new Key(def, reason);
            count = (zigzag >> 1) ^ -(zigzag & 1);
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
}
