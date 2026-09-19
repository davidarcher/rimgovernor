#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Verse;

namespace HomeBridge.BridgeTools
{
    // One immutable file per event keeps publication atomic without rewriting
    // prior history. The private native profile owns retention and isolation.
    internal sealed class ClockEventJournal
    {
        private readonly string directory;
        internal long Newest { get; private set; }
        // Retained rows whose file is present but no longer decodes as the
        // event at its cursor: external damage to an immutable file. Bounded;
        // the cursors are health evidence, never a repair list.
        private const int CorruptCapacity = 32;
        private readonly SortedDictionary<long, string> corrupt = new SortedDictionary<long, string>();
        internal KeyValuePair<long, string>[] Corrupt { get { return corrupt.ToArray(); } }

        internal ClockEventJournal()
        {
            directory = Path.Combine(GenFilePaths.SaveDataFolderPath, "RimGovernorClockEvents");
            Directory.CreateDirectory(directory);
            foreach (var pending in Directory.GetFiles(directory, "*.pending"))
            {
                // A fully flushed row can outlive a crash before its rename.
                // Partial or conflicting rows fail closed instead of disappearing.
                var document = XElement.Load(pending);
                var row = Decode(document) as Dictionary<string, object?> ?? throw new IOException("Native event row is not a record.");
                var target = EventPath(Convert.ToInt64(row["cursor"]));
                if (File.Exists(target))
                {
                    if (!XNode.DeepEquals(document, XElement.Load(target)))
                        throw new IOException("Native event publication conflicts with retained history.");
                    File.Delete(pending);
                }
                else File.Move(pending, target);
            }
            var ids = Directory.GetFiles(directory, "*.xml")
                .Select(p => long.Parse(Path.GetFileNameWithoutExtension(p), CultureInfo.InvariantCulture))
                .OrderBy(x => x).ToArray();
            for (int i = 0; i < ids.Length; i++)
                if (ids[i] != i + 1L) throw new IOException("Native clock event journal has a gap.");
            Newest = ids.Length == 0 ? 0 : ids[ids.Length - 1];
        }

        private string EventPath(long cursor) { return Path.Combine(directory, cursor.ToString("D20", CultureInfo.InvariantCulture) + ".xml"); }

        // The retained file of one published row, for a disposable fixture to
        // damage. Nothing in production resolves a row's path outside this class.
        internal string RetainedPath(long cursor)
        {
            if (cursor < 1 || cursor > Newest) throw new ArgumentOutOfRangeException(nameof(cursor));
            return EventPath(cursor);
        }

        internal void Append(Dictionary<string, object?> row)
        {
            var cursor = Convert.ToInt64(row["cursor"]);
            if (cursor != Newest + 1) throw new IOException("Native clock cursor is not consecutive.");
            // The temporary is named by its cursor, no longer than the row it
            // becomes: a save-data folder deep enough that only the final name
            // fits Windows MAX_PATH otherwise fails every publication with a
            // DirectoryNotFoundException before the first row exists (#388).
            // A retry of the same cursor after a failed write overwrites it.
            var temporary = Path.Combine(directory, cursor.ToString("X16", CultureInfo.InvariantCulture) + ".pending");
            // The directory can vanish under a live process (a tool clearing the
            // save-data folder between harnesses); the rows it held then read as
            // lost, and the journal keeps appending rather than failing every
            // event for the rest of the process (#119).
            Directory.CreateDirectory(directory);
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                Encode(row).Save(stream);
                stream.Flush(true);
            }
            File.Move(temporary, EventPath(cursor));
            Newest = cursor;
        }

        internal List<Dictionary<string, object?>> Read(long after, int limit)
        {
            if (after < 0 || after > Newest) throw new IOException("Native clock cursor is outside the retained journal.");
            var rows = new List<Dictionary<string, object?>>();
            if (after == Newest) return rows;
            for (long cursor = after + 1; cursor <= Newest && rows.Count < limit; cursor++)
            {
                var row = Decode(XElement.Load(EventPath(cursor))) as Dictionary<string, object?> ?? throw new IOException("Native event row is not a record.");
                if (Convert.ToInt64(row["cursor"]) != cursor) throw new IOException("Native event identity changed.");
                rows.Add(row);
                if (cursor == Newest) break;
            }
            return rows;
        }

        internal sealed class Window
        {
            internal readonly List<Dictionary<string, object?>> Rows = new List<Dictionary<string, object?>>();
            internal long Next;
            internal ulong Lost;
        }

        // Bound file probes as well as returned rows. A missing immutable file is
        // explicit cursor loss, and so is one that exists but no longer decodes
        // as the row at its cursor: the damage is to one retained file, so the
        // window reports it as lost (the controller holds on the gap, as for a
        // wiped directory) instead of refusing every read that crosses it for
        // the rest of the process. The cursor is kept for runtime_health.
        internal Window ReadWindow(long after, int limit)
        {
            if (after < 0 || after > Newest || limit < 1 || limit > 128)
                throw new ArgumentOutOfRangeException(nameof(after));
            var result = new Window { Next = after };
            for (int scanned = 0; result.Next < Newest && scanned < limit; scanned++)
            {
                var cursor = checked(result.Next + 1);
                try
                {
                    var row = Decode(XElement.Load(EventPath(cursor))) as Dictionary<string, object?> ?? throw new IOException("Native event row is not a record.");
                    if (Convert.ToInt64(row["cursor"]) != cursor) throw new IOException("Native event identity changed.");
                    result.Rows.Add(row);
                }
                catch (FileNotFoundException) { result.Lost++; }
                catch (DirectoryNotFoundException) { result.Lost++; }
                catch (Exception error) when (error is IOException || error is System.Xml.XmlException || error is InvalidDataException
                    || error is FormatException || error is OverflowException || error is InvalidCastException || error is KeyNotFoundException
                    || error is InvalidOperationException)
                {
                    result.Lost++;
                    if (corrupt.Count < CorruptCapacity || corrupt.ContainsKey(cursor)) corrupt[cursor] = error.GetType().Name;
                }
                result.Next = cursor;
            }
            return result;
        }

        private static XElement Encode(object? value)
        {
            if (value == null) return new XElement("null");
            if (value is string) return new XElement("text", value);
            var map = value as IDictionary<string, object?>;
            if (map != null) return new XElement("map", map.Select(p => new XElement("item", new XAttribute("key", p.Key), Encode(p.Value))));
            var list = value as IEnumerable;
            if (list != null) return new XElement("list", list.Cast<object>().Select(Encode));
            if (value is bool) return new XElement("bool", value);
            if (value is float || value is double || value is decimal)
                return new XElement("real", Convert.ToDouble(value).ToString("R", CultureInfo.InvariantCulture));
            if (value is byte || value is short || value is int || value is long)
                return new XElement("integer", Convert.ToInt64(value).ToString(CultureInfo.InvariantCulture));
            throw new InvalidDataException("Unsupported native event value: " + value.GetType().FullName);
        }

        private static object? Decode(XElement node)
        {
            switch (node.Name.LocalName)
            {
                case "null": return null;
                case "text": return node.Value;
                case "bool": return bool.Parse(node.Value);
                case "real": return double.Parse(node.Value, CultureInfo.InvariantCulture);
                case "integer": return long.Parse(node.Value, CultureInfo.InvariantCulture);
                case "list": return node.Elements().Select(Decode).ToList();
                case "map": return node.Elements("item").ToDictionary(p => (string)p.Attribute("key"), p => Decode(p.Elements().Single()));
                default: throw new InvalidDataException("Unsupported native event encoding.");
            }
        }
    }
}
