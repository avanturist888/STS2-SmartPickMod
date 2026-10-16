using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FirstMod;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;

using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Rewards;

namespace FirstMod.Patches;

/// <summary>
/// Patches the card reward screen to export offered cards when it opens.
/// Uses reflection to find the reward screen type at runtime (STS2 API may vary).
/// </summary>
[HarmonyPatch]
public static class RewardExportPatches
{
    private static MethodBase? _targetMethod;

    [HarmonyTargetMethod]
    public static MethodBase? TargetMethod()
    {
        if (_targetMethod != null) return _targetMethod;

        try
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => a.GetName().Name?.Contains("Sts2") == true || a.GetName().Name?.Contains("MegaCrit") == true)
                .ToList();

            IEnumerable<Type> allTypes = Array.Empty<Type>();
            foreach (var asm in assemblies)
            {
                try
                {
                    allTypes = allTypes.Concat(asm.GetTypes());
                }
                catch (ReflectionTypeLoadException) { }
            }

            var rewardTypes = allTypes
                .Where(t => t.IsClass && !t.IsAbstract &&
                    (t.Name.Contains("Reward") || t.Name.Contains("CardChoice") ||
                     t.Name.Contains("CardPick") || t.Name.Contains("CombatReward") ||
                     t.Name.Contains("CardSelect") || t.Name.Contains("CardOffer")))
                .OrderByDescending(t => t.Name.Contains("Card"))
                .ThenByDescending(t => t.Name.Length)
                .ToList();

            foreach (var type in rewardTypes)
            {
                foreach (var methodName in new[] { "Show", "Display", "Open", "ShowRewards", "Present", "Ready", "OnReady", "_Ready", "Init", "EnterTree", "_EnterTree" })
                {
                    var method = type.GetMethod(methodName,
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (method != null && method.GetParameters().Length <= 1)
                    {
                        _targetMethod = method;
                        Log.Info($"[SmartPick] Reward patch target: {type.FullName}.{method.Name}");
                        return _targetMethod;
                    }
                }
            }

            Log.Info("[SmartPick] Reward patch: no reward screen type found, reward advisor disabled.");
        }
        catch (Exception ex)
        {
            Log.Error($"[SmartPick] Reward patch discovery: {ex.Message}");
        }

