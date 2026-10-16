using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Nodes.Screens.RelicCollection;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;

namespace FirstMod;

/// <summary>
/// Attaches tier badges to relic nodes on reward, merchant, treasure, and compendium screens.
/// </summary>
public static class RelicBadgeOverlay
{
    private static readonly List<Node> _badges = new();

    private static readonly Dictionary<string, Color> TierColors = new()
    {
        ["S"] = new Color("ff8000"),  // orange — legendary
        ["A"] = new Color("a335ee"),  // purple — epic
        ["B"] = new Color("0070dd"),  // blue — rare
        ["C"] = new Color("1eff00"),  // green — uncommon
        ["D"] = new Color("9d9d9d"),  // grey — poor
        ["?"] = new Color("666666"),
    };

    public static void AttachBadges(Node screenNode)
    {
        try
        {
            var relics = new List<(Control node, RelicModel model)>();
            FindRelicsInTree(screenNode, relics, 0);

            foreach (var (node, model) in relics)
            {
                var entry = model.Id.Entry;
                var relicName = RelicTierData.IdEntryToDisplayName(entry);
                var tier = RelicTierData.GetTier(relicName);

                if (string.IsNullOrEmpty(relicName)) continue;
                if (tier == null) continue;

                var badge = CreateBadge(tier, node);
                if (badge != null)
                    _badges.Add(badge);
            }

        }
        catch (Exception ex)
        {
            Log.Error($"[SmartPick] RelicBadge AttachBadges: {ex.Message}\n{ex.StackTrace}");
        }
    }

    public static void AttachBadgesDeferred(Node screenNode, int delayMs = 150)
    {
        Task.Run(async () =>
        {
            await Task.Delay(delayMs);
            Callable.From(() =>
            {
                try { AttachBadges(screenNode); }
                catch (Exception ex) { Log.Error($"[SmartPick] RelicBadge Deferred: {ex.Message}"); }
            }).CallDeferred();
        });
    }

    public static void RebadgeAfterPurchase(Node screenNode)
    {
        Task.Run(async () =>
        {
            await Task.Delay(300);
            Callable.From(() =>
            {
                try
                {
                    if (!GodotObject.IsInstanceValid(screenNode)) return;
                    ClearBadges();
                    AttachBadges(screenNode);
                }
                catch (Exception ex) { Log.Error($"[SmartPick] RelicBadge RebadgeAfterPurchase: {ex.Message}"); }
            }).CallDeferred();
        });
    }

    public static void ClearBadges()
    {
        foreach (var badge in _badges)
        {
            try
            {
                if (GodotObject.IsInstanceValid(badge))
                    badge.QueueFree();
            }
            catch { }
        }
        _badges.Clear();
    }

