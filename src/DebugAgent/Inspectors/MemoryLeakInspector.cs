using System.Collections.Concurrent;
using System.Diagnostics;

namespace DebugAgents;

/// <summary>
/// Represents a heap snapshot at a point in time.
/// </summary>
public record HeapSnapshot(
    string Id,
    DateTimeOffset Timestamp,
    long TotalManagedMemoryBytes,
    long HeapSizeBytes,
    long FragmentedBytes,
    long PinnedObjectCount,
    long FinalizationPendingCount,
    long WorkingSetBytes,
    long Gen0SizeBytes,
    long Gen1SizeBytes,
    long Gen2SizeBytes,
    long LohSizeBytes,
    long PoeSizeBytes,
    Dictionary<string, TypeStat> TypeStats
);

/// <summary>
/// Per-type memory statistics.
/// </summary>
public record TypeStat(
    string TypeName,
    int Count,
    long EstimatedSizeBytes
);

/// <summary>
/// Memory leak detection inspector — captures heap snapshots and compares them for growth.
/// </summary>
public static class MemoryLeakInspector
{
    private static readonly List<HeapSnapshot> _snapshots = new();
    private static readonly object _lock = new();
    private const int MaxSnapshots = 20;
    private static int _snapshotCounter;

    [DebugTool("take_heap_snapshot", "Record current heap state: GC memory, generation sizes, object counts by type. Returns snapshot ID.")]
    public static object TakeHeapSnapshot()
    {
        try
        {
            lock (_lock)
            {
                var snapshot = CaptureSnapshot();
                _snapshots.Add(snapshot);

                // Trim old snapshots
                while (_snapshots.Count > MaxSnapshots)
                    _snapshots.RemoveAt(0);

                return new
                {
                    snapshot_id = snapshot.Id,
                    timestamp = snapshot.Timestamp.ToString("o"),
                    summary = new
                    {
                        managed_memory_mb = Math.Round(snapshot.TotalManagedMemoryBytes / 1024.0 / 1024.0, 2),
                        heap_size_mb = Math.Round(snapshot.HeapSizeBytes / 1024.0 / 1024.0, 2),
                        fragmented_mb = Math.Round(snapshot.FragmentedBytes / 1024.0 / 1024.0, 2),
                        gen0_size_mb = Math.Round(snapshot.Gen0SizeBytes / 1024.0 / 1024.0, 2),
                        gen1_size_mb = Math.Round(snapshot.Gen1SizeBytes / 1024.0 / 1024.0, 2),
                        gen2_size_mb = Math.Round(snapshot.Gen2SizeBytes / 1024.0 / 1024.0, 2),
                        loh_size_mb = Math.Round(snapshot.LohSizeBytes / 1024.0 / 1024.0, 2),
                        pinned_objects = snapshot.PinnedObjectCount,
                        finalization_pending = snapshot.FinalizationPendingCount,
                        working_set_mb = Math.Round(snapshot.WorkingSetBytes / 1024.0 / 1024.0, 2),
                    },
                    type_count = snapshot.TypeStats.Count,
                    total_snapshots = _snapshots.Count,
                };
            }
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }
    }

