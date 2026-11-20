using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace PMK.Launcher
{
    /// <summary>
    /// Measurement for the CPS-2 feasibility study (enabled with PMK_PROFILE=1):
    /// time per game update, and the game's reachable memory measured as a
    /// 68000 would hold it (4-byte references and floats, packed fields).
    /// </summary>
    public static class Profiling
    {
        public static readonly bool Enabled = Environment.GetEnvironmentVariable("PMK_PROFILE") == "1";
        private static readonly List<double> updatesUs = new List<double>();
        private static long frames;

        public static void RecordUpdate(long ticks)
        {
            if (!Enabled) return;
            frames++;
            if (frames > 120) updatesUs.Add(ticks * 1e6 / System.Diagnostics.Stopwatch.Frequency);
            if (frames % 600 == 0) Report();
        }

        public static void Report()
        {
            if (updatesUs.Count == 0) return;
            var s = updatesUs.OrderBy(x => x).ToList();
            Log.Write($"PROFILE frame {frames}: update us avg {s.Average():F1} p50 {s[s.Count / 2]:F1} p95 {s[(int)(s.Count * 0.95)]:F1} max {s[^1]:F1}");
            updatesUs.Clear();
            var game = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "AncHell");
            if (game != null) Log.Write("PROFILE memory: " + MeasureMemory(game));
        }

        private static string MeasureMemory(Assembly game)
        {
            var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var byType = new Dictionary<string, (int count, long bytes)>();
            long staticBytes = 0;
            var stack = new Stack<object>();
            foreach (var t in game.GetTypes())
            {
                foreach (var f in t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (f.IsLiteral) continue;
                    staticBytes += SizeOf(f.FieldType);
                    try
                    {
                        if (!f.FieldType.IsValueType && f.GetValue(null) is object v) stack.Push(v);
                    }
                    catch (Exception) { }
                }
            }
            while (stack.Count > 0)
            {
                var o = stack.Pop();
                if (o == null || !seen.Add(o)) continue;
                var t = o.GetType();
                if (t.Assembly != game && !(o is Array) && !(t.IsGenericType && t.Namespace == "System.Collections.Generic")) continue;
                long bytes;
                if (o is Array arr)
                {
                    var et = t.GetElementType();
                    bytes = 4 + (long)arr.Length * SizeOf(et);
                    if (!et.IsValueType || (et.IsValueType && !et.IsPrimitive && !et.IsEnum))
                        foreach (var e in arr) if (e != null && !e.GetType().IsPrimitive) stack.Push(e);
                }
                else if (o is IList list)
                {
                    bytes = 8 + list.Count * 4L;
                    foreach (var e in list) if (e != null) stack.Push(e);
                }
                else
                {
                    bytes = 4;
                    foreach (var f in AllInstanceFields(t))
                    {
                        bytes += SizeOf(f.FieldType);
                        if (!f.FieldType.IsPrimitive && !f.FieldType.IsEnum)
                        {
                            try { var v = f.GetValue(o); if (v != null) stack.Push(v); } catch (Exception) { }
                        }
                    }
                }
                string key = o is Array ? "array of " + t.GetElementType().Name : t.Name;
                byType.TryGetValue(key, out var cur);
                byType[key] = (cur.count + 1, cur.bytes + bytes);
            }
            long total = byType.Values.Sum(v => v.bytes);
            var top = byType.OrderByDescending(kv => kv.Value.bytes).Take(12).Select(kv => $"{kv.Key} x{kv.Value.count}={kv.Value.bytes / 1024}K");
            return $"statics {staticBytes / 1024}K, reachable objects {total / 1024}K in {seen.Count} objects; top: {string.Join(", ", top)}";
        }

        private static IEnumerable<FieldInfo> AllInstanceFields(Type t)
        {
            for (; t != null && t != typeof(object); t = t.BaseType)
                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    yield return f;
        }

        private static int SizeOf(Type t)
        {
            if (t == typeof(bool) || t == typeof(byte) || t == typeof(sbyte)) return 1;
            if (t == typeof(short) || t == typeof(ushort) || t == typeof(char)) return 2;
            if (t == typeof(long) || t == typeof(ulong) || t == typeof(double)) return 8;
            if (t.IsEnum) return 4;
            if (t.IsPrimitive) return 4;
            if (t.IsValueType && !t.IsPrimitive)
                return Math.Max(1, t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Sum(f => SizeOf(f.FieldType)));
            return 4; // a reference
        }
    }
}
