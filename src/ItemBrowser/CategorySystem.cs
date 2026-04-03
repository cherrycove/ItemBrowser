using System;
using System.Collections.Generic;
using System.Linq;

namespace ItemBrowser;

internal static class CategorySystem
{
    private static readonly WikiCategoryGroup[] _wikiCategoryGroups = BuildWikiCategoryGroups();
    private static readonly Dictionary<string, ItemCategory> _wikiOverrides = BuildWikiCategoryOverrides();
    private static readonly Dictionary<string, int> _wikiOrder = BuildWikiCategoryOrder();
    private static readonly HashSet<string> _hiddenPrefabNames = BuildHiddenPrefabNameSet();

    internal static bool ShouldHideItemFromBrowser(Item item)
    {
        if (item == null)
        {
            return true;
        }

        string prefabName = item.name ?? string.Empty;
        if (string.IsNullOrWhiteSpace(prefabName))
        {
            return true;
        }

        string normalized = NormalizeCategoryKey(prefabName);
        if (_hiddenPrefabNames.Contains(normalized))
        {
            return true;
        }

        if (prefabName.StartsWith("C_", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (prefabName.Contains("_Prop", StringComparison.OrdinalIgnoreCase)
            || prefabName.Contains("_TEMP", StringComparison.OrdinalIgnoreCase)
            || prefabName.Contains("_UNUSED", StringComparison.OrdinalIgnoreCase)
            || prefabName.Contains("_Hidden", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static HashSet<string> BuildHiddenPrefabNameSet()
    {
        return new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            NormalizeCategoryKey("Lollipop_Prop"),
            NormalizeCategoryKey("Lollipop_Prop Variant"),
            NormalizeCategoryKey("FortifiedMilk_TEMP"),
            NormalizeCategoryKey("Clusterberry_UNUSED"),
            NormalizeCategoryKey("Mandrake_Hidden"),
            NormalizeCategoryKey("BingBong_Prop Variant"),
            NormalizeCategoryKey("Binoculars_Prop"),
            NormalizeCategoryKey("Bugle_Prop Variant"),
            NormalizeCategoryKey("Berrynana Peel Blue Variant"),
            NormalizeCategoryKey("Berrynana Peel Brown Variant"),
            NormalizeCategoryKey("Berrynana Peel Pink Variant"),
            NormalizeCategoryKey("Berrynana Peel Yellow"),
            NormalizeCategoryKey("GuidebookPage_4_BodyHeat Variant"),
            NormalizeCategoryKey("GuidebookPage_5_Sleepy Variant"),
            NormalizeCategoryKey("GuidebookPage_6_Awake Variant"),
            NormalizeCategoryKey("GuidebookPage_7_Crashout Variant"),
            NormalizeCategoryKey("Parasol_Roots Variant"),
            NormalizeCategoryKey("ClimbingChalk"),
            NormalizeCategoryKey("Skull")
        };
    }

    internal static ItemCategory GetCategory(Item item, string displayName)
    {
        if (item == null)
        {
            return ItemCategory.MiscEquipment;
        }

        if (TryGetCategoryOverride(item, displayName, out ItemCategory mappedCategory))
        {
            return mappedCategory;
        }

        WarnFallbackCategory(item, displayName);
        return ItemCategory.MiscEquipment;
    }


    private static bool TryGetCategoryOverride(Item item, string displayName, out ItemCategory category)
    {
        category = default;
        if (item == null)
        {
            return false;
        }

        List<string> candidateKeys = GetCategoryCandidateKeys(item, displayName)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        for (int i = 0; i < candidateKeys.Count; i++)
        {
            string key = candidateKeys[i];
            if (_wikiOverrides.TryGetValue(key, out category))
            {
                Plugin.VerboseLog($"Category override hit: key='{key}' -> {category} (prefab='{item.name}', display='{displayName}')");
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> GetCategoryCandidateKeys(Item item, string displayName)
    {
        string prefabName = item.name ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(prefabName))
        {
            yield return NormalizeCategoryKey(prefabName);

            int variantIndex = prefabName.IndexOf(" Variant", StringComparison.OrdinalIgnoreCase);
            if (variantIndex > 0)
            {
                yield return NormalizeCategoryKey(prefabName.Substring(0, variantIndex));
            }
        }

        if (!string.IsNullOrWhiteSpace(displayName))
        {
            yield return NormalizeCategoryKey(displayName);
        }
    }

    private static WikiCategoryGroup[] BuildWikiCategoryGroups()
    {
        return new[]
        {
            new WikiCategoryGroup(ItemCategory.NaturalFood, new[]
            {
                "Item_Coconut", "Item_Coconut_half", "Apple Berry Red", "Apple Berry Yellow", "Apple Berry Green",
                "Item_Honeycomb", "Pepper Berry", "Kingberry Purple", "Kingberry Yellow", "Kingberry Green",
                "Berrynana Pink", "Berrynana Blue", "Berrynana Brown",
                "Clusterberry Yellow", "Clusterberry Red", "Clusterberry Black",
                "Winterberry Orange", "Winterberry Yellow",
                "Prickleberry_Red", "Prickleberry_Gold",
                "MedicinalRoot", "Mandrake", "Marshmallow", "Glizzy", "Egg", "EggTurkey", "Bugfix", "Scorpion",
                "Napberry", "Shroomberry_Red", "Shroomberry_Yellow", "Shroomberry_Green", "Shroomberry_Blue", "Shroomberry_Purple"
            }),
            new WikiCategoryGroup(ItemCategory.MysticalFood, new[]
            {
                "Cure-All", "PandorasBox"
            }),
            new WikiCategoryGroup(ItemCategory.PackagedFood, new[]
            {
                "Airplane Food", "Lollipop", "Energy Drink", "FortifiedMilk", "Granola Bar", "ScoutCookies", "Sports Drink", "TrailMix"
            }),
            new WikiCategoryGroup(ItemCategory.Mushroom, new[]
            {
                "Mushroom Lace", "Mushroom Lace Poison", "Mushroom Normie", "Mushroom Normie Poison", "Mushroom Chubby", "Mushroom Cluster", "Mushroom Cluster Poison"
            }),
            new WikiCategoryGroup(ItemCategory.Consumable, new[]
            {
                "Antidote", "Balloon", "BalloonBunch", "Bandages", "HealingDart Variant", "FirstAidKit", "Flare", "Heat Pack", "HealingPuffShroom", "RescueHook", "GuidebookPageScroll Variant", "Sunscreen"
            }),
            new WikiCategoryGroup(ItemCategory.Deployable, new[]
            {
                "BounceShroom", "ChainShooter", "Flag_Plantable_Checkpoint", "CloudFungus", "MagicBean", "ClimbingSpike", "PortableStovetopItem", "RopeShooter", "RopeSpool", "ScoutCannonItem", "ShelfShroom"
            }),
            new WikiCategoryGroup(ItemCategory.MiscEquipment, new[]
            {
                "Backpack", "BingBong", "Binoculars", "Bugle", "Compass", "Frisbee", "Guidebook", "Lantern", "Parasol", "Pirate Compass", "Torch"
            }),
            new WikiCategoryGroup(ItemCategory.MysticalItem, new[]
            {
                "AncientIdol", "RopeShooterAnti", "Anti-Rope Spool", "Bugle_Magic", "Cursed Skull", "Lantern_Faerie", "ScoutEffigy", "Bugle_Scoutmaster Variant", "BookOfBones"
            })
        };
    }

    private static Dictionary<string, ItemCategory> BuildWikiCategoryOverrides()
    {
        var map = new Dictionary<string, ItemCategory>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < _wikiCategoryGroups.Length; i++)
        {
            WikiCategoryGroup group = _wikiCategoryGroups[i];
            AddWikiOverride(map, group.Category, group.Names);
        }

        return map;
    }

    private static Dictionary<string, int> BuildWikiCategoryOrder()
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int order = 0;

        for (int i = 0; i < _wikiCategoryGroups.Length; i++)
        {
            AddWikiOrder(map, ref order, _wikiCategoryGroups[i].Names);
        }

        return map;
    }

    private static void AddWikiOverride(Dictionary<string, ItemCategory> map, ItemCategory category, params string[] names)
    {
        for (int i = 0; i < names.Length; i++)
        {
            string name = names[i];
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            map[NormalizeCategoryKey(name)] = category;
        }
    }

    private static void AddWikiOrder(Dictionary<string, int> map, ref int order, params string[] names)
    {
        for (int i = 0; i < names.Length; i++)
        {
            string name = names[i];
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            map[NormalizeCategoryKey(name)] = order++;
        }
    }

    private static string NormalizeCategoryKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var chars = new List<char>(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (char.IsLetterOrDigit(c))
            {
                chars.Add(char.ToLowerInvariant(c));
            }
        }

        return new string(chars.ToArray());
    }

    internal static int GetCategoryOrder(ItemCategory category)
    {
        return category switch
        {
            ItemCategory.NaturalFood => 0,
            ItemCategory.MysticalFood => 1,
            ItemCategory.PackagedFood => 2,
            ItemCategory.Mushroom => 3,
            ItemCategory.Consumable => 4,
            ItemCategory.Deployable => 5,
            ItemCategory.MiscEquipment => 6,
            ItemCategory.MysticalItem => 7,
            _ => 8
        };
    }

    internal static bool IsFoodCategory(ItemCategory category)
    {
        return category == ItemCategory.NaturalFood
            || category == ItemCategory.MysticalFood
            || category == ItemCategory.PackagedFood
            || category == ItemCategory.Mushroom;
    }

    internal static bool IsEntryInMajorCategory(ItemEntry entry, MajorCategory major)
    {
        if (major == MajorCategory.All)
        {
            return true;
        }

        bool isFood = IsFoodCategory(entry.Category);
        return major == MajorCategory.Food ? isFood : !isFood;
    }

    internal static int GetWikiSortOrder(ItemEntry entry)
    {
        if (entry == null)
        {
            return int.MaxValue;
        }

        foreach (string key in GetCategoryCandidateKeys(entry.Prefab, entry.DisplayName))
        {
            if (_wikiOrder.TryGetValue(key, out int order))
            {
                return order;
            }
        }

        return int.MaxValue;
    }

    internal static ItemCategory[] GetSubCategories(MajorCategory major)
    {
        if (major == MajorCategory.All)
        {
            return Array.Empty<ItemCategory>();
        }

        if (major == MajorCategory.Food)
        {
            return new[]
            {
                ItemCategory.NaturalFood,
                ItemCategory.MysticalFood,
                ItemCategory.PackagedFood,
                ItemCategory.Mushroom
            };
        }

        return new[]
        {
            ItemCategory.Consumable,
            ItemCategory.Deployable,
            ItemCategory.MiscEquipment,
            ItemCategory.MysticalItem
        };
    }

    internal static string GetMajorCategoryLabel(MajorCategory category)
    {
        return category switch
        {
            MajorCategory.All => Localization.GetTextOrFallback("CATEGORY_ALL", "All"),
            MajorCategory.Food => Localization.GetTextOrFallback("CATEGORY_FOOD", "Food"),
            MajorCategory.Weapon => Localization.GetTextOrFallback("CATEGORY_WEAPON", "Weapon"),
            _ => "Unknown"
        };
    }

    internal static string GetCategoryLabel(ItemCategory category)
    {
        return category switch
        {
            ItemCategory.NaturalFood => Localization.GetTextOrFallback("CATEGORY_NATURAL_FOOD", "Natural Food"),
            ItemCategory.MysticalFood => Localization.GetTextOrFallback("CATEGORY_MYSTICAL_FOOD", "Mystical Food"),
            ItemCategory.PackagedFood => Localization.GetTextOrFallback("CATEGORY_PACKAGED_FOOD", "Packaged Food"),
            ItemCategory.Mushroom => Localization.GetTextOrFallback("CATEGORY_MUSHROOM", "Mushroom"),
            ItemCategory.Consumable => Localization.GetTextOrFallback("CATEGORY_CONSUMABLE", "Consumables"),
            ItemCategory.Deployable => Localization.GetTextOrFallback("CATEGORY_DEPLOYABLE", "Deployable"),
            ItemCategory.MiscEquipment => Localization.GetTextOrFallback("CATEGORY_MISC", "Misc"),
            ItemCategory.MysticalItem => Localization.GetTextOrFallback("CATEGORY_MYSTICAL_ITEM", "Mystical Item"),
            _ => "Other"
        };
    }

    internal static string GetAllSubCategoryLabel()
    {
        return Localization.GetTextOrFallback("CATEGORY_ALL", "All");
    }

    private static void WarnFallbackCategory(Item item, string displayName)
    {
        Plugin.VerboseLog($"Category fallback -> MiscEquipment. Prefab='{item?.name ?? "<null>"}', Display='{displayName}'");
    }

    private sealed class WikiCategoryGroup
    {
        internal ItemCategory Category { get; }
        internal string[] Names { get; }

        internal WikiCategoryGroup(ItemCategory category, string[] names)
        {
            Category = category;
            Names = names;
        }
    }
}
