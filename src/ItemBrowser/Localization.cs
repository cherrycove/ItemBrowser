using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace ItemBrowser;

internal static class Localization
{
    private static readonly Dictionary<string, List<string>> _localizedTextTable = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> _itemNameKeyMap = new(StringComparer.OrdinalIgnoreCase);
    private static bool _itemNameKeyMapInitialized;
    private static int _lastRenderedLanguageIndex = -1;

    internal static string GetLocalizedItemName(Item item)
    {
        if (item == null)
        {
            return string.Empty;
        }

        EnsureItemNameKeyMap();

        string rawName = item.name ?? string.Empty;
        if (ContainsNonAscii(rawName))
        {
            return rawName.Trim();
        }

        string normalizedName = NormalizeItemNameForMap(rawName);
        if (!string.IsNullOrEmpty(normalizedName) && _itemNameKeyMap.TryGetValue(normalizedName, out string mappedKey))
        {
            return ResolveLocalizedText(mappedKey);
        }

        return string.Empty;
    }

    private static void EnsureItemNameKeyMap()
    {
        if (_itemNameKeyMapInitialized)
        {
            return;
        }

        LoadItemNameKeyMap();
        _itemNameKeyMapInitialized = true;
    }

    private static void LoadItemNameKeyMap()
    {
        _itemNameKeyMap.Clear();

        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ItemNameKeyMap.json");
            if (stream == null)
            {
                Plugin.Log.LogWarning("[ItemBrowser] Embedded ItemNameKeyMap.json not found.");
                return;
            }

            using var reader = new StreamReader(stream);
            string json = reader.ReadToEnd();
            var data = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
            if (data == null)
            {
                return;
            }

            foreach (var kvp in data)
            {
                if (!string.IsNullOrWhiteSpace(kvp.Key) && !string.IsNullOrWhiteSpace(kvp.Value))
                {
                    _itemNameKeyMap[kvp.Key] = kvp.Value;
                }
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[ItemBrowser] Failed to load embedded ItemNameKeyMap.json: {e.GetType().Name} {e.Message}");
        }
    }

    internal static string NormalizeItemNameForMap(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        string normalized = StripLocPrefix(name.Trim());
        normalized = normalized.Replace("(Clone)", "").Trim();
        return normalized;
    }

