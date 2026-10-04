using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Numbers;
using RimWorld;
using UnityEngine;
using Verse;

namespace NumbersPerformanceFix
{
    [StaticConstructorOnStartup]
    public static class Patcher
    {
        private static readonly Harmony harmony = new("ifchen0.numbersperformancefix");
        private static bool applied;

        // Harmony runs the declaring type's static constructor when patching, and
        // MainTabWindow_Numbers' static constructor needs Find.World. A failed static constructor
        // breaks the type for the whole session, so the Numbers patches are applied lazily,
        // the first time a Numbers window has been constructed.
        static Patcher()
        {
            harmony.Patch(AccessTools.Method(typeof(MainTabWindow_PawnTable), nameof(MainTabWindow_PawnTable.PostOpen)),
                postfix: new HarmonyMethod(typeof(Patcher), nameof(PostOpenPostfix)));
        }

        public static void PostOpenPostfix(MainTabWindow_PawnTable __instance)
        {
            if (applied || __instance is not MainTabWindow_Numbers)
                return;
            applied = true;
            try
            {
                ApplyNumbersPatches();
            }
            catch (Exception e)
            {
                Log.Error($"[NumbersPerformanceFix] Failed to patch Numbers: {e}");
            }
        }

        private static void ApplyNumbersPatches()
        {
            // Window header: build drop-down menus only when a button can actually be clicked,
            // and throttle the "Count: N" label.
            harmony.Patch(AccessTools.Method(typeof(MainTabWindow_Numbers), nameof(MainTabWindow_Numbers.DoWindowContents)),
                transpiler: new HarmonyMethod(typeof(HeaderPatch), nameof(HeaderPatch.Transpiler)));

            // Text cells: short-lived cache of GetTextFor for every Numbers text column.
            var textPrefix = new HarmonyMethod(typeof(TextCache), nameof(TextCache.Prefix));
            var textFinalizer = new HarmonyMethod(typeof(TextCache), nameof(TextCache.Finalizer));
            int textPatched = 0;
            foreach (Type type in typeof(MainTabWindow_Numbers).Assembly.GetTypes())
            {
                if (!typeof(PawnColumnWorker_Text).IsAssignableFrom(type))
                    continue;
                MethodInfo m = AccessTools.DeclaredMethod(type, "GetTextFor", [typeof(Pawn)]);
                if (m == null || m.IsAbstract)
                    continue;
                harmony.Patch(m, prefix: textPrefix, finalizer: textFinalizer);
                textPatched++;
            }

            // Sorting by a stat column: evaluate each pawn's stat once per sort, not once per comparison.
            harmony.Patch(AccessTools.DeclaredMethod(typeof(PawnColumnWorker_Stat), nameof(PawnColumnWorker.Compare)),
                prefix: new HarmonyMethod(typeof(StatCompareCache), nameof(StatCompareCache.Prefix)));

            // Tooltips built eagerly for every cell: skip them unless the mouse is over the cell.
            var cellPrefix = new HarmonyMethod(typeof(TipPatch), nameof(TipPatch.DoCellPrefix));
            var tipPrefix = new HarmonyMethod(typeof(TipPatch), nameof(TipPatch.GetTipPrefix));
            foreach (Type type in new[] { typeof(PawnColumnWorker_Skill), typeof(PawnColumnWorker_DiseaseProgression) })
            {
                harmony.Patch(AccessTools.DeclaredMethod(type, nameof(PawnColumnWorker.DoCell)), prefix: cellPrefix);
                harmony.Patch(AccessTools.DeclaredMethod(type, "GetTip"), prefix: tipPrefix);
            }

            Log.Message($"[NumbersPerformanceFix] Patched Numbers window header, {textPatched} text columns, 2 tooltip columns.");
        }
    }

    /// <summary>
    /// Shared invalidation: true once per frame (on Repaint, after all input events of the frame
    /// have run) when the interval has elapsed or the player clicked / pressed a key.
    /// </summary>
    internal sealed class Refresher(float interval)
    {
        private float expireAt;
        private int checkedFrame = -1;

        public bool ShouldRefresh()
        {
            if (Event.current == null || Event.current.type != EventType.Repaint)
                return false;
            int frame = Time.frameCount;
            if (frame == checkedFrame)
                return false;
            checkedFrame = frame;

            float now = Time.realtimeSinceStartup;
            if (now < expireAt && !Input.GetMouseButtonUp(0) && !Input.GetMouseButtonUp(1) && !Input.anyKeyDown)
                return false;
            expireAt = now + interval;
            return true;
        }
    }

