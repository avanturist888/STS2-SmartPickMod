using System.Reflection;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Logging;

namespace FirstMod;

/// <summary>
/// Loads embedded tier-list JSON files and provides per-card tier lookups.
/// Source: Mobalytics.
/// Supports custom tier overrides with position-based scoring.
/// </summary>
public static class TierData
{
    // character (lowercase) → card name (lowercase) → tier letter
    private static Dictionary<string, Dictionary<string, string>> _mobaIndex = new();

    // Custom tiers: character → tier → ordered list of card names
    private static Dictionary<string, Dictionary<string, List<string>>> _customOrdered = new();

    public static readonly Dictionary<string, int> TierScore = new()
    {
        ["S"] = 92, ["A"] = 78, ["B"] = 64, ["C"] = 50, ["D"] = 36, ["F"] = 22,
    };

    // Score ranges per tier: (max, min) — first item gets max, last gets min
    public static readonly Dictionary<string, (int max, int min)> TierRange = new()
    {
        ["S"] = (100, 85),
        ["A"] = (84, 71),
        ["B"] = (70, 57),
        ["C"] = (56, 43),
        ["D"] = (42, 29),
    };

    public static readonly string[] TierLetters = ["S", "A", "B", "C", "D"];
    public static readonly string[] AllTierLetters = ["S", "A", "B", "C", "D", "?"];

    private static bool _initialized;
    private static string? _customTiersPath;

    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        try
        {
            _mobaIndex = LoadEmbeddedTierList("mobalytics_cards.json");
            Log.Info($"[SmartPick] TierData: loaded Mobalytics ({_mobaIndex.Count} characters)");
        }
        catch (Exception ex) { Log.Error($"[SmartPick] TierData mobalytics load: {ex.Message}"); }

