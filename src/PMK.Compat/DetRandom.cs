using System;

namespace PMK.Compat
{
    /// <summary>
    /// Replacement for <c>new Random()</c> in the game. The original seeds
    /// every generator from the clock; online play needs every machine to
    /// draw the same numbers, so each generator is seeded from a shared
    /// session seed plus a creation counter. Creation order is identical on
    /// every machine because the simulation runs in lockstep from boot.
    ///
    /// Every number drawn during game updates is also folded into
    /// <see cref="StreamHash"/>, which online peers compare to detect desyncs.
    /// </summary>
    public static class DetRandom
    {
        private static int sessionSeed = Environment.TickCount;
        private static int counter;

        public static int SessionSeed => sessionSeed;

        /// <summary>Rolling hash of all random numbers drawn outside Draw.</summary>
        public static ulong StreamHash { get; private set; } = 14695981039346656037UL;
        public static long DrawCount { get; private set; }

        /// <summary>Set by the launcher while the game draws (draw-only randomness is cosmetic).</summary>
        public static bool Suspended { get; set; }

        public static void Reset(int seed)
        {
            sessionSeed = seed;
            counter = 0;
            StreamHash = 14695981039346656037UL;
            DrawCount = 0;
        }

        public static Random Create() => new TrackedRandom(Mix(sessionSeed, counter++));

        public static Random Create(int seed) => new TrackedRandom(seed);

        internal static int Mix(int seed, int n)
        {
            unchecked
            {
                uint h = (uint)seed * 0x9E3779B1u ^ (uint)n * 0x85EBCA77u;
                h ^= h >> 15;
                h *= 0xC2B2AE3Du;
                h ^= h >> 13;
                return (int)(h & 0x7FFFFFFF);
            }
        }

        internal static void Record(long value)
        {
            if (Suspended) return;
            unchecked
            {
                StreamHash = (StreamHash ^ (ulong)value) * 1099511628211UL;
                DrawCount++;
            }
        }

        /// <summary>Same sequence as <c>new Random(seed)</c>; only observes the results.</summary>
        private sealed class TrackedRandom : Random
        {
            public TrackedRandom(int seed) : base(seed) { }

            public override int Next()
            {
                int v = base.Next();
                Record(v);
                return v;
            }

            public override int Next(int maxValue)
            {
                int v = base.Next(maxValue);
                Record(v);
                return v;
            }

            public override int Next(int minValue, int maxValue)
            {
                int v = base.Next(minValue, maxValue);
                Record(v);
                return v;
            }

            public override double NextDouble()
            {
                double v = base.NextDouble();
                Record(BitConverter.DoubleToInt64Bits(v));
                return v;
            }
        }
    }
}
