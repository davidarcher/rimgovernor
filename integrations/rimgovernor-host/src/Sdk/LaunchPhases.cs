using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace RimGovernor.Host.Sdk
{
    /// <summary>
    /// One finished launch phase: a UTC interval and the id of the phase it nests in (0 for a direct child of the
    /// <c>launch</c> root). Ids are taken when a phase starts, so a parent's id is always lower than its children's.
    /// </summary>
    public sealed class LaunchPhase
    {
        public LaunchPhase(int id, int parent, string name, DateTime startUtc, DateTime endUtc)
        {
            Id = id;
            Parent = parent;
            Name = name;
            StartUtc = startUtc;
            EndUtc = endUtc;
        }

        public int Id { get; }

        public int Parent { get; }

        public string Name { get; }

        public DateTime StartUtc { get; }

        public DateTime EndUtc { get; }
    }

    /// <summary>
    /// The game-side launch waterfall (#3286), buffered as plain data until the brain's span listener exists: the
    /// host records RimWorld's load stages and its own startup here, the bridge records the brain's waits, and the
    /// bridge replays <see cref="Snapshot"/> as the <c>launch</c> spans once the brain has started. Always on; the
    /// buffer is bounded, so a game that never starts a brain holds at most <see cref="Capacity"/> small records.
    /// </summary>
    public static class LaunchPhases
    {
        public const int Capacity = 512;

        private static readonly object Gate = new object();
        private static readonly List<LaunchPhase> Done = new List<LaunchPhase>();
        // Stopwatch-derived wall time: DateTime.UtcNow is coarse on some hosts, and a phase is often a few ms.
        private static readonly DateTime OriginUtc = DateTime.UtcNow;
        private static readonly long OriginTimestamp = Stopwatch.GetTimestamp();
        private static int lastId;

        [ThreadStatic] private static Stack<int> open;

        /// <summary>Phases dropped because the buffer was full.</summary>
        public static int Dropped { get; private set; }

        /// <summary>The current time on the waterfall's clock.</summary>
        public static DateTime UtcNow =>
            OriginUtc + TimeSpan.FromTicks((long)((Stopwatch.GetTimestamp() - OriginTimestamp) * ((double)TimeSpan.TicksPerSecond / Stopwatch.Frequency)));

        /// <summary>Takes the id a phase starting now records under.</summary>
        public static int NextId() => Interlocked.Increment(ref lastId);

        /// <summary>Records one finished phase.</summary>
        public static void Record(int id, int parent, string name, DateTime startUtc, DateTime endUtc)
        {
            lock (Gate)
            {
                if (Done.Count >= Capacity)
                {
                    Dropped++;
                    return;
                }
                Done.Add(new LaunchPhase(id, parent, string.IsNullOrWhiteSpace(name) ? "unknown" : name, startUtc, endUtc < startUtc ? startUtc : endUtc));
            }
        }

        /// <summary>
        /// Times the enclosed code as a phase nested in the phase this thread has open (a direct child of the root
        /// when none is open). Dispose ends it.
        /// </summary>
        public static IDisposable Phase(string name)
        {
            var stack = open ??= new Stack<int>();
            var id = NextId();
            var scope = new Scope(id, stack.Count == 0 ? 0 : stack.Peek(), name, UtcNow);
            stack.Push(id);
            return scope;
        }

        /// <summary>Every phase recorded so far, in the order they finished.</summary>
        public static IReadOnlyList<LaunchPhase> Snapshot()
        {
            lock (Gate)
            {
                return Done.ToArray();
            }
        }

        private sealed class Scope : IDisposable
        {
            private readonly int _id;
            private readonly int _parent;
            private readonly string _name;
            private readonly DateTime _startUtc;
            private bool _disposed;

            public Scope(int id, int parent, string name, DateTime startUtc)
            {
                _id = id;
                _parent = parent;
                _name = name;
                _startUtc = startUtc;
            }

            public void Dispose()
            {
                if (_disposed)
                    return;
                _disposed = true;
                var stack = open;
                if (stack != null && stack.Count > 0 && stack.Peek() == _id)
                    stack.Pop();
                Record(_id, _parent, _name, _startUtc, UtcNow);
            }
        }
    }
}