        // Return a harmless dummy method so Harmony doesn't throw, effectively disabling this patch.
        _targetMethod = typeof(RewardExportPatches).GetMethod(nameof(DummyTarget), BindingFlags.NonPublic | BindingFlags.Static);
        return _targetMethod;
    }

    // Dummy method used when we couldn't find a real reward screen method.
    private static void DummyTarget()
    {
    }

    [HarmonyPostfix]
    public static void AfterRewardScreenShown(object __instance)
    {
        try
        {
            ExportIfValid(__instance);
            Task.Run(async () =>
            {
                await Task.Delay(100);
                ExportIfValid(__instance);
                await Task.Delay(200);
                ExportIfValid(__instance);
            });
        }
        catch (Exception ex)
        {
            Log.Error($"[SmartPick] Reward export postfix: {ex.Message}");
        }
    }

    private static void ExportIfValid(object instance)
    {
        var options = ExtractRewardOptions(instance);
        if (options == null || options.Count == 0) return;
        RewardExporter.ExportRewardState(options);
    }

    private static List<string>? ExtractRewardOptions(object instance)
    {
        if (instance == null) return null;

        try
        {
            var fromChildren = ExtractFromNodeChildren(instance);
            if (fromChildren != null && fromChildren.Count >= 2 && fromChildren.Count <= 5)
                return fromChildren;

            var t = instance.GetType();
            var candidates = new List<(string source, List<string> names)>();

            var propNames = new[] { "OfferedCards", "RewardCards", "CardOptions", "CardChoices", "Choices", "Options", "Rewards", "Cards" };
            foreach (var propName in propNames)
            {
                var prop = t.GetProperty(propName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (prop?.GetValue(instance) is System.Collections.IEnumerable enumerable)
                {
                    var names = CollectCardNames(enumerable);
                    if (names.Count > 0) candidates.Add((propName, names));
                }
            }

            var fieldNames = new[] { "_rewardCards", "_offeredCards", "_choices", "_options", "_cards" };
            foreach (var fieldName in fieldNames)
            {
                var field = t.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
                if (field?.GetValue(instance) is System.Collections.IEnumerable fe)
                {
                    var names = CollectCardNames(fe);
                    if (names.Count > 0) candidates.Add((fieldName, names));
                }
            }

            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (f.FieldType.IsGenericType && f.FieldType.GetGenericTypeDefinition().Name.Contains("List"))
                {
                    try
                    {
                        if (f.GetValue(instance) is System.Collections.IEnumerable list)
                        {
                            var names = CollectCardNames(list);
                            if (names.Count >= 2 && names.Count <= 5) candidates.Add((f.Name, names));
                        }
                    }
                    catch { }
                }
            }

            var best = candidates
                .Where(c => c.names.Count >= 2 && c.names.Count <= 5)
                .OrderByDescending(c => c.names.Count == 3)
                .ThenByDescending(c => c.source.Contains("Reward") || c.source.Contains("Offer") || c.source.Contains("Choice"))
                .FirstOrDefault();
            if (best.names != null && best.names.Count > 0)
            {
                Log.Info($"[SmartPick] Reward options from {best.source}: {string.Join(", ", best.names)}");
                return best.names;
            }
            var fallback = candidates.FirstOrDefault();
            if (fallback.names != null && fallback.names.Count >= 2 && fallback.names.Count <= 5)
                return fallback.names;
        }
        catch (Exception ex)
        {
            Log.Error($"[SmartPick] ExtractRewardOptions: {ex.Message}");
        }

        return null;
    }

    private static List<string>? ExtractFromNodeChildren(object instance)
    {
        if (instance is not Godot.Node node) return null;
        try
        {
            var names = new List<string>();
            foreach (var child in node.GetChildren())
            {
                if (child == null) continue;
                var name = GetCardName(child);
                if (!string.IsNullOrEmpty(name)) names.Add(name);
            }
            return names.Count >= 2 && names.Count <= 5 ? names : null;
        }
        catch { return null; }
    }

    private static List<string> CollectCardNames(System.Collections.IEnumerable enumerable)
    {
        var names = new List<string>();
        foreach (var item in enumerable)
        {
            if (item == null) continue;
            var name = GetCardName(item);
            if (!string.IsNullOrEmpty(name)) names.Add(name);
        }
        return names;
    }

    internal static string? GetCardName(object cardObj)
    {
        if (cardObj == null) return null;
        try
        {
            if (cardObj is MegaCrit.Sts2.Core.Models.CardModel cm)
                return cm.Title ?? cm.GetType().Name;

            var t = cardObj.GetType();
            var modelProp = t.GetProperty("Model", BindingFlags.Public | BindingFlags.Instance)
                ?? t.GetProperty("CardModel", BindingFlags.Public | BindingFlags.Instance)
                ?? t.GetProperty("Card", BindingFlags.Public | BindingFlags.Instance);
            if (modelProp?.GetValue(cardObj) is MegaCrit.Sts2.Core.Models.CardModel m)
                return m.Title ?? m.GetType().Name;

            // Merchant shop: MegaCrit.Sts2.Core.Entities.Merchant.MerchantCardEntry exposes Card via CreationResult.Card
            var creationProp = t.GetProperty("CreationResult",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (creationProp?.GetValue(cardObj) is { } creation)
            {
                var cardFromCr = creation.GetType().GetProperty("Card",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (cardFromCr?.GetValue(creation) is MegaCrit.Sts2.Core.Models.CardModel m2)
                    return m2.Title ?? m2.GetType().Name;
            }

            var titleProp = t.GetProperty("Title", BindingFlags.Public | BindingFlags.Instance);
            if (titleProp?.GetValue(cardObj) is string s && !string.IsNullOrEmpty(s))
                return s;

            return null;
        }
        catch { }
        return null;
    }
}

/// <summary>
/// Explicit patch for the main rewards screen. This is more reliable than reflection-based guessing.
/// Hooks NRewardsScreen.ShowScreen (game v0.107+; replaced SetRewards) and exports any card rewards
/// that look like "choose a card" options.
/// </summary>
[HarmonyPatch(typeof(NRewardsScreen), nameof(NRewardsScreen.ShowScreen))]
public static class RewardsScreenExportPatches
{
    [HarmonyPostfix]
    public static void AfterShowScreen(RewardsSet set)
    {
        var rewards = set?.Rewards;
        if (rewards == null) return;

        try
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var reward in rewards)
                CollectCardTitlesFromReward(reward, names);

            var options = names.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            if (options.Count is >= 2 and <= 6)
            {
                Log.Info($"[SmartPick] RewardsScreenExport: {options.Count} options: {string.Join(", ", options)}");
                RewardExporter.ExportRewardState(options);
            }
            else if (options.Count > 0)
            {
                Log.Info($"[SmartPick] RewardsScreenExport: skipped ({options.Count} titles, expected 2–6): {string.Join(", ", options)}");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[SmartPick] RewardsScreenExport: {ex.Message}");
        }
    }

    /// <summary>Combat reward screen passes gold, potions, relics, and cards in one list — only reflect card-like rewards.</summary>
    private static bool ShouldReflectPropertiesForCardTitles(Reward reward)
    {
        if (reward is LinkedRewardSet)
            return false;
        var n = reward.GetType().Name;
        if (n.Contains("Gold", StringComparison.OrdinalIgnoreCase)) return false;
        if (n.Contains("Potion", StringComparison.OrdinalIgnoreCase)) return false;
        if (n.Contains("Relic", StringComparison.OrdinalIgnoreCase)) return false;
        if (n.Contains("Key", StringComparison.OrdinalIgnoreCase)) return false;
        if (n.Contains("Card", StringComparison.OrdinalIgnoreCase)) return true;
        if (n.Contains("Deck", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static void CollectCardTitlesFromReward(Reward? reward, HashSet<string> acc)
    {
        if (reward == null) return;

        if (reward is LinkedRewardSet linked)
        {
            foreach (var r in linked.Rewards)
                CollectCardTitlesFromReward(r, acc);
            return;
        }

        if (!ShouldReflectPropertiesForCardTitles(reward))
            return;

        var t = reward.GetType();
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        foreach (var prop in t.GetProperties(flags))
        {
            object? val;
            try { val = prop.GetValue(reward); }
            catch { continue; }
            if (val == null) continue;

            if (val is Reward nested)
            {
                CollectCardTitlesFromReward(nested, acc);
                continue;
            }

            if (val is string) continue;

            if (val is System.Collections.IEnumerable enumerable and not string)
            {
                foreach (var item in enumerable)
                {
                    if (item == null) continue;
                    if (item is Reward r2)
                    {
                        CollectCardTitlesFromReward(r2, acc);
                        continue;
                    }
                    var name = RewardExportPatches.GetCardName(item);
                    if (CardTitleHeuristics.IsPlausibleCardTitle(name))
                        acc.Add(name!);
                }
            }
            else
            {
                if (val is Reward r3)
                {
                    CollectCardTitlesFromReward(r3, acc);
                    continue;
                }
                var name = RewardExportPatches.GetCardName(val);
                if (CardTitleHeuristics.IsPlausibleCardTitle(name))
                    acc.Add(name!);
            }
        }
    }
}

/// <summary>Clears exported reward JSON when the rewards overlay closes.</summary>
[HarmonyPatch(typeof(NRewardsScreen), nameof(NRewardsScreen.AfterOverlayClosed))]
public static class RewardsScreenClearPatches
{
    [HarmonyPostfix]
    public static void AfterRewardsOverlayClosed()
    {
        try { RewardExporter.ClearRewardState(); }
        catch (Exception ex) { Log.Error($"[SmartPick] RewardsScreenClear: {ex.Message}"); }
        // Don't clear badges here — the card pick screen opens AFTER rewards overlay closes.
        // Badges will be cleared when the card pick is complete (next SetRewards or merchant close).
    }
}