    /// <summary>
    /// Attach a tier badge to the relic icon on an ancient event option button.
    /// Badge is added as child of the RelicIcon node, so it's freed automatically with the event screen.
    /// </summary>
    public static void AttachBadgeToEventButton(Control eventButton, RelicModel relicModel)
    {
        try
        {
            var entry = relicModel.Id.Entry;
            var relicName = RelicTierData.IdEntryToDisplayName(entry);
            var tier = RelicTierData.GetTier(relicName);

            if (string.IsNullOrEmpty(relicName) || tier == null) return;

            // Find the %RelicIcon TextureRect
            var relicIcon = eventButton.GetNodeOrNull<TextureRect>("%RelicIcon");
            if (relicIcon == null || !relicIcon.Visible) return;

            var color = TierColors.GetValueOrDefault(tier.Tier, TierColors["?"]);

            const int badgeSize = 28;
            const int radius = badgeSize / 2;

            var badge = new PanelContainer();
            badge.CustomMinimumSize = new Vector2(badgeSize, badgeSize);
            badge.Size = new Vector2(badgeSize, badgeSize);
            badge.MouseFilter = Control.MouseFilterEnum.Ignore;

            var outerStyle = new StyleBoxFlat();
            outerStyle.BgColor = new Color(0.12f, 0.10f, 0.08f, 0.95f);
            outerStyle.CornerRadiusBottomLeft = radius;
            outerStyle.CornerRadiusBottomRight = radius;
            outerStyle.CornerRadiusTopLeft = radius;
            outerStyle.CornerRadiusTopRight = radius;
            outerStyle.BorderWidthBottom = 2;
            outerStyle.BorderWidthTop = 2;
            outerStyle.BorderWidthLeft = 2;
            outerStyle.BorderWidthRight = 2;
            outerStyle.BorderColor = new Color(0.35f, 0.28f, 0.2f);
            outerStyle.ContentMarginLeft = 2;
            outerStyle.ContentMarginRight = 2;
            outerStyle.ContentMarginTop = 2;
            outerStyle.ContentMarginBottom = 2;
            badge.AddThemeStyleboxOverride("panel", outerStyle);

            var inner = new PanelContainer();
            inner.MouseFilter = Control.MouseFilterEnum.Ignore;
            var innerStyle = new StyleBoxFlat();
            innerStyle.BgColor = color;
            innerStyle.CornerRadiusBottomLeft = radius;
            innerStyle.CornerRadiusBottomRight = radius;
            innerStyle.CornerRadiusTopLeft = radius;
            innerStyle.CornerRadiusTopRight = radius;
            inner.AddThemeStyleboxOverride("panel", innerStyle);

            var label = new Label();
            label.Text = tier.Tier;
            label.HorizontalAlignment = HorizontalAlignment.Center;
            label.VerticalAlignment = VerticalAlignment.Center;
            label.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f));
            label.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f));
            label.AddThemeConstantOverride("outline_size", 5);
            label.AddThemeFontSizeOverride("font_size", 15);
            label.MouseFilter = Control.MouseFilterEnum.Ignore;
            inner.AddChild(label);
            badge.AddChild(inner);

            relicIcon.AddChild(badge);
            // Position at top-right of the relic icon
            badge.Position = new Vector2(relicIcon.Size.X - badgeSize * 0.7f, -badgeSize * 0.3f);
        }
        catch (Exception ex)
        {
            Log.Error($"[SmartPick] AttachBadgeToEventButton: {ex.Message}");
        }
    }

    private static void FindRelicsInTree(Node parent, List<(Control, RelicModel)> results, int depth)
    {
        if (depth > 15) return;
        foreach (var child in parent.GetChildren())
        {
            if (child == null) continue;

            // NRelicBasicHolder — relic reward screen (typed access)
            if (child is NRelicBasicHolder basicHolder && SafeModel(basicHolder.Relic) is { } basicModel)
            {
                results.Add((basicHolder, basicModel));
                continue;
            }

            // NMerchantRelic — merchant shop relic (typed access, private _relic via reflection)
            if (child is NMerchantRelic merchantRelic)
            {
                var model = GetPrivateField<RelicModel>(merchantRelic, "_relic");
                if (model != null)
                {
                    results.Add((merchantRelic, model));
                    continue;
                }
            }

            // NRelicCollectionEntry — compendium (typed access, public relic field)
            if (child is NRelicCollectionEntry collectionEntry && collectionEntry.relic != null)
            {
                results.Add((collectionEntry, collectionEntry.relic));
                continue;
            }

            // NTreasureRoomRelicHolder — treasure chest (typed access).
            // In singleplayer the multiplayer holders are never initialized, so their NRelic has no model.
            if (child is NTreasureRoomRelicHolder treasureHolder)
            {
                if (SafeModel(treasureHolder.Relic) is { } treasureModel)
                    results.Add((treasureHolder, treasureModel));
                continue;
            }

            FindRelicsInTree(child, results, depth + 1);
        }
    }

    /// <summary>
    /// NRelic.Model throws instead of returning null when the relic hasn't been set yet.
    /// </summary>
    private static RelicModel? SafeModel(NRelic? relic)
    {
        if (relic == null) return null;
        try { return relic.Model; }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>
    /// Find the NRelic node inside a holder to attach badge to its coordinate space.
    /// </summary>
    private static Control? FindRelicIconParent(Control holder)
    {
        // Look for NRelic child (it contains the actual icon)
        foreach (var child in holder.GetChildren())
        {
            if (child is NRelic relic)
                return relic;
        }
        // Recurse one level deeper
        foreach (var child in holder.GetChildren())
        {
            if (child is Control ctrl)
            {
                foreach (var grandchild in ctrl.GetChildren())
                {
                    if (grandchild is NRelic relic)
                        return relic;
                }
            }
        }
        return null;
    }

    private static T? GetPrivateField<T>(object obj, string fieldName) where T : class
    {
        try
        {
            var field = obj.GetType().GetField(fieldName,
                BindingFlags.NonPublic | BindingFlags.Instance);
            return field?.GetValue(obj) as T;
        }
        catch { return null; }
    }

    private static Control? CreateBadge(RelicTierData.RelicTierResult tier, Control relicHolder)
    {
        try
        {
            var color = TierColors.GetValueOrDefault(tier.Tier, TierColors["?"]);
            var textColor = new Color(1f, 1f, 1f);

            var badge = new Control();
            badge.Name = "SmartPickRelicBadge";
            badge.MouseFilter = Control.MouseFilterEnum.Ignore;

            // --- Main circle: tier letter ---
            const int mainSize = 32;
            const int mainR = mainSize / 2;
            var mainCircle = new PanelContainer();
            mainCircle.CustomMinimumSize = new Vector2(mainSize, mainSize);
            mainCircle.Size = new Vector2(mainSize, mainSize);
            mainCircle.Position = Vector2.Zero;
            mainCircle.MouseFilter = Control.MouseFilterEnum.Ignore;

            var mainStyle = new StyleBoxFlat();
            mainStyle.BgColor = new Color(0.12f, 0.10f, 0.08f, 0.95f);
            mainStyle.CornerRadiusBottomLeft = mainR;
            mainStyle.CornerRadiusBottomRight = mainR;
            mainStyle.CornerRadiusTopLeft = mainR;
            mainStyle.CornerRadiusTopRight = mainR;
            mainStyle.BorderWidthBottom = 2;
            mainStyle.BorderWidthTop = 2;
            mainStyle.BorderWidthLeft = 2;
            mainStyle.BorderWidthRight = 2;
            mainStyle.BorderColor = new Color(0.35f, 0.28f, 0.2f);
            mainStyle.ContentMarginLeft = 2;
            mainStyle.ContentMarginRight = 2;
            mainStyle.ContentMarginTop = 2;
            mainStyle.ContentMarginBottom = 2;
            mainCircle.AddThemeStyleboxOverride("panel", mainStyle);

            var innerMain = new PanelContainer();
            innerMain.MouseFilter = Control.MouseFilterEnum.Ignore;
            var innerMainStyle = new StyleBoxFlat();
            innerMainStyle.BgColor = color;
            innerMainStyle.CornerRadiusBottomLeft = mainR;
            innerMainStyle.CornerRadiusBottomRight = mainR;
            innerMainStyle.CornerRadiusTopLeft = mainR;
            innerMainStyle.CornerRadiusTopRight = mainR;
            innerMain.AddThemeStyleboxOverride("panel", innerMainStyle);

            var tierLabel = new Label();
            tierLabel.Text = tier.Tier;
            tierLabel.HorizontalAlignment = HorizontalAlignment.Center;
            tierLabel.VerticalAlignment = VerticalAlignment.Center;
            tierLabel.AddThemeColorOverride("font_color", textColor);
            tierLabel.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f));
            tierLabel.AddThemeConstantOverride("outline_size", 5);
            tierLabel.AddThemeFontSizeOverride("font_size", 18);
            tierLabel.MouseFilter = Control.MouseFilterEnum.Ignore;
            innerMain.AddChild(tierLabel);
            mainCircle.AddChild(innerMain);

            badge.AddChild(mainCircle);

            // Find the NRelic or icon node inside holder to parent badge to
            var attachTarget = FindRelicIconParent(relicHolder) ?? relicHolder;
            attachTarget.AddChild(badge);
            badge.MouseFilter = Control.MouseFilterEnum.Ignore;
            // Position at top-right of the icon
            mainCircle.Position = new Vector2(attachTarget.Size.X - mainSize * 0.7f, -mainSize * 0.3f);

            return badge;
        }
        catch (Exception ex)
        {
            Log.Error($"[SmartPick] RelicBadge CreateBadge: {ex.Message}");
            return null;
        }
    }
}