    private static bool ContainsNonAscii(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] > 127)
            {
                return true;
            }
        }

        return false;
    }

    private static string StripLocPrefix(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return string.Empty;
        }

        const string prefix = "LOC:";
        if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return key.Substring(prefix.Length).Trim();
        }

        return key;
    }

    internal static string ResolveLocalizedText(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return string.Empty;
        }

        try
        {
            string normalizedKey = StripLocPrefix(key.Trim());
            if (string.IsNullOrEmpty(normalizedKey))
            {
                return string.Empty;
            }

            string localized = LocalizedText.GetText(normalizedKey);
            if (string.IsNullOrEmpty(localized))
            {
                return string.Empty;
            }

            string normalizedLocalized = localized.Trim();
            if (normalizedLocalized.StartsWith("LOC:", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            if (string.Equals(normalizedLocalized, normalizedKey, StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            return normalizedLocalized;
        }
        catch
        {
            return string.Empty;
        }
    }

    internal static string GetText(string key, params string[] args)
    {
        string localizationKey = $"{GetLocalizationPrefix()}_{key}".ToUpperInvariant();
        string text = LocalizedText.GetText(localizationKey);
        if (IsMissingLocalization(text, localizationKey))
        {
            EnsureLocalizedTextInjected();
            text = LocalizedText.GetText(localizationKey);
        }

        if (IsMissingLocalization(text, localizationKey))
        {
            text = GetCachedLocalizedText(key);
        }

        if (string.IsNullOrEmpty(text))
        {
            text = key;
        }

        return string.Format(text, args);
    }

    internal static void LoadLocalizedText()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Localized_Text.json");
            if (stream == null)
            {
                Plugin.Log.LogWarning("[ItemBrowser] Embedded Localized_Text.json not found.");
                return;
            }

            using var reader = new StreamReader(stream);
            string json = reader.ReadToEnd();
            var table = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, List<string>>>(json);
            if (table == null)
            {
                Plugin.Log.LogWarning("[ItemBrowser] Localized_Text.json deserialized to null.");
                return;
            }

            _localizedTextTable.Clear();
            foreach (var item in table)
            {
                if (string.IsNullOrWhiteSpace(item.Key))
                {
                    continue;
                }

                var rawValues = item.Value;
                if (rawValues == null || rawValues.Count == 0)
                {
                    _localizedTextTable[item.Key] = new List<string> { item.Key };
                    continue;
                }

                string firstValue = rawValues.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? item.Key;
                var values = rawValues.Select(x => string.IsNullOrWhiteSpace(x) ? firstValue : x).ToList();
                if (values.Count == 0)
                {
                    values.Add(firstValue);
                }

                _localizedTextTable[item.Key] = values;
            }

            EnsureLocalizedTextInjected();
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[ItemBrowser] Failed to load Localized_Text.json: {e.GetType().Name} {e.Message}");
        }
    }

    private static string GetLocalizationPrefix()
    {
        return $"Mod_{Plugin.Name}";
    }

    internal static void EnsureLocalizedTextInjected()
    {
        if (_localizedTextTable.Count == 0) return;

        if (LocalizedText.MAIN_TABLE == null)
        {
            Plugin.Log.LogWarning("[ItemBrowser] LocalizedText.MAIN_TABLE is null. Skip localization injection.");
            return;
        }

        string prefix = GetLocalizationPrefix();
        foreach (var item in _localizedTextTable)
        {
            if (string.IsNullOrWhiteSpace(item.Key) || item.Value == null || item.Value.Count == 0)
            {
                continue;
            }

            string localizedKey = $"{prefix}_{item.Key}".ToUpperInvariant();
            LocalizedText.MAIN_TABLE[localizedKey] = item.Value;
        }

    }

    private static bool IsMissingLocalization(string? text, string key)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;

        string normalized = text.Trim();
        if (normalized.StartsWith("LOC:", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(normalized, key, StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    private static string GetCachedLocalizedText(string key)
    {
        if (!_localizedTextTable.TryGetValue(key, out var values) || values == null || values.Count == 0)
        {
            return string.Empty;
        }

        int index = GetCurrentLanguageIndex();
        if (index < 0 || index >= values.Count)
        {
            index = 0;
        }

        string? value = values[index];
        if (string.IsNullOrWhiteSpace(value))
        {
            value = values[0];
        }

        return value?.Trim() ?? string.Empty;
    }

    internal static int GetCurrentLanguageIndex()
    {
        try
        {
            Type type = typeof(LocalizedText);
            string[] candidates =
            {
                "CurrentLanguageIndex",
                "CurrentLanguage",
                "LanguageIndex",
                "Language",
                "currentLanguageIndex",
                "currentLanguage"
            };

            foreach (string name in candidates)
            {
                var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (prop == null) continue;
                object? value = prop.GetValue(null);
                if (TryConvertLanguageIndex(value, out int index))
                {
                    return NormalizeLanguageIndex(index);
                }
            }

            foreach (string name in candidates)
            {
                var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (field == null) continue;
                object? value = field.GetValue(null);
                if (TryConvertLanguageIndex(value, out int index))
                {
                    return NormalizeLanguageIndex(index);
                }
            }
        }
        catch
        {
            // ignore
        }

        return 0;
    }

    private static int NormalizeLanguageIndex(int index)
    {
        return index < 0 ? 0 : index;
    }

    private static bool TryConvertLanguageIndex(object? value, out int index)
    {
        index = 0;
        if (value == null) return false;

        if (value is int intValue)
        {
            index = intValue;
            return true;
        }

        if (value is Enum enumValue)
        {
            index = Convert.ToInt32(enumValue);
            return true;
        }

        if (value is byte byteValue)
        {
            index = byteValue;
            return true;
        }

        if (value is short shortValue)
        {
            index = shortValue;
            return true;
        }

        if (value is long longValue)
        {
            index = (int)longValue;
            return true;
        }

        return false;
    }

    internal static string GetTextOrFallback(string key, string fallback)
    {
        string text = GetText(key);
        if (string.IsNullOrWhiteSpace(text) || string.Equals(text, key, StringComparison.OrdinalIgnoreCase))
        {
            return fallback;
        }

        return text;
    }

    internal static string BuildLanguageMarker()
    {
        return $"{GetText("TITLE")}|{GetText("SEARCH_PLACEHOLDER")}";
    }

    internal static int LastRenderedLanguageIndex
    {
        get => _lastRenderedLanguageIndex;
        set => _lastRenderedLanguageIndex = value;
    }
}