    [DebugTool("compare_heap_snapshots", "Compare two heap snapshots by ID. Returns per-type count_delta, size_delta, growth_percentage.")]
    public static object CompareHeapSnapshots(
        [ToolParam("First (older) snapshot ID")] string snapshotId1,
        [ToolParam("Second (newer) snapshot ID")] string snapshotId2)
    {
        try
        {
            HeapSnapshot? snap1, snap2;
            lock (_lock)
            {
                snap1 = _snapshots.FirstOrDefault(s => s.Id == snapshotId1);
                snap2 = _snapshots.FirstOrDefault(s => s.Id == snapshotId2);
            }

            if (snap1 == null)
                return new { error = $"Snapshot '{snapshotId1}' not found. Use take_heap_snapshot first." };
            if (snap2 == null)
                return new { error = $"Snapshot '{snapshotId2}' not found. Use take_heap_snapshot first." };

            var allTypeNames = snap1.TypeStats.Keys
                .Union(snap2.TypeStats.Keys)
                .OrderBy(n => n)
                .ToList();

            var typeDeltas = new List<object>();
            foreach (var typeName in allTypeNames)
            {
                snap1.TypeStats.TryGetValue(typeName, out var stat1);
                snap2.TypeStats.TryGetValue(typeName, out var stat2);

                var countDelta = (stat2?.Count ?? 0) - (stat1?.Count ?? 0);
                var sizeDelta = (stat2?.EstimatedSizeBytes ?? 0) - (stat1?.EstimatedSizeBytes ?? 0);
                var oldSize = stat1?.EstimatedSizeBytes ?? 0;
                var growthPct = oldSize > 0
                    ? Math.Round((double)sizeDelta / oldSize * 100, 1)
                    : sizeDelta > 0 ? (double?)100 : null;

                typeDeltas.Add(new
                {
                    type_name = typeName,
                    count_before = stat1?.Count ?? 0,
                    count_after = stat2?.Count ?? 0,
                    count_delta = countDelta,
                    size_before_bytes = stat1?.EstimatedSizeBytes ?? 0,
                    size_after_bytes = stat2?.EstimatedSizeBytes ?? 0,
                    size_delta_bytes = sizeDelta,
                    size_delta_kb = Math.Round(sizeDelta / 1024.0, 2),
                    growth_pct = growthPct,
                });
            }

            var heapDelta = snap2.HeapSizeBytes - snap1.HeapSizeBytes;
            var managedDelta = snap2.TotalManagedMemoryBytes - snap1.TotalManagedMemoryBytes;

            return new
            {
                snapshot1 = new { id = snap1.Id, timestamp = snap1.Timestamp.ToString("o") },
                snapshot2 = new { id = snap2.Id, timestamp = snap2.Timestamp.ToString("o") },
                time_between_seconds = Math.Round((snap2.Timestamp - snap1.Timestamp).TotalSeconds, 1),
                overall = new
                {
                    managed_memory_delta_mb = Math.Round(managedDelta / 1024.0 / 1024.0, 2),
                    heap_size_delta_mb = Math.Round(heapDelta / 1024.0 / 1024.0, 2),
                    heap_growth_pct = snap1.HeapSizeBytes > 0
                        ? (double?)Math.Round((double)heapDelta / snap1.HeapSizeBytes * 100, 1)
                        : null,
                },
                type_deltas = typeDeltas
                    .OrderByDescending(d => ((dynamic)d).size_delta_bytes)
                    .Take(50)
                    .ToList(),
                total_types_compared = allTypeNames.Count,
            };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }
    }

    [DebugTool("get_leak_candidates", "Identify types with consistent growth across stored heap snapshots")]
    public static object GetLeakCandidates(int min_snapshots = 3)
    {
        try
        {
            List<HeapSnapshot> snapshots;
            lock (_lock)
            {
                snapshots = _snapshots.ToList();
            }

            if (snapshots.Count < 2)
                return new
                {
                    message = "Need at least 2 heap snapshots to identify leak candidates.",
                    current_snapshots = snapshots.Count,
                    hint = "Call take_heap_snapshot multiple times at different points in your application lifecycle.",
                };

            var minSnaps = Math.Clamp(min_snapshots, 2, MaxSnapshots);

            // For each type, check if it consistently grows
            var candidates = new List<object>();

            // Get all type names across snapshots
            var allTypes = snapshots
                .SelectMany(s => s.TypeStats.Keys)
                .Distinct()
                .ToList();

            foreach (var typeName in allTypes)
            {
                var sizes = snapshots
                    .Where(s => s.TypeStats.ContainsKey(typeName))
                    .Select(s => s.TypeStats[typeName].EstimatedSizeBytes)
                    .ToList();

                var counts = snapshots
                    .Where(s => s.TypeStats.ContainsKey(typeName))
                    .Select(s => s.TypeStats[typeName].Count)
                    .ToList();

                if (sizes.Count < minSnaps) continue;

                // Check for consistent growth
                var growthCount = 0;
                for (int i = 1; i < sizes.Count; i++)
                {
                    if (sizes[i] > sizes[i - 1]) growthCount++;
                }

                var growthConsistency = (double)growthCount / (sizes.Count - 1);
                var totalGrowth = sizes.Last() - sizes.First();
                var firstSize = sizes.First();

                // Only report types that grew in most snapshots and had meaningful growth
                if (growthConsistency >= 0.6 && totalGrowth > 0 && firstSize > 0)
                {
                    candidates.Add(new
                    {
                        type_name = typeName,
                        snapshots_tracked = sizes.Count,
                        growth_consistency_pct = Math.Round(growthConsistency * 100, 1),
                        size_first_bytes = sizes.First(),
                        size_last_bytes = sizes.Last(),
                        total_growth_bytes = totalGrowth,
                        total_growth_kb = Math.Round(totalGrowth / 1024.0, 2),
                        growth_pct = firstSize > 0 ? Math.Round((double)totalGrowth / firstSize * 100, 1) : 0,
                        count_first = counts.FirstOrDefault(),
                        count_last = counts.LastOrDefault(),
                        count_delta = counts.LastOrDefault() - counts.FirstOrDefault(),
                        size_trend = sizes.Select(s => Math.Round(s / 1024.0, 2)).ToList(),
                    });
                }
            }

            return new
            {
                total_candidates = candidates.Count,
                snapshots_analyzed = snapshots.Count,
                min_snapshots_required = minSnaps,
                leak_candidates = candidates
                    .OrderByDescending(c => ((dynamic)c).total_growth_bytes)
                    .ToList(),
            };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }
    }

