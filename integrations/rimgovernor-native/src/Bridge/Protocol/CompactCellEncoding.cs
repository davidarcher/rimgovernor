#nullable enable
using System.Collections.Generic;
using Google.Protobuf;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    // Lossless planning-field encoding; the tracking grid remains the only
    // cross-read state. Tables belong to this reply, never to a client session.
    internal static class CompactCellEncoding
    {
        internal const int Limit = 65536;

        internal static bool Supports(Obs.CellFields f) => !f.Terrain && f.Roof && f.Visibility
            && f.Traversal && f.Zone && !f.Areas && !f.Things && !f.Designations && f.Room && f.Growth;

        internal static void Encode(Obs.CellsSnapshot snapshot)
        {
            var compact = new Obs.CompactCells();
            var strings = new Dictionary<string, uint>();
            var glows = new Dictionary<double, uint>();
            uint StringIndex(string value) {
                if (!strings.TryGetValue(value, out var index)) {
                    index = (uint)compact.Strings.Count; strings.Add(value, index); compact.Strings.Add(value);
                }
                return index;
            }
            int next = 0;
            for (int z = snapshot.Region.Minimum.Z; z <= snapshot.Region.Maximum.Z; z++) {
                var bytes = new List<byte>();
                for (int x = snapshot.Region.Minimum.X; x <= snapshot.Region.Maximum.X; x++) {
                    var cell = next < snapshot.Cells.Count ? snapshot.Cells[next] : null;
                    if (cell == null || cell.Cell.X != x || cell.Cell.Z != z) { bytes.Add(1); bytes.Add(0); continue; }
                    next++;
                    if (cell.Fogged) { bytes.Add(2); bytes.Add(0); continue; }
                    int flags = (cell.Walkable ? 4 : 0) | (cell.Passable ? 8 : 0)
                        | (cell.Occupied ? 16 : 0) | (cell.Doorway ? 32 : 0)
                        | (cell.SupportsLight ? 64 : 0) | (cell.StorageEmpty ? 128 : 0)
                        | (cell.Indoors ? 256 : 0) | (cell.Polluted ? 512 : 0)
                        | (cell.HasFertility ? 1024 : 0) | (cell.HasRoof ? 2048 : 0)
                        | (cell.HasZoneId ? 4096 : 0) | (cell.HasRoomId ? 8192 : 0)
                        | (cell.NaturalRock ? 16384 : 0);
                    bytes.Add((byte)flags); bytes.Add((byte)(flags >> 8));
                    if (cell.HasRoof) Varint(bytes, StringIndex(cell.Roof));
                    if (cell.HasZoneId) Varint(bytes, StringIndex(cell.ZoneId));
                    if (cell.HasRoomId) Varint(bytes, StringIndex(cell.RoomId));
                    if (!glows.TryGetValue(cell.Glow, out var glow)) {
                        glow = (uint)compact.Glow.Count; glows.Add(cell.Glow, glow); compact.Glow.Add(cell.Glow);
                    }
                    Varint(bytes, glow);
                    if (cell.HasFertility) compact.Fertility.Add(cell.Fertility);
                }
                compact.Rows.Add(ByteString.CopyFrom(bytes.ToArray()));
            }
            snapshot.Cells.Clear();
            snapshot.Compact = compact;
        }

        private static void Varint(List<byte> bytes, uint value) {
            while (value >= 128) { bytes.Add((byte)(value | 128)); value >>= 7; }
            bytes.Add((byte)value);
        }
    }
}
