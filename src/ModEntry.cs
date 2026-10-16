using FirstMod;
using FirstMod.Patches;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes;

[ModInitializer("Initialize")]
public class ModEntry
{
    public static void Initialize()
    {
        TierData.Initialize();
        RelicTierData.Initialize();
        var harmony = new Harmony("smartpick.patch");
        // Patch each class separately: Harmony's PatchAll() aborts on the first missing target,
        // so a single game API change would otherwise silently disable every patch after it.
        int applied = 0, failed = 0;
        foreach (var type in AccessTools.GetTypesFromAssembly(typeof(ModEntry).Assembly))
        {
            if (!type.GetCustomAttributes(typeof(HarmonyPatch), true).Any())
                continue;
            try
            {
                harmony.CreateClassProcessor(type).Patch();
                applied++;
            }
            catch (Exception ex)
            {
                failed++;
                Log.Error($"[SmartPick] Patch {type.Name} FAILED: {ex.InnerException?.Message ?? ex.Message}");
            }
        }
        Log.Info($"[SmartPick] Harmony patches initialized: {applied} applied, {failed} failed.");

        // If NGame._Ready() already ran before our patch was applied, start F7 polling now
        if (NGame.Instance != null && GodotObject.IsInstanceValid(NGame.Instance))
        {
            TierEditorInstallPatch.EnsurePolling(NGame.Instance);
        }
    }
}