    /// <summary>
    /// Caches GetTextFor per (column, pawn). Stale entries keep showing their last value and are
    /// recomputed within a per-frame time budget, so expensive stats (e.g. melee DPS) are spread
    /// over several frames instead of all being recomputed in one frame.
    /// </summary>
    public static class TextCache
    {
        private const float Interval = 0.25f;
        private const double FrameBudgetMs = 1.0;
        private const int MaxEntries = 5000;

        private struct Entry
        {
            public string text;
            public float time;
            public int generation;
        }

        private static readonly Dictionary<(PawnColumnWorker, Pawn), Entry> cache = [];
        // Only used to detect clicks / key presses; the interval itself is tracked per entry.
        private static readonly Refresher clickRefresher = new(float.MaxValue);
        private static readonly long budgetTicks = (long)(FrameBudgetMs * Stopwatch.Frequency / 1000.0);

        private static int generation;
        private static int frame = -1;
        private static long spentTicks;
        private static int depth;

        private const long Hit = -1;
        private const long Nested = -2;

        public static bool Prefix(PawnColumnWorker __instance, Pawn __0, ref string __result, out long __state)
        {
            if (depth > 0)
            {
                __state = Nested;
                return true;
            }

            if (clickRefresher.ShouldRefresh())
                generation++;

            int f = Time.frameCount;
            if (f != frame)
            {
                frame = f;
                spentTicks = 0;
            }

            if (cache.TryGetValue((__instance, __0), out Entry entry))
            {
                bool fresh = entry.generation == generation && Time.realtimeSinceStartup - entry.time < Interval;
                if (fresh || spentTicks >= budgetTicks)
                {
                    __result = entry.text;
                    __state = Hit;
                    return false;
                }
            }

            depth++;
            __state = Stopwatch.GetTimestamp();
            return true;
        }

        public static Exception Finalizer(PawnColumnWorker __instance, Pawn __0, string __result, long __state, Exception __exception)
        {
            if (__state >= 0)
            {
                depth--;
                spentTicks += Stopwatch.GetTimestamp() - __state;
                if (__exception == null)
                {
                    if (cache.Count >= MaxEntries)
                        cache.Clear();
                    cache[(__instance, __0)] = new Entry { text = __result, time = Time.realtimeSinceStartup, generation = generation };
                }
            }
            return __exception;
        }
    }

    public static class HeaderPatch
    {
        private const float CountInterval = 0.5f;

        private static readonly List<FloatMenuOption> Empty = [];
        private static List<FloatMenuOption> placeholder;

        private static int lastCount = -1;
        private static object lastFilter;
        private static Map lastMap;
        private static readonly Refresher countRefresher = new(CountInterval);

        // Buttons can only fire on input events; Layout/Repaint only draw them.
        private static bool DrawOnly
            => Event.current != null && (Event.current.type == EventType.Layout || Event.current.type == EventType.Repaint);

        // DoButton never reads the list unless clicked; it only has to be non-empty where the original checks Count.
        private static List<FloatMenuOption> Placeholder
            => placeholder ??= [new FloatMenuOption("-", null)];

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo genericDef = AccessTools.Method(typeof(OptionsMaker), nameof(OptionsMaker.OptionsMakerForGenericDef));
            MethodInfo forLabel = AccessTools.Method(typeof(OptionsMaker), nameof(OptionsMaker.FloatMenuOptionsFor),
                [typeof(IEnumerable<PawnColumnDef>), typeof(Func<PawnColumnDef, string>)]);
            MethodInfo forColumns = AccessTools.Method(typeof(OptionsMaker), nameof(OptionsMaker.FloatMenuOptionsFor),
                [typeof(IEnumerable<PawnColumnDef>)]);
            MethodInfo other = AccessTools.Method(typeof(OptionsMaker), nameof(OptionsMaker.OtherOptionsMaker));
            MethodInfo selector = AccessTools.Method(typeof(OptionsMaker), nameof(OptionsMaker.PawnSelector));
            MethodInfo countPawns = typeof(Enumerable).GetMethods()
                .First(m => m.Name == nameof(Enumerable.Count) && m.GetParameters().Length == 1)
                .MakeGenericMethod(typeof(Pawn));

