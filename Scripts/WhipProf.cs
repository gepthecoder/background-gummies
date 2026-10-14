using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

/// <summary>
/// Where the main thread's time goes, for whipit_perf.txt: every Update / LateUpdate / FixedUpdate of ours is timed (a
/// stopwatch around it, a fraction of a microsecond), and TransparentWindow times the phases of Unity's frame (scripts,
/// input, physics, rendering...). TransparentWindow writes the totals every 10 s and clears them.
/// </summary>
public static class WhipProf
{
    struct Acc { public long ticks; public int calls; }
    static readonly Dictionary<string, Acc> s_acc = new Dictionary<string, Acc>();
    static readonly List<KeyValuePair<string, Acc>> s_sort = new List<KeyValuePair<string, Acc>>();

    public static long T() => Stopwatch.GetTimestamp();

    public static void Add(string key, long t0)
    {
        long d = Stopwatch.GetTimestamp() - t0;
        s_acc.TryGetValue(key, out Acc a);
        a.ticks += d;
        a.calls++;
        s_acc[key] = a;
    }

    /// <summary>The ones starting with prefix (or not, if exclude), biggest first, as "name 1.2" (ms per frame).</summary>
    public static string Top(int frames, int n, string prefix, bool exclude)
    {
        s_sort.Clear();
        foreach (var kv in s_acc) if (kv.Key.StartsWith(prefix) != exclude) s_sort.Add(kv);
        s_sort.Sort((a, b) => b.Value.ticks.CompareTo(a.Value.ticks));
        var sb = new StringBuilder();
        double perMs = Stopwatch.Frequency / 1000.0 * System.Math.Max(1, frames);
        for (int i = 0; i < s_sort.Count && i < n; i++)
        {
            double ms = s_sort[i].Value.ticks / perMs;
            if (ms < 0.05) break;
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(exclude ? s_sort[i].Key : s_sort[i].Key.Substring(prefix.Length)).Append(' ').Append(ms.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    public static void Clear() => s_acc.Clear();
}
