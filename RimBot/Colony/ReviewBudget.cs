using System;
using System.Collections.Generic;

namespace RimBot.Colony
{
    // No game dependencies: tested independently of RimWorld and the provider.
    public sealed class ReviewBudget
    {
        private readonly Queue<double> requests = new Queue<double>();
        private double lastReview = double.NegativeInfinity;
        public bool CanReview(double now, double interval) => now - lastReview >= interval;
        public void StartReview(double now) { lastReview = now; }
        public int Used(double now)
        {
            while (requests.Count > 0 && now - requests.Peek() >= 3600) requests.Dequeue();
            return requests.Count;
        }
        public bool TryRequest(double now, int limit)
        {
            int used = Used(now);
            if (limit > 0 && used >= limit) return false;
            requests.Enqueue(now);
            return true;
        }
    }
}
