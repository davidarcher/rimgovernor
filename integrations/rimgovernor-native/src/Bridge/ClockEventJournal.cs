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

        internal void Append(Dictionary<string, object?> row)
        {
            var cursor = Convert.ToInt64(row["cursor"]);
            if (cursor != Newest + 1) throw new IOException("Native clock cursor is not consecutive.");
            var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".pending");
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
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
        // explicit cursor loss; corrupt or conflicting content remains a read failure.
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