            var replacements = new Dictionary<MethodInfo, MethodInfo>
            {
                [forLabel] = AccessTools.Method(typeof(HeaderPatch), nameof(ForLabel)),
                [forColumns] = AccessTools.Method(typeof(HeaderPatch), nameof(ForColumns)),
                [other] = AccessTools.Method(typeof(HeaderPatch), nameof(Other)),
                [selector] = AccessTools.Method(typeof(HeaderPatch), nameof(Selector)),
                [countPawns] = AccessTools.Method(typeof(HeaderPatch), nameof(CountPawns)),
            };
            MethodInfo genericHelper = AccessTools.Method(typeof(HeaderPatch), nameof(GenericDef));

            int replaced = 0;
            foreach (CodeInstruction ins in instructions)
            {
                if (ins.operand is MethodInfo mi)
                {
                    if (replacements.TryGetValue(mi, out MethodInfo helper))
                    {
                        ins.opcode = OpCodes.Call;
                        ins.operand = helper;
                        replaced++;
                    }
                    else if (mi.IsGenericMethod && mi.GetGenericMethodDefinition() == genericDef)
                    {
                        ins.opcode = OpCodes.Call;
                        ins.operand = genericHelper.MakeGenericMethod(mi.GetGenericArguments());
                        replaced++;
                    }
                }
                yield return ins;
            }

            if (replaced == 0)
                Log.Warning("[NumbersPerformanceFix] No calls replaced in MainTabWindow_Numbers.DoWindowContents; Numbers may have changed.");
        }

        public static List<FloatMenuOption> GenericDef<T>(OptionsMaker maker, IEnumerable<T> defs) where T : Def
        {
            if (!DrawOnly)
                return maker.OptionsMakerForGenericDef(defs);
            // The Abilities button is only drawn when the list is non-empty.
            if (typeof(T) == typeof(AbilityDef))
                return DefDatabase<AbilityDef>.DefCount > 0 ? Placeholder : Empty;
            return Placeholder;
        }

        public static List<FloatMenuOption> ForLabel(OptionsMaker maker, IEnumerable<PawnColumnDef> pcdList, Func<PawnColumnDef, string> labelOverride)
            => DrawOnly ? Placeholder : maker.FloatMenuOptionsFor(pcdList, labelOverride);

        public static List<FloatMenuOption> ForColumns(OptionsMaker maker, IEnumerable<PawnColumnDef> pcdList)
            => DrawOnly ? Placeholder : maker.FloatMenuOptionsFor(pcdList);

        public static List<FloatMenuOption> Other(OptionsMaker maker)
            => DrawOnly ? Placeholder : maker.OtherOptionsMaker();

        public static List<FloatMenuOption> Selector(OptionsMaker maker)
            => DrawOnly ? Placeholder : maker.PawnSelector();

        public static int CountPawns(IEnumerable<Pawn> pawns)
        {
            object filter = MainTabWindow_Numbers.filterValidator.FirstOrDefault();
            Map map = Find.CurrentMap;
            bool refresh = countRefresher.ShouldRefresh();
            if (lastCount < 0 || refresh || filter != lastFilter || map != lastMap)
            {
                lastCount = pawns.Count();
                lastFilter = filter;
                lastMap = map;
            }
            return lastCount;
        }
    }

    /// <summary>
    /// PawnColumnWorker_Stat.Compare computes the stat for both pawns on every comparison, so a
    /// sort costs O(n log n) stat evaluations. Memoize the value per pawn for the current frame.
    /// </summary>
    public static class StatCompareCache
    {
        private static readonly Dictionary<(PawnColumnWorker, Pawn), float> values = [];
        private static int frame = -1;

        private static float ValueFor(PawnColumnWorker worker, StatDef stat, Pawn pawn)
        {
            if (values.TryGetValue((worker, pawn), out float v))
                return v;
            // Same expression as the original Compare.
            v = stat.Worker.IsDisabledFor(pawn) ? 0f : pawn.GetStatValue(stat);
            values[(worker, pawn)] = v;
            return v;
        }

        public static bool Prefix(PawnColumnWorker_Stat __instance, Pawn a, Pawn b, ref int __result)
        {
            int f = Time.frameCount;
            if (f != frame)
            {
                frame = f;
                values.Clear();
            }

            StatDef stat = __instance.def.Ext().stat;
            __result = ValueFor(__instance, stat, a).CompareTo(ValueFor(__instance, stat, b));
            return false;
        }
    }

    public static class TipPatch
    {
        private static Rect cellRect;

        public static void DoCellPrefix(Rect rect) => cellRect = rect;

        // GetTip is only used for TooltipHandler.TipRegion inside the same cell.
        public static bool GetTipPrefix(ref string __result)
        {
            if (Event.current != null && cellRect.Contains(Event.current.mousePosition))
                return true;
            __result = null;
            return false;
        }
    }
}