        LoadCustomTiers();
    }

    public record TierResult(string Tier, int Score);

    public static TierResult GetTiers(string character, string cardName)
    {
        var charKey = NormalizeCharacter(character);
        var cardKey = NormalizeCardName(cardName);

        // Custom tier takes priority — with position-based score
        var customResult = LookupCustomTier(charKey, cardKey);
        if (customResult != null)
            return customResult;

        // Multiplayer cards are rated in a separate "colorless" list, but some of them
        // live in character pools (e.g. Tank is an Ironclad card).
        var mobaTier = LookupTier(_mobaIndex, charKey, cardKey) ?? LookupTier(_mobaIndex, "colorless", cardKey);

        // Fallback: if character unknown, search all characters
        if (mobaTier == null && (string.IsNullOrEmpty(charKey) || charKey == "unknown"))
        {
            foreach (var ch in _mobaIndex.Keys)
            {
                mobaTier = LookupTier(_mobaIndex, ch, cardKey);
                if (mobaTier != null) break;
            }
        }

        var score = mobaTier != null ? TierScore.GetValueOrDefault(mobaTier, 50) : -1;
        return new TierResult(ScoreToTier(score), score);
    }

    /// <summary>Look up custom tier with position-based scoring.</summary>
    private static TierResult? LookupCustomTier(string charKey, string cardKey)
    {
        if (!_customOrdered.TryGetValue(charKey, out var tiers)) return null;

        foreach (var (tier, list) in tiers)
        {
            var idx = list.FindIndex(c => c.Equals(cardKey, StringComparison.OrdinalIgnoreCase));
            if (idx < 0)
            {
                // Fuzzy match
                var collapsed = CollapseKey(cardKey);
                for (int i = 0; i < list.Count; i++)
                {
                    if (CollapseKey(list[i]) == collapsed) { idx = i; break; }
                }
            }
            if (idx >= 0)
            {
                var score = CalculatePositionScore(tier, idx, list.Count);
                return new TierResult(tier, score);
            }
        }
        return null;
    }

    /// <summary>Calculate score based on position within a tier.</summary>
    public static int CalculatePositionScore(string tier, int position, int totalInTier)
    {
        if (!TierRange.TryGetValue(tier, out var range))
            return TierScore.GetValueOrDefault(tier, 50);

        if (totalInTier <= 1) return range.max;
        // Linear interpolation: first = max, last = min
        return range.max - (int)((float)position / (totalInTier - 1) * (range.max - range.min));
    }

    public static string ScoreToTier(int score) => score switch
    {
        >= 85 => "S",
        >= 71 => "A",
        >= 57 => "B",
        >= 43 => "C",
        >= 29 => "D",
        >= 0 => "F",
        _ => "?",
    };

    /// <summary>Set a custom tier for a card. Removes from previous tier first.</summary>
    public static void SetCustomTier(string character, string cardName, string tier, int insertAt = -1)
    {
        var charKey = NormalizeCharacter(character);
        var cardKey = NormalizeCardName(cardName);

        // Remove from any existing tier first
        RemoveCustomTier(character, cardName);

        EnsureCustomList(charKey, tier);
        var list = _customOrdered[charKey][tier];
        if (insertAt >= 0 && insertAt < list.Count)
            list.Insert(insertAt, cardKey);
        else
            list.Add(cardKey);
    }

    /// <summary>Set the entire ordered list for a tier (used by capture).</summary>
    public static void SetCustomTierList(string character, string tier, List<string> orderedCards)
    {
        var charKey = NormalizeCharacter(character);
        if (!_customOrdered.ContainsKey(charKey))
            _customOrdered[charKey] = new();
        _customOrdered[charKey][tier] = orderedCards;
    }

    private static void EnsureCustomList(string charKey, string tier)
    {
        if (!_customOrdered.ContainsKey(charKey))
            _customOrdered[charKey] = new();
        if (!_customOrdered[charKey].ContainsKey(tier))
            _customOrdered[charKey][tier] = new();
    }

    /// <summary>Remove a custom tier override for a card.</summary>
    public static void RemoveCustomTier(string character, string cardName)
    {
        var charKey = NormalizeCharacter(character);
        var cardKey = NormalizeCardName(cardName);
        if (!_customOrdered.TryGetValue(charKey, out var tiers)) return;

        foreach (var (_, list) in tiers)
        {
            list.RemoveAll(c => c.Equals(cardKey, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Get ordered custom tiers for a character (for UI display).</summary>
    public static Dictionary<string, List<string>>? GetCustomTiersForCharacter(string character)
    {
        var charKey = NormalizeCharacter(character);
        return _customOrdered.TryGetValue(charKey, out var tiers) ? tiers : null;
    }

    /// <summary>Get the full custom ordered index (for saving).</summary>
    public static Dictionary<string, Dictionary<string, List<string>>> GetCustomOrdered() => _customOrdered;

    /// <summary>Reset all custom tiers to empty.</summary>
    public static void ResetCustomTiers()
    {
        _customOrdered.Clear();
    }

    public static void SaveCustomTiers()
    {
        try
        {
            _customTiersPath ??= ProjectSettings.GlobalizePath("user://smartpick_custom_tiers.json");
            var data = new Dictionary<string, object>
            {
                ["cards"] = _customOrdered,
                ["relics"] = RelicTierData.GetCustomOrdered()
            };
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_customTiersPath, json);
            Log.Info($"[SmartPick] Custom tiers saved to {_customTiersPath}");
        }
        catch (Exception ex) { Log.Error($"[SmartPick] SaveCustomTiers: {ex.Message}"); }
    }

    public static void LoadCustomTiers()
    {
        try
        {
            _customTiersPath ??= ProjectSettings.GlobalizePath("user://smartpick_custom_tiers.json");
            if (!File.Exists(_customTiersPath)) return;

            var json = File.ReadAllText(_customTiersPath);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("cards", out var cardsEl))
            {
                _customOrdered = ParseOrderedIndex(cardsEl);
                Log.Info($"[SmartPick] Custom card tiers loaded ({_customOrdered.Count} characters)");
            }

            if (doc.RootElement.TryGetProperty("relics", out var relicsEl))
            {
                RelicTierData.LoadCustomFromJson(relicsEl);
            }
        }
        catch (Exception ex) { Log.Error($"[SmartPick] LoadCustomTiers: {ex.Message}"); }
    }

    private static Dictionary<string, Dictionary<string, List<string>>> ParseOrderedIndex(JsonElement el)
    {
        var result = new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.OrdinalIgnoreCase);
        foreach (var charProp in el.EnumerateObject())
        {
            var tierMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var tierProp in charProp.Value.EnumerateObject())
            {
                var list = new List<string>();
                if (tierProp.Value.ValueKind == JsonValueKind.Array)
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

    private static string? LookupTier(Dictionary<string, Dictionary<string, string>> index, string charKey, string cardKey)
    {
        if (!index.TryGetValue(charKey, out var cards)) return null;
        if (cards.TryGetValue(cardKey, out var tier)) return tier;
        var collapsed = CollapseKey(cardKey);
        foreach (var (k, v) in cards)
        {
            if (CollapseKey(k) == collapsed) return v;
        }
        return null;
    }

    public static string NormalizeCardName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        var s = name.TrimEnd('+').Trim();
        return s.ToLowerInvariant();
    }

    public static string NormalizeCharacter(string character)
    {
        if (string.IsNullOrEmpty(character)) return "";
        var s = character;
        var parenIdx = s.IndexOf('(');
        if (parenIdx > 0) s = s.Substring(0, parenIdx).Trim();
        var dotIdx = s.LastIndexOf('.');
        if (dotIdx >= 0) s = s.Substring(dotIdx + 1);
        s = s.Replace("Player", "").Trim();
        return s.ToLowerInvariant();
    }

    private static string CollapseKey(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    private static Dictionary<string, Dictionary<string, string>> LoadEmbeddedTierList(string resourceName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"Embedded resource '{resourceName}' not found");

        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;
        var characters = root.GetProperty("characters");

        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var charProp in characters.EnumerateObject())
        {
            var charName = charProp.Name.ToLowerInvariant();
            var cardMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var tierProp in charProp.Value.EnumerateObject())
            {
                var tierLetter = tierProp.Name;
                foreach (var card in tierProp.Value.EnumerateArray())
                {
                    var cardName = card.GetString();
                    if (!string.IsNullOrEmpty(cardName))
                        cardMap[cardName.ToLowerInvariant()] = tierLetter;
                }
            }

            result[charName] = cardMap;
        }

        return result;
    }
}
