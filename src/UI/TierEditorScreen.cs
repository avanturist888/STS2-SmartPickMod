using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.UI;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Relics;
namespace FirstMod.UI;

/// <summary>
/// In-game tier list editor with drag-and-drop.
/// Opened via F7 hotkey. Added directly to scene root.
/// </summary>
public partial class TierEditorScreen : Control
{
    private static readonly Color BgColor = new(0.08f, 0.06f, 0.12f, 0.95f);

    private enum Tab { Cards, Relics }
    private Tab _currentTab = Tab.Cards;
    private string _currentCharacter = "";

    // Built dynamically from ModelDb.AllCharacters
    private readonly List<(string poolTitle, string localizedName)> _characters = new();

    private VBoxContainer _rowsContainer = null!;
    private Control _cardPreview = null!;
    private Control? _previewHolder;
    private Control? _cardPreviewTipsOwner;
    private bool _isDraggingCard;
    private Label? _statusLabel;
    private readonly List<Button> _charButtons = new();
    private readonly List<Button> _tabButtons = new();
    private readonly Dictionary<string, TierRow> _tierRows = new();
    private TierRow _unassignedRow = null!;

    private static TierEditorScreen? _instance;

    public static void Toggle()
    {
        if (_instance != null && GodotObject.IsInstanceValid(_instance))
        {
            _instance.OnClose();
            return;
        }

        try
        {
            var screen = new TierEditorScreen();
            screen.Name = "SmartPickTierEditor";

            // Add to NGame BEFORE HoverTipsContainer so hover tips render on top of us
            var root = ((SceneTree)Engine.GetMainLoop()).Root;
            if (NGame.Instance != null && GodotObject.IsInstanceValid(NGame.Instance)
                && NGame.Instance.HoverTipsContainer is Node hoverContainer)
            {
                NGame.Instance.AddChild(screen);
                NGame.Instance.MoveChild(screen, hoverContainer.GetIndex());
            }
            else
            {
                root.AddChild(screen);
            }
            _instance = screen;
            // _Ready() may not be called by Godot on unregistered C# classes,
            // so we call BuildUI manually after adding to tree
            screen.BuildUI();
            Patches.TierEditorInstallPatch.SetOpenButtonVisible(false);
        }
        catch (Exception ex)
        {
            _instance = null;
            Log.Error($"[SmartPick] TierEditor Open: {ex.Message}\n{ex.StackTrace}");
        }
    }

    private HBoxContainer? _charFilter;

    public void BuildUI()
    {
        // Build character list from game data
        foreach (var ch in ModelDb.AllCharacters)
        {
            var poolTitle = ch.CardPool?.Title?.ToLowerInvariant() ?? ch.Id.Entry.ToLowerInvariant();
            string localName;
            try { localName = ch.Title.GetFormattedText(); }
            catch { localName = poolTitle; }
            _characters.Add((poolTitle, localName));
        }
        // Colorless is a pseudo-character: real CardPool with Title="colorless", but no CharacterModel
        string colorlessName;
        try { colorlessName = new MegaCrit.Sts2.Core.Localization.LocString("card_library", "POOL_COLORLESS_TIP").GetFormattedText(); }
        catch { colorlessName = "Colorless"; }
        _characters.Add(("colorless", colorlessName));

        if (_characters.Count > 0)
            _currentCharacter = _characters[0].poolTitle;

        // Full-screen
        var viewportSize = GetViewport().GetVisibleRect().Size;
        Position = Vector2.Zero;
        Size = viewportSize;
        MouseFilter = MouseFilterEnum.Stop;
        // Consume ALL input so nothing passes through
        SetProcessInput(true);

        // Background
        var bg = new ColorRect();
        bg.Color = BgColor;
        bg.Position = Vector2.Zero;
        bg.Size = viewportSize;
        bg.MouseFilter = MouseFilterEnum.Stop;
        AddChild(bg);

        // Main layout
        var margin = new MarginContainer();
        margin.Position = Vector2.Zero;
        margin.Size = viewportSize;
        margin.AddThemeConstantOverride("margin_left", 40);
        margin.AddThemeConstantOverride("margin_right", 40);
        margin.AddThemeConstantOverride("margin_top", 20);
        margin.AddThemeConstantOverride("margin_bottom", 20);
        AddChild(margin);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 10);
        margin.AddChild(vbox);

        // Header
        BuildHeader(vbox);

        // Scrollable tier rows
        var scroll = new ScrollContainer();
        scroll.SizeFlagsVertical = SizeFlags.ExpandFill;
        scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
        vbox.AddChild(scroll);

        _rowsContainer = new VBoxContainer();
        _rowsContainer.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _rowsContainer.AddThemeConstantOverride("separation", 0);
        scroll.AddChild(_rowsContainer);

        // Build tier rows with drop handlers
        foreach (var tier in TierData.TierLetters)
        {
            var row = new TierRow(tier);
            _tierRows[tier] = row;
            _rowsContainer.AddChild(row);
            row.BuildUI();
            row.ItemDropped += (itemId, itemType, character, insertAt) => OnItemDroppedToTier(itemId, itemType, character, tier, insertAt);
        }
        _unassignedRow = new TierRow("?");
        _tierRows["?"] = _unassignedRow;
        _rowsContainer.AddChild(_unassignedRow);
        _unassignedRow.BuildUI();
        _unassignedRow.ItemDropped += (itemId, itemType, character, insertAt) => OnItemDroppedToTier(itemId, itemType, character, "?", insertAt);

        // Card preview overlay (shown on hover, on top of everything)
        _cardPreview = new Control();
        _cardPreview.Name = "CardPreview";
        _cardPreview.MouseFilter = MouseFilterEnum.Ignore;
        _cardPreview.Visible = false;
        _cardPreview.ZIndex = 100;
        AddChild(_cardPreview);

        // Footer
        BuildFooter(vbox);

