using System.Reflection;
using System.Text.Json;
using MegaCrit.Sts2.Core.Logging;

namespace FirstMod;

/// <summary>
/// Loads embedded relic tier-list JSON and provides per-relic tier lookups.
/// Single source (Mobalytics), global tiers (not per-character).
/// Supports custom ordered tiers with position-based scoring.
/// </summary>
public static class RelicTierData
{
    // relic name (normalized) → tier letter
    private static Dictionary<string, string> _index = new();

    // Custom tiers: tier → ordered list of relic names
    private static Dictionary<string, List<string>> _customOrdered = new();

    private static bool _initialized;

    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        try
        {
            _index = LoadEmbeddedTierList("mobalytics_relics.json");
            Log.Info($"[SmartPick] RelicTierData: loaded {_index.Count} relics");
        }
        catch (Exception ex) { Log.Error($"[SmartPick] RelicTierData load: {ex.Message}"); }
    }

    public record RelicTierResult(string Tier, int Score);

    public static RelicTierResult? GetTier(string relicName)
    {
        if (string.IsNullOrEmpty(relicName)) return null;

        var key = NormalizeName(relicName);

        // Custom ordered tier takes priority
        var customResult = LookupCustomTier(key);
        if (customResult != null) return customResult;

        if (_index.TryGetValue(key, out var tier))
            return new RelicTierResult(tier, TierData.TierScore.GetValueOrDefault(tier, 50));

        // Fuzzy match
        var collapsed = CollapseKey(key);
        foreach (var (k, v) in _index)
        {
            if (CollapseKey(k) == collapsed)
                return new RelicTierResult(v, TierData.TierScore.GetValueOrDefault(v, 50));
        }

        return null;
    }

    private static RelicTierResult? LookupCustomTier(string key)
    {
        foreach (var (tier, list) in _customOrdered)
        {
            var idx = list.FindIndex(r => r.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (idx < 0)
            {
                var collapsed = CollapseKey(key);
                for (int i = 0; i < list.Count; i++)
                {
                    if (CollapseKey(list[i]) == collapsed) { idx = i; break; }
                }
            }
            if (idx >= 0)
            {
                var score = TierData.CalculatePositionScore(tier, idx, list.Count);
                return new RelicTierResult(tier, score);
            }
        }
        return null;
    }

    public static void SetCustomTier(string relicName, string tier, int insertAt = -1)
    {
        var key = NormalizeName(relicName);
        RemoveCustomTier(relicName);

        if (!_customOrdered.ContainsKey(tier))
            _customOrdered[tier] = new();

        var list = _customOrdered[tier];
        if (insertAt >= 0 && insertAt < list.Count)
            list.Insert(insertAt, key);
        else
            list.Add(key);
    }

    public static void SetCustomTierList(string tier, List<string> orderedRelics)
    {
        _customOrdered[tier] = orderedRelics;
    }

    public static void RemoveCustomTier(string relicName)
    {
        var key = NormalizeName(relicName);
        foreach (var (_, list) in _customOrdered)
            list.RemoveAll(r => r.Equals(key, StringComparison.OrdinalIgnoreCase));
    }

    public static Dictionary<string, List<string>> GetCustomOrdered() => _customOrdered;

    public static void ResetCustomTiers()
    {
        _customOrdered.Clear();
    }

    public static void LoadCustomFromJson(JsonElement el)
    {
        _customOrdered.Clear();
        foreach (var tierProp in el.EnumerateObject())
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
            _customOrdered[tierProp.Name] = list;
        }
        Log.Info($"[SmartPick] Custom relic tiers loaded ({_customOrdered.Sum(t => t.Value.Count)} relics)");
    }

    private static string NormalizeName(string name)
    {
        return name.Trim().ToLowerInvariant();
    }

    public static string IdEntryToDisplayName(string entry)
    {
        var words = entry.Split('_', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < words.Length; i++)
        {
            if (words[i].Length > 0)
                words[i] = char.ToUpper(words[i][0]) + words[i].Substring(1).ToLower();
        }
        return string.Join(" ", words);
    }

    private static string CollapseKey(string s)
    {
        // Tier lists say "The Chosen Cheese" while the game id is CHOSEN_CHEESE.
        s = s.Trim();
        if (s.StartsWith("the ", StringComparison.OrdinalIgnoreCase))
            s = s.Substring(4);
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    private static Dictionary<string, string> LoadEmbeddedTierList(string resourceName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"Embedded resource '{resourceName}' not found");

        using var doc = JsonDocument.Parse(stream);
        var tiers = doc.RootElement.GetProperty("tiers");

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var tierProp in tiers.EnumerateObject())
        {
            var tierLetter = tierProp.Name;
            foreach (var relic in tierProp.Value.EnumerateArray())
            {
                var relicName = relic.GetString();
                if (!string.IsNullOrEmpty(relicName))
                    result[relicName.ToLowerInvariant()] = tierLetter;
            }
        }

        return result;
    }
}
