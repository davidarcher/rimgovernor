#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// The bounded encoder capacity detached replies are formatted on (#644).
    /// A caller reserves a slot before it admits an expensive capture, so no
    /// more captured replies exist than encoders to own them; a reservation
    /// that cannot be had within the bound is the caller's typed capacity
    /// refusal rather than an unbounded queue. Encoding runs on the thread
    /// pool through TaskScheduler.Default, which Unity's game thread never
    /// drains; ProtoBoundary.EncodeDetached still checks thread identity.
    /// Control hops never reserve here, so saturated bulk encoding cannot
    /// delay a renew or stop.
    /// </summary>
    internal static class ReplyEncoder
    {
        internal const int Slots = 2;

        /// How long a caller waits for a slot before refusing: a bundle's
        /// encode is milliseconds, so a wait this long is saturation.
        internal const int ReserveTimeoutMs = 10000;

        private static readonly SemaphoreSlim Capacity = new SemaphoreSlim(Slots, Slots);

        /// Slots free now, for probes and a status line.
        internal static int Available => Capacity.CurrentCount;

        /// <summary>One reserved slot; disposing releases it exactly once.</summary>
        internal sealed class Lease : IDisposable
        {
            private int _released;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _released, 1) == 0) Capacity.Release();
            }
        }

        /// <summary>
        /// A slot, or null when none frees within timeoutMs. Never blocks a
        /// thread; a cancelled token throws and reserves nothing.
        /// </summary>
        internal static async Task<Lease?> Reserve(CancellationToken cancellationToken, int timeoutMs = ReserveTimeoutMs)
            => await Capacity.WaitAsync(timeoutMs, cancellationToken).ConfigureAwait(false) ? new Lease() : null;

        internal static Task<object> Run(Func<object> encode, CancellationToken cancellationToken)
            => Task.Factory.StartNew(encode, cancellationToken, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
    }
}