        // Populate
        PopulateItems();
    }

    private void BuildHeader(VBoxContainer parent)
    {
        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 20);
        parent.AddChild(header);

        // Title
        var title = new Label();
        title.Text = "SmartPick Tier Editor";
        title.AddThemeFontSizeOverride("font_size", 28);
        title.AddThemeColorOverride("font_color", new Color(1f, 0.85f, 0.5f));
        header.AddChild(title);

        // Spacer
        var spacer = new Control();
        spacer.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        header.AddChild(spacer);

        // Tab buttons
        var cardsBtn = CreateTabButton("Cards", Tab.Cards);
        var relicsBtn = CreateTabButton("Relics", Tab.Relics);
        header.AddChild(cardsBtn);
        header.AddChild(relicsBtn);
        _tabButtons.Add(cardsBtn);
        _tabButtons.Add(relicsBtn);

        // Character filter (below header, only for cards)
        _charFilter = new HBoxContainer();
        _charFilter.Name = "CharFilter";
        _charFilter.AddThemeConstantOverride("separation", 8);
        parent.AddChild(_charFilter);
        var charFilter = _charFilter;

        foreach (var (poolTitle, localizedName) in _characters)
        {
            var btn = new Button();
            btn.ToggleMode = true;
            btn.ButtonPressed = poolTitle == _currentCharacter;
            StyleToggleButton(btn);

            // Use Button's built-in icon + text (no icon for colorless — it has no CharacterModel)
            try
            {
                var ch = ModelDb.AllCharacters.FirstOrDefault(c =>
                    (c.CardPool?.Title?.ToLowerInvariant() ?? c.Id.Entry.ToLowerInvariant()) == poolTitle);
                if (ch != null)
                {
                    var iconTex = ch.IconTexture;
                    if (iconTex != null) btn.Icon = iconTex;
                }
            }
            catch { }

            btn.Text = localizedName;
            btn.AddThemeFontSizeOverride("font_size", 16);
            btn.AddThemeConstantOverride("icon_max_width", 24);
            var charId = poolTitle;
            btn.Pressed += () => OnCharacterSelected(charId);
            charFilter.AddChild(btn);
            _charButtons.Add(btn);
        }
    }

    private Button CreateTabButton(string text, Tab tab)
    {
        var btn = new Button();
        btn.Text = text;
        btn.ToggleMode = true;
        btn.ButtonPressed = _currentTab == tab;
        btn.CustomMinimumSize = new Vector2(120, 44);
        btn.AddThemeFontSizeOverride("font_size", 20);
        StyleToggleButton(btn);
        btn.Pressed += () => OnTabSelected(tab);
        return btn;
    }

    private static void StyleToggleButton(Button btn)
    {
        // Normal state: subtle
        var normal = new StyleBoxFlat();
        normal.BgColor = new Color(0.15f, 0.13f, 0.2f, 0.7f);
        normal.CornerRadiusBottomLeft = 6;
        normal.CornerRadiusBottomRight = 6;
        normal.CornerRadiusTopLeft = 6;
        normal.CornerRadiusTopRight = 6;
        normal.BorderWidthBottom = 1;
        normal.BorderWidthTop = 1;
        normal.BorderWidthLeft = 1;
        normal.BorderWidthRight = 1;
        normal.BorderColor = new Color(0.3f, 0.25f, 0.4f);
        normal.ContentMarginLeft = 10;
        normal.ContentMarginRight = 10;
        normal.ContentMarginTop = 4;
        normal.ContentMarginBottom = 4;

        // Pressed/active state: bright border + lighter bg
        var pressed = new StyleBoxFlat();
        pressed.BgColor = new Color(0.25f, 0.2f, 0.35f, 0.95f);
        pressed.CornerRadiusBottomLeft = 6;
        pressed.CornerRadiusBottomRight = 6;
        pressed.CornerRadiusTopLeft = 6;
        pressed.CornerRadiusTopRight = 6;
        pressed.BorderWidthBottom = 2;
        pressed.BorderWidthTop = 2;
        pressed.BorderWidthLeft = 2;
        pressed.BorderWidthRight = 2;
        pressed.BorderColor = new Color(1f, 0.85f, 0.4f);
        pressed.ContentMarginLeft = 10;
        pressed.ContentMarginRight = 10;
        pressed.ContentMarginTop = 4;
        pressed.ContentMarginBottom = 4;

        // Hover
        var hover = new StyleBoxFlat();
        hover.BgColor = new Color(0.2f, 0.17f, 0.3f, 0.85f);
        hover.CornerRadiusBottomLeft = 6;
        hover.CornerRadiusBottomRight = 6;
        hover.CornerRadiusTopLeft = 6;
        hover.CornerRadiusTopRight = 6;
        hover.BorderWidthBottom = 1;
        hover.BorderWidthTop = 1;
        hover.BorderWidthLeft = 1;
        hover.BorderWidthRight = 1;
        hover.BorderColor = new Color(0.5f, 0.4f, 0.6f);
        hover.ContentMarginLeft = 10;
        hover.ContentMarginRight = 10;
        hover.ContentMarginTop = 4;
        hover.ContentMarginBottom = 4;

        btn.AddThemeStyleboxOverride("normal", normal);
        btn.AddThemeStyleboxOverride("pressed", pressed);
        btn.AddThemeStyleboxOverride("hover", hover);
        btn.AddThemeStyleboxOverride("hover_pressed", pressed);
        btn.AddThemeColorOverride("font_color", new Color(0.7f, 0.65f, 0.8f));
        btn.AddThemeColorOverride("font_pressed_color", new Color(1f, 0.9f, 0.5f));
        btn.AddThemeColorOverride("font_hover_color", new Color(0.9f, 0.85f, 0.95f));
        btn.AddThemeColorOverride("font_hover_pressed_color", new Color(1f, 0.9f, 0.5f));
    }

    private static Button CreateFooterButton(string text, Color color)
    {
        var btn = new Button();
        btn.Text = text;
        btn.CustomMinimumSize = new Vector2(130, 38);
        btn.AddThemeFontSizeOverride("font_size", 15);

        var normal = new StyleBoxFlat();
        normal.BgColor = new Color(color, 0.15f);
        normal.CornerRadiusBottomLeft = 6;
        normal.CornerRadiusBottomRight = 6;
        normal.CornerRadiusTopLeft = 6;
        normal.CornerRadiusTopRight = 6;
        normal.BorderWidthBottom = 1;
        normal.BorderWidthTop = 1;
        normal.BorderWidthLeft = 1;
        normal.BorderWidthRight = 1;
        normal.BorderColor = new Color(color, 0.5f);
        normal.ContentMarginLeft = 8;
        normal.ContentMarginRight = 8;

        var hover = new StyleBoxFlat();
        hover.BgColor = new Color(color, 0.25f);
        hover.CornerRadiusBottomLeft = 6;
        hover.CornerRadiusBottomRight = 6;
        hover.CornerRadiusTopLeft = 6;
        hover.CornerRadiusTopRight = 6;
        hover.BorderWidthBottom = 2;
        hover.BorderWidthTop = 2;
        hover.BorderWidthLeft = 2;
        hover.BorderWidthRight = 2;
        hover.BorderColor = color;
        hover.ContentMarginLeft = 8;
        hover.ContentMarginRight = 8;

        btn.AddThemeStyleboxOverride("normal", normal);
        btn.AddThemeStyleboxOverride("hover", hover);
        btn.AddThemeStyleboxOverride("pressed", hover);
        btn.AddThemeColorOverride("font_color", color);
        btn.AddThemeColorOverride("font_hover_color", new Color(1f, 1f, 1f));
        return btn;
    }

    private void BuildFooter(VBoxContainer parent)
    {
        var footer = new HBoxContainer();
        footer.AddThemeConstantOverride("separation", 15);
        parent.AddChild(footer);

        var exportBtn = CreateFooterButton("Export", new Color(0.5f, 0.8f, 1f));
        exportBtn.Pressed += OnExport;
        footer.AddChild(exportBtn);

        var importBtn = CreateFooterButton("Import", new Color(0.5f, 0.8f, 1f));
        importBtn.Pressed += OnImport;
        footer.AddChild(importBtn);

        // Spacer between import/export and reset
        var resetSpacer = new Control();
        resetSpacer.CustomMinimumSize = new Vector2(30, 0);
        footer.AddChild(resetSpacer);

        var resetBtn = CreateFooterButton("Reset to Defaults", new Color(1f, 0.4f, 0.3f));
        resetBtn.Pressed += OnResetConfirm;
        footer.AddChild(resetBtn);

        var clearBtn = CreateFooterButton("Clear All to ?", new Color(1f, 0.75f, 0.2f));
        clearBtn.Pressed += OnClearAllConfirm;
        footer.AddChild(clearBtn);

        var spacer = new Control();
        spacer.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        footer.AddChild(spacer);

        _statusLabel = new Label();
        _statusLabel.AddThemeFontSizeOverride("font_size", 14);
        _statusLabel.AddThemeColorOverride("font_color", new Color(0.7f, 0.9f, 0.5f));
        footer.AddChild(_statusLabel);

        var spacer2 = new Control();
        spacer2.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        footer.AddChild(spacer2);

        var closeBtn = CreateFooterButton("Close [F7]", new Color(0.8f, 0.8f, 0.8f));
        closeBtn.Pressed += OnClose;
        footer.AddChild(closeBtn);
    }

    private void PopulateItems()
    {
        // Clear existing
        foreach (var row in _tierRows.Values)
            row.ClearItems();
        _unassignedRow.ClearItems();

        if (_currentTab == Tab.Cards)
            PopulateCards();
        else
            PopulateRelics();
    }

    private void PopulateCards()
    {
        try
        {
            var allCards = ModelDb.AllCards
                .Where(c => c.Pool?.Title?.ToLowerInvariant() == _currentCharacter)
                .ToDictionary(c => CardBadgeOverlay.NormalizeCardIdPublic(c.Id.Entry).ToLowerInvariant(), c => c);

            var customTiers = TierData.GetCustomTiersForCharacter(_currentCharacter);
            var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // First: add custom-ordered cards in their saved order
            if (customTiers != null)
            {
                foreach (var tier in TierData.AllTierLetters)
                {
                    if (!customTiers.TryGetValue(tier, out var orderedList)) continue;
                    if (!_tierRows.TryGetValue(tier, out var row)) continue;

                    foreach (var cardKey in orderedList)
                    {
                        var lookupKey = TierData.NormalizeCardName(cardKey);
                        var card = allCards.Values.FirstOrDefault(c =>
                            CardBadgeOverlay.NormalizeCardIdPublic(c.Id.Entry).Equals(cardKey, StringComparison.OrdinalIgnoreCase) ||
                            TierData.NormalizeCardName(CardBadgeOverlay.NormalizeCardIdPublic(c.Id.Entry)) == lookupKey);
                        if (card == null) continue;

                        var icon = CreateCardIcon(card);
                        if (icon == null) continue;
                        row.AddItem(icon);
                        placed.Add(card.Id.Entry.ToLowerInvariant());
                    }
                }
            }

            // Then: add remaining cards by built-in tier
            foreach (var card in allCards.Values.OrderBy(c => c.Id.Entry))
            {
                if (placed.Contains(card.Id.Entry.ToLowerInvariant())) continue;

                var tiers = TierData.GetTiers(_currentCharacter, CardBadgeOverlay.NormalizeCardIdPublic(card.Id.Entry));
                var targetTier = tiers.Tier;
                var icon = CreateCardIcon(card);
                if (icon == null) continue;

                if (_tierRows.TryGetValue(targetTier, out var row))
                    row.AddItem(icon);
                else
                    _unassignedRow.AddItem(icon);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[SmartPick] TierEditor PopulateCards: {ex.Message}\n{ex.StackTrace}");
        }
    }

    private void PopulateRelics()
    {
        try
        {
            var allRelics = ModelDb.AllRelics.ToDictionary(r => r.Id.Entry.ToLowerInvariant(), r => r);
            var customOrdered = RelicTierData.GetCustomOrdered();
            var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // First: custom-ordered relics
            foreach (var tier in TierData.AllTierLetters)
            {
                if (!customOrdered.TryGetValue(tier, out var orderedList)) continue;
                if (!_tierRows.TryGetValue(tier, out var row)) continue;

                foreach (var relicKey in orderedList)
                {
                    var relic = allRelics.Values.FirstOrDefault(r =>
                        RelicTierData.IdEntryToDisplayName(r.Id.Entry).Equals(relicKey, StringComparison.OrdinalIgnoreCase) ||
                        r.Id.Entry.Equals(relicKey, StringComparison.OrdinalIgnoreCase));
                    if (relic == null) continue;

                    var icon = CreateRelicIcon(relic);
                    if (icon == null) continue;
                    row.AddItem(icon);
                    placed.Add(relic.Id.Entry.ToLowerInvariant());
                }
            }

            // Then: remaining relics by built-in tier
            foreach (var relic in allRelics.Values.OrderBy(r => r.Id.Entry))
            {
                if (placed.Contains(relic.Id.Entry.ToLowerInvariant())) continue;

                var relicName = RelicTierData.IdEntryToDisplayName(relic.Id.Entry);
                var tier = RelicTierData.GetTier(relicName);
                var icon = CreateRelicIcon(relic);
                if (icon == null) continue;

                if (tier != null && _tierRows.TryGetValue(tier.Tier, out var row))
                    row.AddItem(icon);
                else
                    _unassignedRow.AddItem(icon);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[SmartPick] TierEditor PopulateRelics: {ex.Message}\n{ex.StackTrace}");
        }
    }

    private Control? CreateCardIcon(CardModel card)
    {
        try
        {
            string displayName;
            try { displayName = card.Title; }
            catch { displayName = CardBadgeOverlay.NormalizeCardIdPublic(card.Id.Entry); }

            var icon = new TierItemIcon(displayName, null);
            icon.ItemId = card.Id.Entry;
            icon.ItemType = TierItemIcon.Type.Card;
            icon.Character = _currentCharacter;

            // Game hover tips on the icon itself (keyword explanations)
            try { icon.HoverTips = card.HoverTips; } catch { }

            // NCard preview on hover
            icon.ItemHoverEntered += (source) => { if (!_isDraggingCard) ShowCardPreview(card, source); };
            icon.ItemHoverExited += () => { if (!_isDraggingCard) HideCardPreview(); };
            // Keep card preview during drag, follow cursor
            icon.ItemDragStarted += (source) => { _isDraggingCard = true; ShowCardPreview(card, source); };
            icon.ItemDragEnded += () => { _isDraggingCard = false; HideCardPreview(); };

            return icon;
        }
        catch (Exception ex)
        {
            Log.Error($"[SmartPick] CreateCardIcon: {ex.Message}");
            return null;
        }
    }

    private Control? CreateRelicIcon(RelicModel relic)
    {
        try
        {
            string displayName;
            try { displayName = relic.Title.GetFormattedText(); }
            catch { displayName = RelicTierData.IdEntryToDisplayName(relic.Id.Entry); }

            NRelic? nRelic = null;
            try { nRelic = NRelic.Create(relic, NRelic.IconSize.Small); }
            catch { }

            var icon = new TierItemIcon(displayName, nRelic);
            icon.ItemId = relic.Id.Entry;
            icon.ItemType = TierItemIcon.Type.Relic;

            // Game hover tips for relic details
            try
            {
                var tips = relic.HoverTips?.ToList();
                icon.HoverTips = tips;
            }
            catch (Exception ex)
            {
                Log.Error($"[SmartPick] CreateRelicIcon HoverTips failed for {relic.Id.Entry}: {ex.Message}");
            }

            return icon;
        }
        catch (Exception ex)
        {
            Log.Error($"[SmartPick] CreateRelicIcon: {ex.Message}");
            return null;
        }
    }

    private void ShowCardPreview(CardModel card, Control source)
    {
        foreach (var child in _cardPreview.GetChildren())
            child.QueueFree();

        try
        {
            // Try canonical first (like NCardLibraryGrid does), then mutable clone as fallback
            NCard? nCard = null;
            try
            {
                nCard = NCard.Create(card, ModelVisibility.Visible);
            }
            catch (Exception ex1)
            {
                Log.Error($"[SmartPick] ShowCardPreview canonical failed: {ex1.Message}");
                try
                {
                    var mutableCard = (CardModel)card.MutableClone();
                    nCard = NCard.Create(mutableCard, ModelVisibility.Visible);
                }
                catch (Exception ex2)
                {
                    Log.Error($"[SmartPick] ShowCardPreview clone failed: {ex2.Message}");
                }
            }

            if (nCard == null)
            {
                Log.Error("[SmartPick] ShowCardPreview: NCard.Create returned null");
                return;
            }

            try { nCard.UpdateVisuals(PileType.None, CardPreviewMode.Normal); }
            catch (Exception ex) { Log.Error($"[SmartPick] UpdateVisuals failed: {ex.Message}"); }

            nCard.MouseFilter = MouseFilterEnum.Ignore;
            // NCard from pool may have size 0 — use NGridCardHolder like the game does
            var holder = NGridCardHolder.Create(nCard);
            if (holder != null)
            {
                holder.MouseFilter = MouseFilterEnum.Ignore;
                _cardPreview.AddChild(holder);

                _previewHolder = holder;
            }
            else
            {
                _cardPreview.AddChild(nCard);
            }

            UpdatePreviewPosition();
            _cardPreview.Visible = true;

            // Show hover tips (keyword explanations) next to the card preview
            try
            {
                var tips = card.HoverTips?.ToList();
                if (tips != null && tips.Count > 0)
                {
                    var tipSet = NHoverTipSet.CreateAndShow(source, tips);
                    var cx = _cardPreview.GlobalPosition.X;
                    var cy = _cardPreview.GlobalPosition.Y;
                    // Place tips to the right of the card preview, aligned with its top
                    tipSet.GlobalPosition = new Vector2(cx + 160, cy - 211);
                    _cardPreviewTipsOwner = source;
                }
            }
            catch (Exception ex) { Log.Error($"[SmartPick] ShowCardPreview tips: {ex.Message}"); }
        }
        catch (Exception ex)
        {
            Log.Error($"[SmartPick] ShowCardPreview: {ex.Message}\n{ex.StackTrace}");
        }
    }

    private void HideCardPreview()
    {
        foreach (var child in _cardPreview.GetChildren())
        {
            if (child is Control ctrl)
            {
                try { NHoverTipSet.Remove(ctrl); } catch { }
            }
            child.QueueFree();
        }
        _previewHolder = null;
        _cardPreview.Visible = false;
        if (_cardPreviewTipsOwner != null && GodotObject.IsInstanceValid(_cardPreviewTipsOwner))
        {
            try { NHoverTipSet.Remove(_cardPreviewTipsOwner); } catch { }
        }
        _cardPreviewTipsOwner = null;
    }

    private void OnItemDroppedToTier(string itemId, TierItemIcon.Type itemType, string character, string tier, int insertAt)
    {
        Log.Info($"[SmartPick] DROP: {itemId} → tier={tier} insertAt={insertAt}");
        if (itemType == TierItemIcon.Type.Card)
        {
            CaptureCurrentCardTier(character, tier);
            var cardName = CardBadgeOverlay.NormalizeCardIdPublic(itemId);
            Log.Info($"[SmartPick] DROP card: '{cardName}' → {tier}[{insertAt}]");
            TierData.SetCustomTier(character, cardName, tier, insertAt);

            // Log resulting order
            var custom = TierData.GetCustomTiersForCharacter(character);
            if (custom != null && custom.TryGetValue(tier, out var list))
                Log.Info($"[SmartPick] DROP result {tier}: [{string.Join(", ", list)}]");
        }
        else
        {
            CaptureCurrentRelicTier(tier);
            var relicName = RelicTierData.IdEntryToDisplayName(itemId);
            RelicTierData.SetCustomTier(relicName, tier, insertAt);
        }
        TierData.SaveCustomTiers();
        PopulateItems();
    }

    /// <summary>
    /// Capture all cards currently in a tier row into custom ordered list.
    /// Reads from the actual UI to preserve visual order.
    /// </summary>
    private void CaptureCurrentCardTier(string character, string tier)
    {
        if (!_tierRows.TryGetValue(tier, out var row)) return;

        // Collect card names in their current UI order
        var orderedNames = new List<string>();
        foreach (var child in row.GetFlowChildren())
        {
            if (child is TierItemIcon icon && icon.ItemType == TierItemIcon.Type.Card)
            {
                orderedNames.Add(TierData.NormalizeCardName(
                    CardBadgeOverlay.NormalizeCardIdPublic(icon.ItemId)));
            }
        }

        Log.Info($"[SmartPick] CAPTURE {tier}: {orderedNames.Count} cards: [{string.Join(", ", orderedNames.Take(5))}...]");
        if (orderedNames.Count > 0)
            TierData.SetCustomTierList(character, tier, orderedNames);
    }

    private void CaptureCurrentRelicTier(string tier)
    {
        if (!_tierRows.TryGetValue(tier, out var row)) return;

        var orderedNames = new List<string>();
        foreach (var child in row.GetFlowChildren())
        {
            if (child is TierItemIcon icon && icon.ItemType == TierItemIcon.Type.Relic)
            {
                orderedNames.Add(RelicTierData.IdEntryToDisplayName(icon.ItemId)
                    .Trim().ToLowerInvariant());
            }
        }

        if (orderedNames.Count > 0)
            RelicTierData.SetCustomTierList(tier, orderedNames);
    }

    private void OnItemRemovedFromTier(string itemId, TierItemIcon.Type itemType, string character)
    {
        if (itemType == TierItemIcon.Type.Card)
        {
            var cardName = CardBadgeOverlay.NormalizeCardIdPublic(itemId);
            TierData.RemoveCustomTier(character, cardName);
        }
        else
        {
            var relicName = RelicTierData.IdEntryToDisplayName(itemId);
            RelicTierData.RemoveCustomTier(relicName);
        }
        TierData.SaveCustomTiers();
        PopulateItems();
    }

    private void OnTabSelected(Tab tab)
    {
        _currentTab = tab;
        foreach (var btn in _tabButtons)
            btn.ButtonPressed = false;
        // Re-press the correct one
        int idx = tab == Tab.Cards ? 0 : 1;
        if (idx < _tabButtons.Count)
            _tabButtons[idx].ButtonPressed = true;

        if (_charFilter != null)
            _charFilter.Visible = tab == Tab.Cards;

        PopulateItems();
    }

    private void OnCharacterSelected(string character)
    {
        _currentCharacter = character;
        for (int i = 0; i < _charButtons.Count; i++)
            _charButtons[i].ButtonPressed = _characters[i].poolTitle == character;
        PopulateItems();
    }

    private void OnResetConfirm()
    {
        // Show confirmation dialog
        if (_importDialog != null && GodotObject.IsInstanceValid(_importDialog))
            _importDialog.QueueFree();

        var viewportSize = GetViewport().GetVisibleRect().Size;

        var overlay = new ColorRect();
        overlay.Color = new Color(0, 0, 0, 0.6f);
        overlay.Position = Vector2.Zero;
        overlay.Size = viewportSize;
        overlay.MouseFilter = MouseFilterEnum.Stop;

        var panel = new PanelContainer();
        var panelStyle = new StyleBoxFlat();
        panelStyle.BgColor = new Color(0.12f, 0.08f, 0.08f, 0.98f);
        panelStyle.CornerRadiusBottomLeft = 8;
        panelStyle.CornerRadiusBottomRight = 8;
        panelStyle.CornerRadiusTopLeft = 8;
        panelStyle.CornerRadiusTopRight = 8;
        panelStyle.BorderWidthBottom = 2;
        panelStyle.BorderWidthTop = 2;
        panelStyle.BorderWidthLeft = 2;
        panelStyle.BorderWidthRight = 2;
        panelStyle.BorderColor = new Color(1f, 0.4f, 0.3f);
        panelStyle.ContentMarginLeft = 30;
        panelStyle.ContentMarginRight = 30;
        panelStyle.ContentMarginTop = 20;
        panelStyle.ContentMarginBottom = 20;
        panel.AddThemeStyleboxOverride("panel", panelStyle);
        panel.Position = new Vector2(viewportSize.X / 2 - 200, viewportSize.Y / 2 - 80);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 15);

        var msg = new Label();
        msg.Text = "Reset all custom tiers to defaults?";
        msg.AddThemeFontSizeOverride("font_size", 20);
        msg.AddThemeColorOverride("font_color", new Color(1f, 0.85f, 0.7f));
        msg.HorizontalAlignment = HorizontalAlignment.Center;
        vbox.AddChild(msg);

        var warn = new Label();
        warn.Text = "This cannot be undone.";
        warn.AddThemeFontSizeOverride("font_size", 15);
        warn.AddThemeColorOverride("font_color", new Color(1f, 0.5f, 0.3f));
        warn.HorizontalAlignment = HorizontalAlignment.Center;
        vbox.AddChild(warn);

        var btnRow = new HBoxContainer();
        btnRow.AddThemeConstantOverride("separation", 20);
        btnRow.Alignment = BoxContainer.AlignmentMode.Center;

        var confirmBtn = CreateFooterButton("Reset", new Color(1f, 0.4f, 0.3f));
        confirmBtn.Pressed += () =>
        {
            TierData.ResetCustomTiers();
            RelicTierData.ResetCustomTiers();
            TierData.SaveCustomTiers();
            PopulateItems();
            overlay.QueueFree();
            _importDialog = null;
            ShowStatus("Reset to defaults!");
        };
        btnRow.AddChild(confirmBtn);

        var cancelBtn = CreateFooterButton("Cancel", new Color(0.8f, 0.8f, 0.8f));
        cancelBtn.Pressed += () =>
        {
            overlay.QueueFree();
            _importDialog = null;
        };
        btnRow.AddChild(cancelBtn);

        vbox.AddChild(btnRow);
        panel.AddChild(vbox);
        overlay.AddChild(panel);
        AddChild(overlay);
        _importDialog = overlay;
    }

    private void OnClearAllConfirm()
    {
        if (_importDialog != null && GodotObject.IsInstanceValid(_importDialog))
            _importDialog.QueueFree();

        var viewportSize = GetViewport().GetVisibleRect().Size;
        bool isCards = _currentTab == Tab.Cards;

        string targetName;
        if (isCards)
        {
            var charEntry = _characters.FirstOrDefault(c => c.poolTitle == _currentCharacter);
            targetName = charEntry.localizedName ?? _currentCharacter;
        }
        else
        {
            targetName = "relics";
        }

        var overlay = new ColorRect();
        overlay.Color = new Color(0, 0, 0, 0.6f);
        overlay.Position = Vector2.Zero;
        overlay.Size = viewportSize;
        overlay.MouseFilter = MouseFilterEnum.Stop;

        var panel = new PanelContainer();
        var panelStyle = new StyleBoxFlat();
        panelStyle.BgColor = new Color(0.12f, 0.1f, 0.06f, 0.98f);
        panelStyle.SetCornerRadiusAll(8);
        panelStyle.BorderWidthBottom = 2;
        panelStyle.BorderWidthTop = 2;
        panelStyle.BorderWidthLeft = 2;
        panelStyle.BorderWidthRight = 2;
        panelStyle.BorderColor = new Color(1f, 0.75f, 0.2f);
        panelStyle.ContentMarginLeft = 30;
        panelStyle.ContentMarginRight = 30;
        panelStyle.ContentMarginTop = 20;
        panelStyle.ContentMarginBottom = 20;
        panel.AddThemeStyleboxOverride("panel", panelStyle);
        panel.Position = new Vector2(viewportSize.X / 2 - 220, viewportSize.Y / 2 - 80);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 15);

        var msg = new Label();
        msg.Text = isCards
            ? $"Move all {targetName} cards to unassigned (?)?"
            : "Move all relics to unassigned (?)?";
        msg.AddThemeFontSizeOverride("font_size", 20);
        msg.AddThemeColorOverride("font_color", new Color(1f, 0.9f, 0.7f));
        msg.HorizontalAlignment = HorizontalAlignment.Center;
        vbox.AddChild(msg);

        var warn = new Label();
        warn.Text = "Items will lose their tier rankings.";
        warn.AddThemeFontSizeOverride("font_size", 15);
        warn.AddThemeColorOverride("font_color", new Color(1f, 0.75f, 0.2f));
        warn.HorizontalAlignment = HorizontalAlignment.Center;
        vbox.AddChild(warn);

        var btnRow = new HBoxContainer();
        btnRow.AddThemeConstantOverride("separation", 20);
        btnRow.Alignment = BoxContainer.AlignmentMode.Center;

        var confirmBtn = CreateFooterButton("Clear All", new Color(1f, 0.75f, 0.2f));
        confirmBtn.Pressed += () =>
        {
            if (isCards)
            {
                foreach (var tier in TierData.TierLetters)
                    TierData.SetCustomTierList(_currentCharacter, tier, new List<string>());

                var allCardNames = ModelDb.AllCards
                    .Where(c => c.Pool?.Title?.ToLowerInvariant() == _currentCharacter)
                    .Select(c => TierData.NormalizeCardName(CardBadgeOverlay.NormalizeCardIdPublic(c.Id.Entry)))
                    .ToList();
                TierData.SetCustomTierList(_currentCharacter, "?", allCardNames);
            }
            else
            {
                foreach (var tier in TierData.TierLetters)
                    RelicTierData.SetCustomTierList(tier, new List<string>());

                var allRelicNames = ModelDb.AllRelics
                    .Select(r => RelicTierData.IdEntryToDisplayName(r.Id.Entry).Trim().ToLowerInvariant())
                    .ToList();
                RelicTierData.SetCustomTierList("?", allRelicNames);
            }

            TierData.SaveCustomTiers();
            PopulateItems();
            overlay.QueueFree();
            _importDialog = null;
            ShowStatus(isCards ? $"All {targetName} cards moved to ?" : "All relics moved to ?");
        };
        btnRow.AddChild(confirmBtn);

        var cancelBtn = CreateFooterButton("Cancel", new Color(0.8f, 0.8f, 0.8f));
        cancelBtn.Pressed += () =>
        {
            overlay.QueueFree();
            _importDialog = null;
        };
        btnRow.AddChild(cancelBtn);

        vbox.AddChild(btnRow);
        panel.AddChild(vbox);
        overlay.AddChild(panel);
        AddChild(overlay);
        _importDialog = overlay;
    }

    public override void _GuiInput(InputEvent @event)
    {
        // Consume all GUI input to prevent clicks passing through to game screens below
        AcceptEvent();
    }

    public override void _Process(double delta)
    {
        // Update card preview position to follow cursor during drag
        if (_isDraggingCard && _cardPreview.Visible)
        {
            UpdatePreviewPosition();
        }
    }

    private void UpdatePreviewPosition()
    {
        var mousePos = GetViewport().GetMousePosition();
        var viewportSize = GetViewport().GetVisibleRect().Size;
        var halfW = 150f;
        var halfH = 211f;

        var cx = mousePos.X + 30 + halfW;
        var cy = mousePos.Y;

        if (cx + halfW > viewportSize.X) cx = mousePos.X - 30 - halfW;
        if (cx - halfW < 10) cx = halfW + 10;
        if (cy - halfH < 10) cy = halfH + 10;
        if (cy + halfH > viewportSize.Y - 10) cy = viewportSize.Y - halfH - 10;

        _cardPreview.GlobalPosition = new Vector2(cx, cy);

        // Update hover tips position too
        if (_previewHolder != null)
        {
            try
            {
                NHoverTipSet.Remove(_previewHolder);
            }
            catch { }
        }
    }

    private void OnExport()
    {
        try
        {
            // Export FULL current state (custom + built-in) for all characters and relics
            var cards = new Dictionary<string, Dictionary<string, List<string>>>();

            foreach (var (poolTitle, _) in _characters)
            {
                var charTiers = new Dictionary<string, List<string>>();
                var allCards = ModelDb.AllCards
                    .Where(c => c.Pool?.Title?.ToLowerInvariant() == poolTitle)
                    .ToList();

                foreach (var card in allCards)
                {
                    var cardName = CardBadgeOverlay.NormalizeCardIdPublic(card.Id.Entry);
                    var result = TierData.GetTiers(poolTitle, cardName);
                    var tier = result.Score >= 0 ? result.Tier : "?";

                    if (!charTiers.ContainsKey(tier))
                        charTiers[tier] = new();
                    charTiers[tier].Add(TierData.NormalizeCardName(cardName));
                }

                if (charTiers.Count > 0)
                    cards[poolTitle] = charTiers;
            }

            var relics = new Dictionary<string, List<string>>();
            foreach (var relic in ModelDb.AllRelics)
            {
                var relicName = RelicTierData.IdEntryToDisplayName(relic.Id.Entry);
                var result = RelicTierData.GetTier(relicName);
                var tier = result?.Tier ?? "?";

                if (!relics.ContainsKey(tier))
                    relics[tier] = new();
                relics[tier].Add(relicName.Trim().ToLowerInvariant());
            }

            // Order tiers S, A, B, C, D, ? for clean output
            var orderedCards = new Dictionary<string, Dictionary<string, List<string>>>();
            foreach (var (ch, tiers) in cards)
            {
                var ordered = new Dictionary<string, List<string>>();
                foreach (var t in TierData.AllTierLetters)
                    if (tiers.TryGetValue(t, out var list) && list.Count > 0)
                        ordered[t] = list;
                orderedCards[ch] = ordered;
            }
            var orderedRelics = new Dictionary<string, List<string>>();
            foreach (var t in TierData.AllTierLetters)
                if (relics.TryGetValue(t, out var list) && list.Count > 0)
                    orderedRelics[t] = list;

            var data = new Dictionary<string, object> { ["cards"] = orderedCards, ["relics"] = orderedRelics };
            var json = System.Text.Json.JsonSerializer.Serialize(data);
            DisplayServer.ClipboardSet(json);
            ShowStatus("Copied to clipboard!");
        }
        catch (Exception ex)
        {
            Log.Error($"[SmartPick] Export: {ex.Message}");
            ShowStatus("Export failed!");
        }
    }

    private Control? _importDialog;

    private void OnImport()
    {
        try
        {
            var json = DisplayServer.ClipboardGet();
            if (string.IsNullOrWhiteSpace(json))
            {
                ShowStatus("Clipboard is empty!");
                return;
            }

            System.Text.Json.JsonDocument doc;
            try { doc = System.Text.Json.JsonDocument.Parse(json); }
            catch
            {
                ShowStatus("Invalid JSON in clipboard!");
                return;
            }

            // Parse incoming data
            var importedCards = new Dictionary<string, Dictionary<string, List<string>>>();
            var importedRelics = new Dictionary<string, List<string>>();

            if (doc.RootElement.TryGetProperty("cards", out var cardsEl))
                importedCards = ParseImportedCards(cardsEl);
            if (doc.RootElement.TryGetProperty("relics", out var relicsEl))
            {
                foreach (var tierProp in relicsEl.EnumerateObject())
                {
                    var list = new List<string>();
                    if (tierProp.Value.ValueKind == System.Text.Json.JsonValueKind.Array)
                        foreach (var item in tierProp.Value.EnumerateArray())
                        {
                            var name = item.GetString();
                            if (!string.IsNullOrEmpty(name)) list.Add(name);
                        }
                    importedRelics[tierProp.Name] = list;
                }
            }

            // Build diff summary
            int totalCardChanges = importedCards.Sum(ch => ch.Value.Sum(t => t.Value.Count));
            int totalRelicChanges = importedRelics.Sum(t => t.Value.Count);
            int charCount = importedCards.Count;

            if (totalCardChanges == 0 && totalRelicChanges == 0)
            {
                ShowStatus("Nothing to import!");
                doc.Dispose();
                return;
            }

            ShowImportDialog(importedCards, importedRelics, totalCardChanges, totalRelicChanges, charCount, doc);
        }
        catch (Exception ex)
        {
            Log.Error($"[SmartPick] Import: {ex.Message}");
            ShowStatus("Import failed!");
        }
    }

    private static readonly Dictionary<string, Color> DiffTierColors = new()
    {
        ["S"] = new Color("ff8000"), ["A"] = new Color("a335ee"),
        ["B"] = new Color("0070dd"), ["C"] = new Color("1eff00"),
        ["D"] = new Color("9d9d9d"), ["?"] = new Color("666666"),
    };

    private record DiffEntry(string Name, string OldTier, string NewTier,
        IEnumerable<MegaCrit.Sts2.Core.HoverTips.IHoverTip>? HoverTips = null,
        CardModel? Card = null);

    private void ShowImportDialog(
        Dictionary<string, Dictionary<string, List<string>>> importedCards,
        Dictionary<string, List<string>> importedRelics,
        int totalCards, int totalRelics, int charCount,
        System.Text.Json.JsonDocument doc)
    {
        if (_importDialog != null && GodotObject.IsInstanceValid(_importDialog))
            _importDialog.QueueFree();

        // Build diff: only items whose tier actually changes
        var cardDiffs = new List<(string character, DiffEntry entry)>();
        foreach (var (character, tiers) in importedCards)
        {
            foreach (var (newTier, list) in tiers)
            {
                foreach (var cardKey in list)
                {
                    var currentResult = TierData.GetTiers(character, cardKey);
                    var oldTier = currentResult.Score >= 0 ? currentResult.Tier : "?";
                    if (oldTier != newTier)
                    {
                        string displayName = cardKey;
                        IEnumerable<MegaCrit.Sts2.Core.HoverTips.IHoverTip>? tips = null;
                        CardModel? foundCard = null;
                        try
                        {
                            foundCard = ModelDb.AllCards.FirstOrDefault(c =>
                                CardBadgeOverlay.NormalizeCardIdPublic(c.Id.Entry)
                                    .Equals(cardKey, StringComparison.OrdinalIgnoreCase));
                            if (foundCard != null)
                            {
                                displayName = foundCard.Title;
                                tips = foundCard.HoverTips;
                            }
                        }
                        catch { }
                        cardDiffs.Add((character, new DiffEntry(displayName, oldTier, newTier, tips, foundCard)));
                    }
                }
            }
        }

        var relicDiffs = new List<DiffEntry>();
        foreach (var (newTier, list) in importedRelics)
        {
            foreach (var relicKey in list)
            {
                var currentResult = RelicTierData.GetTier(relicKey);
                var oldTier = currentResult?.Tier ?? "?";
                if (oldTier != newTier)
                {
                    string displayName = relicKey;
                    IEnumerable<MegaCrit.Sts2.Core.HoverTips.IHoverTip>? tips = null;
                    try
                    {
                        var relicModel = ModelDb.AllRelics.FirstOrDefault(r =>
                            RelicTierData.IdEntryToDisplayName(r.Id.Entry)
                                .Equals(relicKey, StringComparison.OrdinalIgnoreCase));
                        if (relicModel != null)
                        {
                            displayName = relicModel.Title.GetFormattedText();
                            tips = relicModel.HoverTips;
                        }
                    }
                    catch { }
                    relicDiffs.Add(new DiffEntry(displayName, oldTier, newTier, tips));
                }
            }
        }

        if (cardDiffs.Count == 0 && relicDiffs.Count == 0)
        {
            ShowStatus("No changes to import!");
            doc.Dispose();
            return;
        }

        var viewportSize = GetViewport().GetVisibleRect().Size;

        // Dark overlay
        var overlay = new ColorRect();
        overlay.Color = new Color(0, 0, 0, 0.6f);
        overlay.Position = Vector2.Zero;
        overlay.Size = viewportSize;
        overlay.MouseFilter = MouseFilterEnum.Stop;

        // Dialog panel
        var panel = new PanelContainer();
        var panelStyle = new StyleBoxFlat();
        panelStyle.BgColor = new Color(0.1f, 0.08f, 0.15f, 0.98f);
        panelStyle.CornerRadiusBottomLeft = 8;
        panelStyle.CornerRadiusBottomRight = 8;
        panelStyle.CornerRadiusTopLeft = 8;
        panelStyle.CornerRadiusTopRight = 8;
        panelStyle.BorderWidthBottom = 2;
        panelStyle.BorderWidthTop = 2;
        panelStyle.BorderWidthLeft = 2;
        panelStyle.BorderWidthRight = 2;
        panelStyle.BorderColor = new Color(0.5f, 0.4f, 0.2f);
        panelStyle.ContentMarginLeft = 20;
        panelStyle.ContentMarginRight = 20;
        panelStyle.ContentMarginTop = 15;
        panelStyle.ContentMarginBottom = 15;
        panel.AddThemeStyleboxOverride("panel", panelStyle);

        var dialogWidth = Mathf.Min(700, viewportSize.X - 100);
        var dialogHeight = Mathf.Min(500, viewportSize.Y - 100);
        panel.CustomMinimumSize = new Vector2(dialogWidth, 0);
        panel.Position = new Vector2((viewportSize.X - dialogWidth) / 2, (viewportSize.Y - dialogHeight) / 2);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 10);

        // Title
        var title = new Label();
        title.AddThemeFontSizeOverride("font_size", 20);
        title.AddThemeColorOverride("font_color", new Color(1f, 0.85f, 0.5f));
        title.HorizontalAlignment = HorizontalAlignment.Center;
        vbox.AddChild(title);

        // Character/relic filter checkboxes
        var selectedChars = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var ch in importedCards.Keys) selectedChars[ch] = true;
        bool selectedRelics = relicDiffs.Count > 0;

        // Track nodes per character/relics for visibility toggling
        var charNodes = new Dictionary<string, List<Control>>(StringComparer.OrdinalIgnoreCase);
        var relicNodes = new List<Control>();

        // Build character filter row
        var grouped = cardDiffs.GroupBy(d => d.character).ToList();
        if (grouped.Count > 1 || (grouped.Count > 0 && relicDiffs.Count > 0))
        {
            var filterRow = new HFlowContainer();
            filterRow.AddThemeConstantOverride("h_separation", 10);
            filterRow.AddThemeConstantOverride("v_separation", 4);

            foreach (var group in grouped)
            {
                var charKey = group.Key;
                var charDisplayName = GetCharacterDisplayName(charKey);

                var cb = new CheckBox();
                cb.Text = $"{charDisplayName} ({group.Count()})";
                cb.ButtonPressed = true;
                cb.AddThemeFontSizeOverride("font_size", 14);
                cb.AddThemeColorOverride("font_color", new Color(0.9f, 0.85f, 0.7f));
                cb.Toggled += (pressed) =>
                {
                    selectedChars[charKey] = pressed;
                    if (charNodes.TryGetValue(charKey, out var nodes))
                        foreach (var n in nodes) n.Visible = pressed;
                    UpdateImportTitle(title, cardDiffs, relicDiffs, selectedChars, selectedRelics);
                };
                filterRow.AddChild(cb);
            }

            if (relicDiffs.Count > 0)
            {
                var relicCb = new CheckBox();
                relicCb.Text = $"Relics ({relicDiffs.Count})";
                relicCb.ButtonPressed = true;
                relicCb.AddThemeFontSizeOverride("font_size", 14);
                relicCb.AddThemeColorOverride("font_color", new Color(0.9f, 0.85f, 0.7f));
                relicCb.Toggled += (pressed) =>
                {
                    selectedRelics = pressed;
                    foreach (var n in relicNodes) n.Visible = pressed;
                    UpdateImportTitle(title, cardDiffs, relicDiffs, selectedChars, selectedRelics);
                };
                filterRow.AddChild(relicCb);
            }

            vbox.AddChild(filterRow);
        }

        UpdateImportTitle(title, cardDiffs, relicDiffs, selectedChars, selectedRelics);

        // Scrollable diff list
        var scroll = new ScrollContainer();
        scroll.CustomMinimumSize = new Vector2(0, dialogHeight - 150);
        scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
        vbox.AddChild(scroll);

        var diffList = new VBoxContainer();
        diffList.AddThemeConstantOverride("separation", 3);
        diffList.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.AddChild(diffList);

        // Card diffs grouped by character
        foreach (var group in grouped)
        {
            var charKey = group.Key;
            var nodes = new List<Control>();
            charNodes[charKey] = nodes;

            var charLabel = new Label();
            charLabel.Text = $"— {GetCharacterDisplayName(charKey)} —";
            charLabel.AddThemeFontSizeOverride("font_size", 15);
            charLabel.AddThemeColorOverride("font_color", new Color(0.8f, 0.75f, 0.6f));
            charLabel.HorizontalAlignment = HorizontalAlignment.Center;
            diffList.AddChild(charLabel);
            nodes.Add(charLabel);

            foreach (var (_, entry) in group)
            {
                var row = CreateDiffRow(entry);
                diffList.AddChild(row);
                nodes.Add(row);
            }
        }

        // Relic diffs
        if (relicDiffs.Count > 0)
        {
            var relicHeader = new Label();
            relicHeader.Text = "— Relics —";
            relicHeader.AddThemeFontSizeOverride("font_size", 15);
            relicHeader.AddThemeColorOverride("font_color", new Color(0.8f, 0.75f, 0.6f));
            relicHeader.HorizontalAlignment = HorizontalAlignment.Center;
            diffList.AddChild(relicHeader);
            relicNodes.Add(relicHeader);

            foreach (var entry in relicDiffs)
            {
                var row = CreateDiffRow(entry);
                diffList.AddChild(row);
                relicNodes.Add(row);
            }
        }

        // Buttons
        var btnRow = new HBoxContainer();
        btnRow.AddThemeConstantOverride("separation", 15);

        var confirmBtn = new Button();
        confirmBtn.Text = "Apply Changes";
        confirmBtn.CustomMinimumSize = new Vector2(160, 40);
        confirmBtn.AddThemeFontSizeOverride("font_size", 16);
        confirmBtn.Pressed += () =>
        {
            var filteredCards = importedCards
                .Where(kv => selectedChars.GetValueOrDefault(kv.Key, false))
                .ToDictionary(kv => kv.Key, kv => kv.Value);
            var filteredRelics = selectedRelics ? importedRelics : new();
            ApplyImport(filteredCards, filteredRelics);
            doc.Dispose();
            overlay.QueueFree();
            _importDialog = null;
        };
        btnRow.AddChild(confirmBtn);

        var cancelBtn = new Button();
        cancelBtn.Text = "Cancel";
        cancelBtn.CustomMinimumSize = new Vector2(120, 40);
        cancelBtn.AddThemeFontSizeOverride("font_size", 16);
        cancelBtn.Pressed += () =>
        {
            doc.Dispose();
            overlay.QueueFree();
            _importDialog = null;
        };
        btnRow.AddChild(cancelBtn);

        vbox.AddChild(btnRow);
        panel.AddChild(vbox);
        overlay.AddChild(panel);

        AddChild(overlay);
        _importDialog = overlay;
    }

    private Control CreateDiffRow(DiffEntry entry)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);

        // Item name with styled background + hover tips
        var namePanel = new PanelContainer();
        namePanel.CustomMinimumSize = new Vector2(200, 28);
        namePanel.MouseFilter = MouseFilterEnum.Stop;
        var nameStyle = new StyleBoxFlat();
        nameStyle.BgColor = new Color(0.15f, 0.13f, 0.2f, 0.9f);
        nameStyle.CornerRadiusBottomLeft = 4;
        nameStyle.CornerRadiusBottomRight = 4;
        nameStyle.CornerRadiusTopLeft = 4;
        nameStyle.CornerRadiusTopRight = 4;
        nameStyle.ContentMarginLeft = 6;
        nameStyle.ContentMarginRight = 6;
        var nameHoverStyle = new StyleBoxFlat();
        nameHoverStyle.BgColor = new Color(0.25f, 0.22f, 0.32f, 0.95f);
        nameHoverStyle.CornerRadiusBottomLeft = 4;
        nameHoverStyle.CornerRadiusBottomRight = 4;
        nameHoverStyle.CornerRadiusTopLeft = 4;
        nameHoverStyle.CornerRadiusTopRight = 4;
        nameHoverStyle.ContentMarginLeft = 6;
        nameHoverStyle.ContentMarginRight = 6;
        namePanel.AddThemeStyleboxOverride("panel", nameStyle);

        // Hover: card preview (left) + hover tips (right), or just tips for relics
        {
            var tips = entry.HoverTips?.ToList();
            var card = entry.Card;
            namePanel.MouseEntered += () =>
            {
                namePanel.AddThemeStyleboxOverride("panel", nameHoverStyle);
                if (card != null)
                {
                    ShowCardPreview(card, namePanel);
                    // Also show hover tips to the right of the card preview
                    if (tips != null)
                    {
                        try
                        {
                            var tipSet = NHoverTipSet.CreateAndShow(namePanel, tips);
                            // Position tips to the right of card preview
                            var cx = _cardPreview.GlobalPosition.X;
                            var cy = _cardPreview.GlobalPosition.Y;
                            tipSet.GlobalPosition = new Vector2(cx + 160, cy - 211);
                        }
                        catch { }
                    }
                }
                else if (tips != null)
                {
                    try
                    {
                        var tipSet = NHoverTipSet.CreateAndShow(namePanel, tips);
                        tipSet.GlobalPosition = new Vector2(
                            namePanel.GlobalPosition.X + namePanel.Size.X + 10,
                            namePanel.GlobalPosition.Y);
                    }
                    catch { }
                }
            };
            namePanel.MouseExited += () =>
            {
                namePanel.AddThemeStyleboxOverride("panel", nameStyle);
                try { NHoverTipSet.Remove(namePanel); } catch { }
                if (card != null)
                    HideCardPreview();
            };
        }

        var nameLabel = new Label();
        nameLabel.Text = entry.Name;
        nameLabel.AddThemeFontSizeOverride("font_size", 14);
        nameLabel.AddThemeColorOverride("font_color", new Color(0.9f, 0.85f, 0.75f));
        nameLabel.ClipText = true;
        nameLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        nameLabel.MouseFilter = MouseFilterEnum.Ignore;
        namePanel.AddChild(nameLabel);
        row.AddChild(namePanel);

        // Old tier badge
        row.AddChild(CreateTierBadge(entry.OldTier));

        // Arrow
        var arrow = new Label();
        arrow.Text = "→";
        arrow.AddThemeFontSizeOverride("font_size", 18);
        arrow.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.7f));
        row.AddChild(arrow);

        // New tier badge
        row.AddChild(CreateTierBadge(entry.NewTier));

        return row;
    }

    private Control CreateTierBadge(string tier)
    {
        var badge = new PanelContainer();
        badge.CustomMinimumSize = new Vector2(32, 28);
        var color = DiffTierColors.GetValueOrDefault(tier, DiffTierColors["?"]);
        var style = new StyleBoxFlat();
        style.BgColor = new Color(color, 0.3f);
        style.CornerRadiusBottomLeft = 4;
        style.CornerRadiusBottomRight = 4;
        style.CornerRadiusTopLeft = 4;
        style.CornerRadiusTopRight = 4;
        style.BorderWidthBottom = 1;
        style.BorderWidthTop = 1;
        style.BorderWidthLeft = 1;
        style.BorderWidthRight = 1;
        style.BorderColor = color;
        badge.AddThemeStyleboxOverride("panel", style);

        var label = new Label();
        label.Text = tier;
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.VerticalAlignment = VerticalAlignment.Center;
        label.AddThemeFontSizeOverride("font_size", 16);
        label.AddThemeColorOverride("font_color", color);
        label.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0));
        label.AddThemeConstantOverride("outline_size", 3);
        badge.AddChild(label);

        return badge;
    }

    /// <summary>
    /// Get localized display name for a character pool title.
    /// Works for real characters (ironclad, silent, etc.) and the colorless pseudo-character.
    /// </summary>
    private string GetCharacterDisplayName(string poolTitle)
    {
        // First check our _characters list (includes colorless with localized name)
        var entry = _characters.FirstOrDefault(c =>
            c.poolTitle.Equals(poolTitle, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(entry.localizedName))
            return entry.localizedName;

        // Fallback: look up in ModelDb directly
        try
        {
            var charModel = ModelDb.AllCharacters.FirstOrDefault(c =>
                c.CardPool?.Title?.ToLowerInvariant() == poolTitle.ToLowerInvariant());
            if (charModel != null) return charModel.Title.GetFormattedText();
        }
        catch { }
        return poolTitle;
    }

    private static void UpdateImportTitle(Label title,
        List<(string character, DiffEntry entry)> cardDiffs,
        List<DiffEntry> relicDiffs,
        Dictionary<string, bool> selectedChars, bool selectedRelics)
    {
        int cards = cardDiffs.Count(d => selectedChars.GetValueOrDefault(d.character, false));
        int relics = selectedRelics ? relicDiffs.Count : 0;
        title.Text = $"Import Changes ({cards} cards, {relics} relics)";
    }

    private void ApplyImport(
        Dictionary<string, Dictionary<string, List<string>>> importedCards,
        Dictionary<string, List<string>> importedRelics)
    {
        foreach (var (character, tiers) in importedCards)
        {
            foreach (var (tier, list) in tiers)
            {
                TierData.SetCustomTierList(character, tier, list);
            }
        }

        foreach (var (tier, list) in importedRelics)
        {
            RelicTierData.SetCustomTierList(tier, list);
        }

        TierData.SaveCustomTiers();
        PopulateItems();
        ShowStatus("Imported successfully!");
    }

    private static Dictionary<string, Dictionary<string, List<string>>> ParseImportedCards(System.Text.Json.JsonElement el)
    {
        var result = new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.OrdinalIgnoreCase);
        foreach (var charProp in el.EnumerateObject())
        {
            var tierMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var tierProp in charProp.Value.EnumerateObject())
            {
                var list = new List<string>();
                if (tierProp.Value.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var item in tierProp.Value.EnumerateArray())
                    {
                        var name = item.GetString();
                        if (!string.IsNullOrEmpty(name)) list.Add(name);
                    }
                }
                tierMap[tierProp.Name] = list;
            }
            result[charProp.Name] = tierMap;
        }
        return result;
    }

    private void ShowStatus(string message)
    {
        if (_statusLabel == null) return;
        _statusLabel.Text = message;
        // Clear after 3 seconds
        Task.Run(async () =>
        {
            await Task.Delay(3000);
            Callable.From(() =>
            {
                if (_statusLabel != null && GodotObject.IsInstanceValid(_statusLabel))
                    _statusLabel.Text = "";
            }).CallDeferred();
        });
    }

    private void OnClose()
    {
        _instance = null;
        QueueFree();
        Patches.TierEditorInstallPatch.SetOpenButtonVisible(true);

        // Refresh badges on any active screens
        RefreshActiveBadges();
    }

    private static void RefreshActiveBadges()
    {
        // Re-badge active card screens
        CardBadgeOverlay.ClearBadges();
        if (CardBadgeOverlay.ActiveMerchant != null
            && GodotObject.IsInstanceValid(CardBadgeOverlay.ActiveMerchant))
            CardBadgeOverlay.AttachBadgesDeferred(CardBadgeOverlay.ActiveMerchant);
        else if (CardBadgeOverlay.ActiveRewardScreen != null
            && GodotObject.IsInstanceValid(CardBadgeOverlay.ActiveRewardScreen))
            CardBadgeOverlay.AttachBadgesDeferred(CardBadgeOverlay.ActiveRewardScreen);
        else if (CardBadgeOverlay.ActiveGridSelection != null
            && GodotObject.IsInstanceValid(CardBadgeOverlay.ActiveGridSelection))
            CardBadgeOverlay.AttachBadgesDeferred(CardBadgeOverlay.ActiveGridSelection);

        // Re-badge active relic screens
        RelicBadgeOverlay.ClearBadges();
        if (CardBadgeOverlay.ActiveMerchant != null
            && GodotObject.IsInstanceValid(CardBadgeOverlay.ActiveMerchant))
            RelicBadgeOverlay.AttachBadgesDeferred(CardBadgeOverlay.ActiveMerchant);
    }

}
