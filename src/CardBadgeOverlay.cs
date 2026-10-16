using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;

namespace FirstMod;

/// <summary>
/// Attaches tier badges to NCardHolder nodes when card screens open.
/// Uses NCardHolder.CardModel to get card identity — no node name parsing needed.
/// </summary>
public static class CardBadgeOverlay
{
    private static readonly List<Node> _badges = new();

    /// <summary>Track open screens for re-badging after overlapping screen closes.</summary>
    public static Node? ActiveMerchant { get; set; }
    public static Node? ActiveRewardScreen { get; set; }
    public static Node? ActiveGridSelection { get; set; }

    private static readonly Dictionary<string, Color> TierColors = new()
    {
        ["S"] = new Color("ff8000"),  // orange — legendary
        ["A"] = new Color("a335ee"),  // purple — epic
        ["B"] = new Color("0070dd"),  // blue — rare
        ["C"] = new Color("1eff00"),  // green — uncommon
        ["D"] = new Color("9d9d9d"),  // grey — poor
        ["F"] = new Color("9d9d9d"),
        ["?"] = new Color("666666"),
    };

    /// <summary>
    /// Scan a screen node for NCardHolder children and attach tier badges.
    /// Called from Harmony postfixes when card screens open.
    /// </summary>
    public static void AttachBadges(Node screenNode)
    {
        try
        {
            var fallbackCharacter = CombatExporter.ResolveCharacterName();
            var cards = new List<(Control node, CardModel model)>();
            FindCardsInTree(screenNode, cards, 0);
            foreach (var (node, model) in cards)
            {
                var cardName = model.Id.Entry;
                if (string.IsNullOrEmpty(cardName)) continue;

                // Get character from the card's pool (works correctly in multiplayer)
                string character;
                try { character = model.Pool?.Title ?? fallbackCharacter; }
                catch { character = fallbackCharacter; }

                var lookupName = NormalizeCardId(cardName);
                var tiers = TierData.GetTiers(character, lookupName);
                if (tiers.Score < 0) continue;

                var badge = CreateBadge(tiers, node);
                if (badge != null)
                    _badges.Add(badge);
            }


        }
        catch (Exception ex)
        {
            Log.Error($"[SmartPick] AttachBadges: {ex.Message}");
        }
    }

    /// <summary>
    /// Attach badges after a short delay (for screens that populate cards asynchronously).
    /// Retries up to 5 times if no cards found yet.
    /// </summary>
    private static volatile int _badgeGeneration;