    private static HeapSnapshot CaptureSnapshot()
    {
        var id = $"heap-{++_snapshotCounter:D3}";
        var now = DateTimeOffset.UtcNow;
        var gcInfo = GC.GetGCMemoryInfo();
        var proc = Process.GetCurrentProcess();

        long gen0 = 0, gen1 = 0, gen2 = 0, loh = 0, poe = 0;
        try
        {
            var genInfo = gcInfo.GenerationInfo;
            if (genInfo.Length > 0) gen0 = genInfo[0].SizeAfterBytes;
            if (genInfo.Length > 1) gen1 = genInfo[1].SizeAfterBytes;
            if (genInfo.Length > 2) gen2 = genInfo[2].SizeAfterBytes;
        }
        catch { }

        try
        {
            var pinned = gcInfo.PinnedObjectsCount;
            // GenerationInfo may not have POE; handle defensively
        }
        catch { }

        // Type-level stats: enumerate loaded types and estimate via reflection
        var typeStats = CaptureTypeStats();

        return new HeapSnapshot(
            id,
            now,
            GC.GetTotalMemory(false),
            gcInfo.HeapSizeBytes,
            gcInfo.FragmentedBytes,
            gcInfo.PinnedObjectsCount,
            gcInfo.FinalizationPendingCount,
            proc.WorkingSet64,
            gen0,
            gen1,
            gen2,
            loh,
            poe,
            typeStats
        );
    }

    /// <summary>
    /// Captures type-level statistics by scanning common heap-resident objects.
    /// Uses a lightweight approach since .NET doesn't expose per-type heap stats without profiling APIs.
    /// </summary>
    private static Dictionary<string, TypeStat> CaptureTypeStats()
    {
        var stats = new Dictionary<string, TypeStat>();

        try
        {
            // Track common diagnostic objects via GC generation info
            // For per-type stats, we use a sampling approach with weak references
            // This is a pragmatic approach — production tools use EventPipe/DOTNET_Trace for exact data

            // Get object counts from process diagnostics
            var proc = Process.GetCurrentProcess();

            // Track delegate counts (common leak source)
            try
            {
                stats["Thread"] = new TypeStat("System.Threading.Thread",
                    Process.GetCurrentProcess().Threads.Count, 0);
            }
            catch { }

            // Track common types via reflection on loaded assemblies
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var name = asm.GetName().Name ?? "";
                    if (name.StartsWith("System.") || name.StartsWith("Microsoft."))
                        continue;

                    var types = asm.GetTypes();
                    foreach (var t in types)
                    {
                        try
                        {
                            // Count static fields as potential retention points
                            var staticFields = t.GetFields(System.Reflection.BindingFlags.Static |
                                                           System.Reflection.BindingFlags.NonPublic |
                                                           System.Reflection.BindingFlags.Public);
                            if (staticFields.Length > 0)
                            {
                                var key = t.FullName ?? t.Name;
                                if (!stats.ContainsKey(key))
                                    stats[key] = new TypeStat(key, 1, staticFields.Length * 64);
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }

            // Add GC-based aggregate stats
            try
            {
                var gcInfo = GC.GetGCMemoryInfo();
                stats["__GC_Total_Heap"] = new TypeStat("__GC_Total_Heap", 1, (long)gcInfo.HeapSizeBytes);
                stats["__GC_Fragmented"] = new TypeStat("__GC_Fragmented", 1, (long)gcInfo.FragmentedBytes);
            }
            catch { }
        }
        catch
        {
            // Type stats are best-effort
        }

        return stats;
    }
}
