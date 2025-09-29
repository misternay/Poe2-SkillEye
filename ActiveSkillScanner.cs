namespace SkillEye
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using GameHelper.RemoteObjects.Components;
    using GameOffsets.Natives;
    using GameOffsets.Objects.Components;
    using GameOffsets.Objects.FilesStructures;

    public sealed class SkillEyeRow
    {
        public int Index { get; init; }
        public string Name { get; init; } = "";
        public ActiveSkillDetails Details { get; init; }
        public IntPtr EntryPtr { get; init; }
        public List<(string Label, decimal Value)> Metrics { get; init; } = new();
    }

    internal static class ActiveSkillScanner
    {
        private sealed class CacheEntry
        {
            public IntPtr ActorAddress;
            public IntPtr VectorFirst;
            public IntPtr VectorLast;
            public Dictionary<string, SkillEyeRow> BestRowsByName = new(StringComparer.OrdinalIgnoreCase);
        }

        private static readonly Dictionary<IntPtr, CacheEntry> _cacheByActorAddress = new();

        private static readonly Dictionary<string, int> _metricWeightsByLabel = new(StringComparer.OrdinalIgnoreCase)
        {
            ["DPS"] = 1000,
            ["APS"] = 600,
            ["AVG"] = 500,
            ["DoT/s"] = 400,
            ["Chaos DoT/s"] = 400,
            ["BaseAVG"] = 200,
            ["CooldownMs"] = 1,
        };

        /// <summary>
        ///     Return (and cache) best raw row per skill name. If <paramref name="interested"/> is non-null,
        ///     the result is filtered to those names. Set <paramref name="forceFull"/> to true when GH’s
        ///     own surface name set changed.
        /// </summary>
        public static Dictionary<string, SkillEyeRow> GetRowsCached(Actor actor,
                                                                    HashSet<string> interested,
                                                                    bool forceFull)
        {
            var actorAddress = GetActorAddress(actor);
            if (actorAddress == IntPtr.Zero)
                return new Dictionary<string, SkillEyeRow>(StringComparer.OrdinalIgnoreCase);

            // Read the vector header to see if it changed
            var actorOffsets = ProcReader.Read<ActorOffset>(actorAddress);
            var activeSkillsVector = actorOffsets.ActiveSkillsPtr;

            if (!forceFull &&
                _cacheByActorAddress.TryGetValue(actorAddress, out var cacheEntry) &&
                cacheEntry.VectorFirst == activeSkillsVector.First &&
                cacheEntry.VectorLast == activeSkillsVector.Last)
            {
                // Unchanged; return filtered view if needed
                if (interested is null || interested.Count == 0)
                    return cacheEntry.BestRowsByName;

                return cacheEntry.BestRowsByName
                    .Where(kv => interested.Contains(kv.Key))
                    .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
            }

            // Rebuild from memory (raw entries)
            var bestRowsByName = BuildBestRowsFromMemory(activeSkillsVector, out _);

            _cacheByActorAddress[actorAddress] = new CacheEntry
            {
                ActorAddress = actorAddress,
                VectorFirst = activeSkillsVector.First,
                VectorLast = activeSkillsVector.Last,
                BestRowsByName = bestRowsByName
            };

            if (interested is null || interested.Count == 0)
                return bestRowsByName;

            return bestRowsByName
                .Where(kv => interested.Contains(kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        }

        private static Dictionary<string, SkillEyeRow> BuildBestRowsFromMemory(StdVector activeSkillsVector, out int totalRows)
        {
            totalRows = 0;
            var bestRowsByName = new Dictionary<string, SkillEyeRow>(StringComparer.OrdinalIgnoreCase);

            ScanLayout<ActiveSkillPtrOnly>(
                activeSkillsVector,
                e => e.ActiveSkillPtr,
                indexBase: 0,
                bestByName: bestRowsByName,
                ref totalRows
            );

            ScanLayout<ActiveSkillStructure>(
                activeSkillsVector,
                e => e.ActiveSkillPtr,
                indexBase: 0,
                bestByName: bestRowsByName,
                ref totalRows
            );

            return bestRowsByName;
        }

        private static int ScanLayout<TElem>(StdVector vector,
                                             Func<TElem, IntPtr> getPtr,
                                             int indexBase,
                                             Dictionary<string, SkillEyeRow> bestByName,
                                             ref int totalRows)
            where TElem : unmanaged
        {
            int namedWithMetricsCount = 0;
            var elements = ProcReader.ReadStdVector<TElem>(vector);

            for (int i = 0; i < elements.Length; i++)
            {
                totalRows++;

                IntPtr entryPtr = getPtr(elements[i]);
                if (entryPtr == IntPtr.Zero) continue;

                var details = ProcReader.Read<ActiveSkillDetails>(entryPtr);

                // Name via GEPL chain (skip if no GEPL)
                string skillName = "";
                if (details.GrantedEffectsPerLevelDatRow != IntPtr.Zero)
                {
                    var keyPtr = details.GrantedEffectsPerLevelDatRow;

                    // name = Unicode( *(*key) )
                    IntPtr first = ProcReader.Read<IntPtr>(keyPtr);
                    IntPtr second = ProcReader.Read<IntPtr>(first);
                    skillName = ProcReader.ReadUnicodeString(second);

                    var gepl = ProcReader.Read<GrantedEffectsPerLevelDatOffset>(keyPtr);
                    var ge = ProcReader.Read<GrantedEffectsDatOffset>(gepl.GrantedEffectDatPtr);
                    details.ActiveSkillsDatPtr = ge.ActiveSkillDatPtr;
                }

                // Metrics only if we can find stats
                var statsPtr = ActiveSkillDetailsLayout.ReadStatsPtr(entryPtr);
                var metrics = CollectSkillMetrics(statsPtr);

                // Keep only NAMED rows WITH metrics
                if (string.IsNullOrWhiteSpace(skillName) || metrics.Count == 0)
                    continue;

                var row = new SkillEyeRow
                {
                    Index = indexBase + i,
                    Name = skillName,
                    Details = details,
                    EntryPtr = entryPtr,
                    Metrics = metrics
                };
                namedWithMetricsCount++;

                // Track "best" entry per name by metric score (then metrics count, then lowest index)
                var newScore = Score(row.Metrics, row.Index);
                if (!bestByName.TryGetValue(skillName, out var currentBest))
                {
                    bestByName[skillName] = row;
                }
                else
                {
                    var currentScore = Score(currentBest.Metrics, currentBest.Index);
                    if (newScore > currentScore ||
                        (newScore == currentScore && (row.Metrics.Count > currentBest.Metrics.Count ||
                                                      (row.Metrics.Count == currentBest.Metrics.Count && row.Index < currentBest.Index))))
                    {
                        bestByName[skillName] = row;
                    }
                }
            }

            return namedWithMetricsCount;
        }

        private static int Score(IReadOnlyList<(string Label, decimal Value)> metrics, int index)
        {
            int score = 0;
            for (int i = 0; i < metrics.Count; i++)
            {
                var label = metrics[i].Label;
                if (label != null && _metricWeightsByLabel.TryGetValue(label, out var weight))
                    score += weight;
                else
                    score += 3; // small credit for unknowns
            }
            return score * 100 + metrics.Count * 2 - index;
        }

        private static IntPtr GetActorAddress(Actor actor)
        {
            var property = typeof(Actor).GetProperty("Address", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return property is null ? IntPtr.Zero : (IntPtr)property.GetValue(actor);
        }

        /// <summary>
        ///     Reads the current StatPairs for the best raw row of <paramref name="skillName"/> 
        ///     and returns  fresh metrics.
        /// </summary>
        public static bool TryGetLiveMetrics(Actor actor, string skillName, out List<(string Label, decimal Value)> metrics)
        {
            metrics = null;

            var rows = GetRowsCached(actor, null, forceFull: false);
            if (!rows.TryGetValue(skillName, out var row) || row.EntryPtr == IntPtr.Zero)
                return false;

            var statsPtr = ActiveSkillDetailsLayout.ReadStatsPtr(row.EntryPtr);
            metrics = CollectSkillMetrics(statsPtr);
            return metrics is { Count: > 0 };
        }

        /// <summary>
        ///     Returns the cached raw entry pointer for a skill, if available (useful for custom reads).
        /// </summary>
        public static bool TryGetEntryPtr(Actor actor, string skillName, out IntPtr entryPtr)
        {
            entryPtr = IntPtr.Zero;
            var rows = GetRowsCached(actor, null, forceFull: false);
            if (!rows.TryGetValue(skillName, out var row)) return false;
            entryPtr = row.EntryPtr;
            return entryPtr != IntPtr.Zero;
        }

        private static class StatIds
        {
            public const int DisplaySkillCooldownTimeMs = 20918;
            public const int HundredTimesDamagePerSecond = 686;
            public const int HundredTimesAttacksPerSecond = 685;
            public const int HundredTimesAverageDamagePerHit = 1977;
            public const int IntermediaryFireSkillDotAreaDamagePerMinute = 7585;
            public const int IntermediaryChaosSkillDotDamagePerMinute = 7596;
            public const int BaseSkillShowAverageInsteadOfDps = 1979;
        }

        private static List<(string Label, decimal Value)> CollectSkillMetrics(IntPtr statsPtr)
        {
            var result = new List<(string, decimal)>(6);
            if (statsPtr == IntPtr.Zero) return result;

            var subOffsets = ProcReader.Read<SubStatsComponentOffsets>(statsPtr);
            var statPairs = ProcReader.ReadStdVector<StatPair>(subOffsets.Stats);

            int? cooldownMs = null;
            int? damagePerSecondHundred = null;
            int? attacksPerSecondHundred = null;
            int? averageDamagePerHitHundred = null;
            int? dotFirePerMinute = null;
            int? dotChaosPerMinute = null;
            int? baseAverageHundred = null;

            for (int i = 0; i < statPairs.Length; i++)
            {
                var statId = statPairs[i].StatId;
                var value = statPairs[i].Value;

                if (statId == StatIds.DisplaySkillCooldownTimeMs) cooldownMs = value;
                else if (statId == StatIds.HundredTimesDamagePerSecond) damagePerSecondHundred = value;
                else if (statId == StatIds.HundredTimesAttacksPerSecond) attacksPerSecondHundred = value;
                else if (statId == StatIds.HundredTimesAverageDamagePerHit) averageDamagePerHitHundred = value;
                else if (statId == StatIds.IntermediaryFireSkillDotAreaDamagePerMinute) dotFirePerMinute = value;
                else if (statId == StatIds.IntermediaryChaosSkillDotDamagePerMinute) dotChaosPerMinute = value;
                else if (statId == StatIds.BaseSkillShowAverageInsteadOfDps) baseAverageHundred = value;
            }

            if (cooldownMs.HasValue && cooldownMs.Value > 0) result.Add(("CooldownMs", cooldownMs.Value));
            if (damagePerSecondHundred.HasValue) result.Add(("DPS", damagePerSecondHundred.Value / 100m));
            if (attacksPerSecondHundred.HasValue) result.Add(("APS", attacksPerSecondHundred.Value / 100m));
            if (averageDamagePerHitHundred.HasValue) result.Add(("AVG", averageDamagePerHitHundred.Value / 100m));
            if (dotFirePerMinute.HasValue) result.Add(("DoT/s", dotFirePerMinute.Value / 60m));
            if (dotChaosPerMinute.HasValue) result.Add(("Chaos DoT/s", dotChaosPerMinute.Value / 60m));
            if (baseAverageHundred.HasValue) result.Add(("BaseAVG", baseAverageHundred.Value / 100m));

            return result;
        }
    }
}