    public static void AttachBadgesDeferred(Node screenNode, int delayMs = 50)
    {
        var gen = ++_badgeGeneration;
        Task.Run(async () =>
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                await Task.Delay(attempt == 0 ? delayMs : 250);
                if (gen != _badgeGeneration) return;
                Callable.From(() =>
                {
                    try
                    {
                        if (gen != _badgeGeneration) return;
                        if (!GodotObject.IsInstanceValid(screenNode)) return;
                        if (_badges.Count > 0) return; // already attached
                        AttachBadges(screenNode);
                    }
                    catch (Exception ex) { Log.Error($"[SmartPick] AttachBadgesDeferred: {ex.Message}"); }
                }).CallDeferred();
            }
        });
    }

    /// <summary>
    /// Force re-badge after purchase — clears and re-attaches with longer delay
    /// to ensure new merchant items are ready.
    /// </summary>
    public static void RebadgeAfterPurchase(Node screenNode)
    {
        var gen = ++_badgeGeneration;
        Task.Run(async () =>
        {
            await Task.Delay(300);
            if (gen != _badgeGeneration) return;
            Callable.From(() =>
            {
                try
                {
                    if (gen != _badgeGeneration) return;
                    if (!GodotObject.IsInstanceValid(screenNode)) return;
                    ClearBadges();
                    AttachBadges(screenNode);
                }
                catch (Exception ex) { Log.Error($"[SmartPick] RebadgeAfterPurchase: {ex.Message}"); }
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
    /// Find all card-bearing nodes: NCardHolder (reward/deck) and NMerchantCard (shop).
    /// </summary>
    private static void FindCardsInTree(Node parent, List<(Control, CardModel)> results, int depth)
    {
        if (depth > 15) return;
        foreach (var child in parent.GetChildren())
        {
            if (child == null) continue;

            // NCardHolder — used in reward screen and deck view
            if (child is NCardHolder holder && holder.CardModel != null)
            {
                results.Add((holder, holder.CardModel));
                continue;
            }

            // NCard — used in card bundles (stacked cards at run start)
            if (child is NCard card && card.Model != null)
            {
                results.Add((card, card.Model));
                continue;
            }

            // NMerchantCard — used in merchant shop; get CardModel via _cardNode.Model
            if (child.GetType().Name == "NMerchantCard" && child is Control merchantCtrl)
            {
                var model = GetMerchantCardModel(child);
                if (model != null)
                {
                    results.Add((merchantCtrl, model));
                    continue;
                }
            }

            FindCardsInTree(child, results, depth + 1);
        }
    }

    private static CardModel? GetMerchantCardModel(Node merchantCard)
    {
        try
        {
            var cardNodeField = merchantCard.GetType().GetField("_cardNode",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (cardNodeField?.GetValue(merchantCard) is Node cardNode)
            {
                var modelProp = cardNode.GetType().GetProperty("Model",
                    BindingFlags.Public | BindingFlags.Instance);
                return modelProp?.GetValue(cardNode) as CardModel;
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Convert card ID entry (e.g. "CARD_SWORD_BOOMERANG") to title case for tier lookup.
    /// </summary>
    public static string NormalizeCardIdPublic(string entry) => NormalizeCardId(entry);

    private static string NormalizeCardId(string entry)
    {
        // Strip "CARD_" prefix if present
        if (entry.StartsWith("CARD_", StringComparison.OrdinalIgnoreCase))
            entry = entry.Substring(5);

        var words = entry.Split('_', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < words.Length; i++)
            words[i] = char.ToUpper(words[i][0]) + words[i].Substring(1).ToLower();
        return string.Join(" ", words);
    }

    private static PanelContainer? CreateBadge(TierData.TierResult tiers, Control cardHolder)
    {
        try
        {
            var color = TierColors.GetValueOrDefault(tiers.Tier, TierColors["?"]);
            // Always white text — readable on all tier colors
            var textColor = new Color(1f, 1f, 1f);

            // Root container for positioning
            var badge = new Control();
            badge.Name = "SmartPickBadge";
            badge.MouseFilter = Control.MouseFilterEnum.Ignore;

            // --- Main circle: tier letter ---
            const int mainSize = 44;
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
            mainStyle.ContentMarginLeft = 3;
            mainStyle.ContentMarginRight = 3;
            mainStyle.ContentMarginTop = 3;
            mainStyle.ContentMarginBottom = 3;
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
            tierLabel.Text = tiers.Tier;
            tierLabel.HorizontalAlignment = HorizontalAlignment.Center;
            tierLabel.VerticalAlignment = VerticalAlignment.Center;
            tierLabel.AddThemeColorOverride("font_color", textColor);
            tierLabel.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f));
            tierLabel.AddThemeConstantOverride("outline_size", 6);
            tierLabel.AddThemeFontSizeOverride("font_size", 22);
            tierLabel.MouseFilter = Control.MouseFilterEnum.Ignore;
            innerMain.AddChild(tierLabel);
            mainCircle.AddChild(innerMain);

            // --- Small circle: score ---
            const int smallSize = 24;
            const int smallR = smallSize / 2;
            var smallCircle = new PanelContainer();
            smallCircle.CustomMinimumSize = new Vector2(smallSize, smallSize);
            smallCircle.Size = new Vector2(smallSize, smallSize);
            smallCircle.Position = new Vector2(mainSize - 8, 0); // peeks out from right
            smallCircle.MouseFilter = Control.MouseFilterEnum.Ignore;

            var smallStyle = new StyleBoxFlat();
            smallStyle.BgColor = new Color(0.12f, 0.10f, 0.08f, 0.95f);
            smallStyle.CornerRadiusBottomLeft = smallR;
            smallStyle.CornerRadiusBottomRight = smallR;
            smallStyle.CornerRadiusTopLeft = smallR;
            smallStyle.CornerRadiusTopRight = smallR;
            smallStyle.BorderWidthBottom = 2;
            smallStyle.BorderWidthTop = 2;
            smallStyle.BorderWidthLeft = 2;
            smallStyle.BorderWidthRight = 2;
            smallStyle.BorderColor = new Color(0.35f, 0.28f, 0.2f);
            smallStyle.ContentMarginLeft = 1;
            smallStyle.ContentMarginRight = 1;
            smallStyle.ContentMarginTop = 1;
            smallStyle.ContentMarginBottom = 1;
            smallCircle.AddThemeStyleboxOverride("panel", smallStyle);

            var innerSmall = new PanelContainer();
            innerSmall.MouseFilter = Control.MouseFilterEnum.Ignore;
            var innerSmallStyle = new StyleBoxFlat();
            innerSmallStyle.BgColor = new Color(0.18f, 0.15f, 0.12f, 0.95f);
            innerSmallStyle.CornerRadiusBottomLeft = smallR;
            innerSmallStyle.CornerRadiusBottomRight = smallR;
            innerSmallStyle.CornerRadiusTopLeft = smallR;
            innerSmallStyle.CornerRadiusTopRight = smallR;
            innerSmall.AddThemeStyleboxOverride("panel", innerSmallStyle);

            var scoreLabel = new Label();
            scoreLabel.Text = $"{tiers.Score}";
            scoreLabel.HorizontalAlignment = HorizontalAlignment.Center;
            scoreLabel.VerticalAlignment = VerticalAlignment.Center;
            scoreLabel.AddThemeColorOverride("font_color", new Color(0.9f, 0.85f, 0.75f));
            scoreLabel.AddThemeFontSizeOverride("font_size", 11);
            scoreLabel.MouseFilter = Control.MouseFilterEnum.Ignore;
            innerSmall.AddChild(scoreLabel);
            smallCircle.AddChild(innerSmall);

            // Small circle first (behind), main circle on top
            badge.AddChild(smallCircle);
            badge.AddChild(mainCircle);

            // Top-right corner of card (holder origin at card center)
            badge.Position = new Vector2(112, -218);

            cardHolder.AddChild(badge);
            return (PanelContainer)mainCircle; // return a node for tracking
        }
        catch (Exception ex)
        {
            Log.Error($"[SmartPick] CreateBadge: {ex.Message}");
            return null;
        }
    }
}
