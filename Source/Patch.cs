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

            // Text and icon cells: short-lived cache of GetTextFor / GetIconFor. Covers every mod's
            // columns, but only serves cached values while a Numbers window is being drawn.
            var textPrefix = new HarmonyMethod(typeof(TextCache), nameof(TextCache.Prefix));
            var textFinalizer = new HarmonyMethod(typeof(TextCache), nameof(TextCache.Finalizer));
            int textPatched = 0;
            foreach (Type type in typeof(PawnColumnWorker_Text).AllSubclasses())
            {
                MethodInfo m = AccessTools.DeclaredMethod(type, "GetTextFor", [typeof(Pawn)]);
                if (m == null || m.IsAbstract)
                    continue;
                harmony.Patch(m, prefix: textPrefix, finalizer: textFinalizer);
                textPatched++;
            }

            var iconPrefix = new HarmonyMethod(typeof(IconCache), nameof(IconCache.Prefix));
            var iconFinalizer = new HarmonyMethod(typeof(IconCache), nameof(IconCache.Finalizer));
            int iconPatched = 0;
            foreach (Type type in typeof(PawnColumnWorker_Icon).AllSubclasses())
            {
                // Clickable / paintable icons (e.g. drop all) must reflect the click immediately.
                if (IsInteractiveIcon(type))
                    continue;
                MethodInfo m = AccessTools.DeclaredMethod(type, "GetIconFor", [typeof(Pawn)]);
                if (m == null || m.IsAbstract)
                    continue;
                harmony.Patch(m, prefix: iconPrefix, finalizer: iconFinalizer);
                iconPatched++;
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

            // Gear / inventory: the item list stays live, only the per-item tooltip label is built lazily.
            var labelTranspiler = new HarmonyMethod(typeof(TipPatch), nameof(TipPatch.LazyThingLabelTranspiler));
            foreach (Type type in new[] { typeof(PawnColumnWorker_Equipment), typeof(PawnColumnWorker_Inventory) })
                harmony.Patch(AccessTools.DeclaredMethod(type, "DrawThing"), transpiler: labelTranspiler);

            // Need bars: cache change arrow and instant level.
            harmony.Patch(AccessTools.DeclaredMethod(typeof(PawnColumnWorker_Need), nameof(PawnColumnWorker.DoCell)),
                transpiler: new HarmonyMethod(typeof(NeedCache), nameof(NeedCache.Transpiler)));

            // Prisoner interaction: non-exclusive modes (hemogen farm, bloodfeed only) are checkboxes, as in vanilla.
            harmony.Patch(AccessTools.DeclaredMethod(typeof(PawnColumnWorker_PrisonerInteraction), "DrawInteractionRadioButton"),
                prefix: new HarmonyMethod(typeof(PrisonerInteractionPatch), nameof(PrisonerInteractionPatch.Prefix)));

            // Dev mode only: per-column cell cost, logged periodically while a Numbers table is open.
            harmony.Patch(AccessTools.Method(typeof(PawnTable), nameof(PawnTable.PawnTableOnGUI)),
                transpiler: new HarmonyMethod(typeof(CellProfiler), nameof(CellProfiler.Transpiler)));

            Log.Message($"[NumbersPerformanceFix] Patched Numbers window header, {textPatched} text columns, {iconPatched} icon columns, 4 tooltip columns.");
        }

        private static bool IsInteractiveIcon(Type type)
        {
            for (Type t = type; t != null && t != typeof(PawnColumnWorker_Icon); t = t.BaseType)
            {
                if (AccessTools.DeclaredMethod(t, "ClickedIcon") != null || AccessTools.DeclaredMethod(t, "PaintedIcon") != null)
                    return true;
            }
            return false;
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
    /// Shared state of the cell caches. Stale entries keep showing their last value and are
    /// recomputed within a per-frame time budget, so expensive values (e.g. melee DPS) are spread
    /// over several frames instead of all being recomputed in one frame. A click or key press
    /// marks every entry stale.
    /// </summary>
    internal static class CacheClock
    {
        public const float Interval = 0.5f;
        public const int MaxEntries = 5000;
        private const double FrameBudgetMs = 1.0;
        // A single refresh can cost several ms (e.g. melee DPS); the excess is paid back over the
        // following frames, so the average stays within the budget. Capped so one very slow value
        // cannot stall all refreshes for long.
        private const double MaxDebtMs = 20.0;
        // Entries invalidated by a click are refreshed before ones that merely aged out.
        private const float ClickPriority = 1000f;

        // Only used to detect clicks / key presses; the interval itself is tracked per entry.
        private static readonly Refresher clickRefresher = new(float.MaxValue);
        private static readonly long budgetTicks = (long)(FrameBudgetMs * Stopwatch.Frequency / 1000.0);
        private static readonly long maxDebtTicks = (long)(MaxDebtMs * Stopwatch.Frequency / 1000.0);

        public static int generation;
        private static int frame = -1;
        private static long spentTicks;
        private static int depth;
        // Oldest-first: only entries at least about as old as the ones denied last frame are refreshed.
        private static float minRefreshAge;
        private static float maxDeniedAge;

        public const long Hit = -1;
        public const long Bypass = -2;

        /// <summary>False for nested calls and outside Numbers windows; the original then runs uncached.</summary>
        public static bool Active()
        {
            if (depth > 0 || Find.WindowStack?.currentlyDrawnWindow is not MainTabWindow_Numbers)
                return false;

            if (clickRefresher.ShouldRefresh())
                generation++;

            int f = Time.frameCount;
            if (f != frame)
            {
                frame = f;
                spentTicks = Math.Min(Math.Max(spentTicks - budgetTicks, 0), maxDebtTicks);
                minRefreshAge = maxDeniedAge * 0.75f;
                maxDeniedAge = 0f;
            }
            return true;
        }

        /// <summary>
        /// Fresh while no click happened and either the game has not ticked (paused) or the
        /// interval has not elapsed.
        /// </summary>
        public static bool IsFresh<T>(in CacheEntry<T> e)
            => e.generation == generation && (e.tick == Find.TickManager.TicksGame || Time.realtimeSinceStartup - e.time < Interval);

        /// <summary>True when the cached value should be served instead of recomputing it now.</summary>
        public static bool CanServe<T>(in CacheEntry<T> e)
        {
            if (IsFresh(e))
                return true;
            float age = Time.realtimeSinceStartup - e.time + (e.generation != generation ? ClickPriority : 0f);
            if (spentTicks < budgetTicks && age >= minRefreshAge)
                return false;
            if (age > maxDeniedAge)
                maxDeniedAge = age;
            return true;
        }

        public static CacheEntry<T> Stamp<T>(T value)
            => new() { value = value, time = Time.realtimeSinceStartup, tick = Find.TickManager.TicksGame, generation = generation };

        public static long Begin()
        {
            depth++;
            return Stopwatch.GetTimestamp();
        }

        public static void End(long start)
        {
            depth--;
            spentTicks += Stopwatch.GetTimestamp() - start;
        }
    }

    internal struct CacheEntry<T>
    {
        public T value;
        public float time;
        public int tick;
        public int generation;
    }

    /// <summary>Caches GetTextFor per (column, pawn).</summary>
    public static class TextCache
    {
        private static readonly Dictionary<(PawnColumnWorker, Pawn), CacheEntry<string>> cache = [];

        public static bool Prefix(PawnColumnWorker __instance, Pawn __0, ref string __result, out long __state)
        {
            if (!CacheClock.Active())
            {
                __state = CacheClock.Bypass;
                return true;
            }
            if (cache.TryGetValue((__instance, __0), out var entry) && CacheClock.CanServe(entry))
            {
                __result = entry.value;
                __state = CacheClock.Hit;
                return false;
            }
            __state = CacheClock.Begin();
            return true;
        }

        public static Exception Finalizer(PawnColumnWorker __instance, Pawn __0, string __result, long __state, Exception __exception)
        {
            if (__state >= 0)
            {
                CacheClock.End(__state);
                if (__exception == null)
                {
                    if (cache.Count >= CacheClock.MaxEntries)
                        cache.Clear();
                    cache[(__instance, __0)] = CacheClock.Stamp(__result);
                }
            }
            return __exception;
        }
    }

    /// <summary>Caches GetIconFor per (column, pawn) for non-interactive icon columns.</summary>
    public static class IconCache
    {
        private static readonly Dictionary<(PawnColumnWorker, Pawn), CacheEntry<Texture2D>> cache = [];

        public static bool Prefix(PawnColumnWorker __instance, Pawn __0, ref Texture2D __result, out long __state)
        {
            if (!CacheClock.Active())
            {
                __state = CacheClock.Bypass;
                return true;
            }
            if (cache.TryGetValue((__instance, __0), out var entry) && CacheClock.CanServe(entry))
            {
                __result = entry.value;
                __state = CacheClock.Hit;
                return false;
            }
            __state = CacheClock.Begin();
            return true;
        }

        public static Exception Finalizer(PawnColumnWorker __instance, Pawn __0, Texture2D __result, long __state, Exception __exception)
        {
            if (__state >= 0)
            {
                CacheClock.End(__state);
                if (__exception == null)
                {
                    if (cache.Count >= CacheClock.MaxEntries)
                        cache.Clear();
                    cache[(__instance, __0)] = CacheClock.Stamp(__result);
                }
            }
            return __exception;
        }
    }

    /// <summary>
    /// Need bars: the change arrow (e.g. food fall rate) and instant level are cached; the bar
    /// level itself stays live.
    /// </summary>
    public static class NeedCache
    {
        private static readonly Dictionary<Need, CacheEntry<(int arrow, float instant)>> cache = [];

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var replacements = new Dictionary<MethodInfo, MethodInfo>
            {
                [AccessTools.PropertyGetter(typeof(Need), nameof(Need.GUIChangeArrow))] = AccessTools.Method(typeof(NeedCache), nameof(Arrow)),
                [AccessTools.PropertyGetter(typeof(Need), nameof(Need.CurInstantLevelPercentage))] = AccessTools.Method(typeof(NeedCache), nameof(Instant)),
            };
            int replaced = 0;
            foreach (CodeInstruction ins in instructions)
            {
                if (ins.operand is MethodInfo mi && replacements.TryGetValue(mi, out MethodInfo helper))
                {
                    ins.opcode = OpCodes.Call;
                    ins.operand = helper;
                    replaced++;
                }
                yield return ins;
            }
            if (replaced == 0)
                Log.Warning("[NumbersPerformanceFix] Need bar values not found; Numbers may have changed.");
        }

        public static int Arrow(Need need) => Get(need).arrow;

        public static float Instant(Need need) => Get(need).instant;

        private static (int arrow, float instant) Get(Need need)
        {
            if (!CacheClock.Active())
                return (need.GUIChangeArrow, need.CurInstantLevelPercentage);
            if (cache.TryGetValue(need, out var entry) && CacheClock.IsFresh(entry))
                return entry.value;
            var value = (need.GUIChangeArrow, need.CurInstantLevelPercentage);
            if (cache.Count >= CacheClock.MaxEntries)
                cache.Clear();
            cache[need] = CacheClock.Stamp(value);
            return value;
        }
    }

    /// <summary>
    /// Numbers draws every prisoner interaction mode as a radio button and calls
    /// SetExclusiveInteraction, which vanilla rejects for non-exclusive modes (hemogen farm,
    /// bloodfeed only). Draw those as paintable checkboxes and toggle them like ITab_Pawn_Visitor.
    /// </summary>
    public static class PrisonerInteractionPatch
    {
        public static bool Prefix(Rect rect, Pawn pawn, PrisonerInteractionModeDef prisonerInteraction)
        {
            if (!prisonerInteraction.isNonExclusiveInteraction)
                return true;

            bool enabled = pawn.guest.IsInteractionEnabled(prisonerInteraction);
            bool checkOn = enabled;
            Widgets.Checkbox(rect.x, rect.y, ref checkOn, paintable: true);
            if (checkOn != enabled)
            {
                pawn.guest.ToggleNonExclusiveInteraction(prisonerInteraction, checkOn);
                NonExclusiveInteractionToggled(pawn, prisonerInteraction, checkOn);
            }
            return false;
        }

        // Same as ITab_Pawn_Visitor.NonExclusiveInteractionToggled, for an arbitrary pawn.
        private static void NonExclusiveInteractionToggled(Pawn pawn, PrisonerInteractionModeDef mode, bool enabled)
        {
            if (!ModsConfig.BiotechActive || mode != PrisonerInteractionModeDefOf.HemogenFarm)
                return;
            Bill bill = pawn.BillStack?.Bills?.FirstOrDefault(x => x.recipe == RecipeDefOf.ExtractHemogenPack);
            if (enabled)
            {
                if (bill == null && SanguophageUtility.CanSafelyBeQueuedForHemogenExtraction(pawn))
                    HealthCardUtility.CreateSurgeryBill(pawn, RecipeDefOf.ExtractHemogenPack, null);
            }
            else if (bill != null)
            {
                pawn.BillStack.Bills.Remove(bill);
            }
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

        /// <summary>
        /// DrawThing(Rect rect, Thing thing, Pawn) ends with TipRegion(rect, new TipSignal(thing.LabelCap)).
        /// Replace the label with one that is only built when the mouse is over the item.
        /// </summary>
        public static IEnumerable<CodeInstruction> LazyThingLabelTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo labelCap = AccessTools.PropertyGetter(typeof(Entity), nameof(Entity.LabelCap));
            ConstructorInfo tipCtor = AccessTools.Constructor(typeof(TipSignal), [typeof(string)]);
            MethodInfo helper = AccessTools.Method(typeof(TipPatch), nameof(LabelIfHovered));

            List<CodeInstruction> codes = [.. instructions];
            int replaced = 0;
            for (int i = 0; i < codes.Count - 1; i++)
            {
                if (codes[i].Calls(labelCap) && codes[i + 1].opcode == OpCodes.Newobj && Equals(codes[i + 1].operand, tipCtor))
                {
                    // Stack holds the thing; push rect (arg 1) and call LabelIfHovered(thing, rect).
                    codes[i] = new CodeInstruction(OpCodes.Call, helper).MoveLabelsFrom(codes[i]);
                    codes.Insert(i, new CodeInstruction(OpCodes.Ldarg_1));
                    replaced++;
                    i++;
                }
            }
            if (replaced == 0)
                Log.Warning("[NumbersPerformanceFix] Gear/inventory tooltip label not found; Numbers may have changed.");
            return codes;
        }

        // TipRegion ignores an empty text, and anything outside Repaint or outside the rect anyway.
        public static string LabelIfHovered(Thing thing, Rect rect)
            => Event.current.type == EventType.Repaint && Mouse.IsOver(rect) ? thing.LabelCap : "";
    }

    /// <summary>
    /// Dev mode only: measures DoCell per column on Repaint in Numbers tables and logs the most
    /// expensive columns every few seconds, to find what is still worth optimizing.
    /// </summary>
    public static class CellProfiler
    {
        private const float LogInterval = 10f;
        private const int TopCount = 10;

        private static readonly Dictionary<PawnColumnDef, long> ticks = [];
        private static long totalTicks;
        private static int lastFrame = -1;
        private static int frames;
        private static float nextLog;

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo doCell = AccessTools.Method(typeof(PawnColumnWorker), nameof(PawnColumnWorker.DoCell));
            MethodInfo wrapper = AccessTools.Method(typeof(CellProfiler), nameof(DoCell));
            int replaced = 0;
            foreach (CodeInstruction ins in instructions)
            {
                if (ins.Calls(doCell))
                {
                    ins.opcode = OpCodes.Call;
                    ins.operand = wrapper;
                    replaced++;
                }
                yield return ins;
            }
            if (replaced == 0)
                Log.Warning("[NumbersPerformanceFix] PawnTable DoCell call not found; cell profiler disabled.");
        }

        public static void DoCell(PawnColumnWorker worker, Rect rect, Pawn pawn, PawnTable table)
        {
            if (!Prefs.DevMode || table is not PawnTable_NumbersMain || Event.current.type != EventType.Repaint)
            {
                worker.DoCell(rect, pawn, table);
                return;
            }

            long start = Stopwatch.GetTimestamp();
            try
            {
                worker.DoCell(rect, pawn, table);
            }
            finally
            {
                Record(worker.def, Stopwatch.GetTimestamp() - start);
            }
        }

        private static void Record(PawnColumnDef def, long elapsed)
        {
            int f = Time.frameCount;
            if (f != lastFrame)
            {
                lastFrame = f;
                float now = Time.realtimeSinceStartup;
                if (frames > 0 && now >= nextLog)
                    Flush();
                if (frames == 0)
                    nextLog = now + LogInterval;
                frames++;
            }
            ticks.TryGetValue(def, out long t);
            ticks[def] = t + elapsed;
            totalTicks += elapsed;
        }

        private static void Flush()
        {
            double MsPerFrame(long t) => t * 1000.0 / Stopwatch.Frequency / frames;
            string top = string.Join(", ", ticks.OrderByDescending(kv => kv.Value).Take(TopCount)
                .Select(kv => $"{kv.Key.defName} {MsPerFrame(kv.Value):0.000}"));
            Log.Message($"[NumbersPerformanceFix] cells {MsPerFrame(totalTicks):0.000} ms/frame over {frames} frames; top: {top}");
            ticks.Clear();
            totalTicks = 0;
            frames = 0;
        }
    }
}
