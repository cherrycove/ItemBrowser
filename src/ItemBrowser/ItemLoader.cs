using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

using UnityEngine;

using Zorro.Core;

namespace ItemBrowser;

internal static class ItemLoader
{
    private static Coroutine? _preloadCoroutine;
    private static int _preloadTotalCount;
    private static int _preloadProcessedCount;
    private static int _preloadAddedCount;
    private static float _nextPreloadCheckTime;

    internal static void EnsureItemList()
    {
        if (SharedState.ItemListInitialized || SharedState.ItemPreloadRunning)
        {
            return;
        }

        TryStartBackgroundItemPreload("EnsureItemList");
    }

    internal static void TickBackgroundItemPreload()
    {
        if (SharedState.ItemListInitialized || SharedState.ItemPreloadRunning)
        {
            return;
        }

        if (Time.unscaledTime < _nextPreloadCheckTime)
        {
            return;
        }

        _nextPreloadCheckTime = Time.unscaledTime + 0.1f;
        TryStartBackgroundItemPreload("AutoWarmup");
    }

    internal static bool TryStartBackgroundItemPreload(string reason)
    {
        if (SharedState.ItemListInitialized || SharedState.ItemPreloadRunning)
        {
            return false;
        }

        if (SharedState.Instance == null)
        {
            return false;
        }

        var db = SingletonAsset<ItemDatabase>.Instance;
        if (db == null || db.Objects == null || db.Objects.Count == 0)
        {
            return false;
        }

        SharedState.ItemEntries.Clear();
        SharedState.ItemIconCache.Clear();
        SharedState.GeneratedTextureSpriteCache.Clear();
        IconResolver.ResetIconPrewarmState(stopCoroutine: true);
        VirtualList.ResetButtonPoolWarmupState(stopCoroutine: true);

        VirtualList.MarkListDirty($"Item preload started ({reason})");

        _preloadTotalCount = db.Objects.Count;
        _preloadProcessedCount = 0;
        _preloadAddedCount = 0;
        SharedState.ItemPreloadRunning = true;
        SharedState.ItemNamesLanguageIndex = Localization.GetCurrentLanguageIndex();
        SharedState.ItemNamesLanguageMarker = Localization.BuildLanguageMarker();
        int dbId = db.GetInstanceID();
        SharedState.PreloadingDatabaseId = dbId;
        SharedState.LoadedDatabaseId = 0;
        _preloadCoroutine = SharedState.Instance.StartCoroutine(BuildItemListGradually(db, dbId));

        Plugin.VerboseLog($"Item preload started ({reason}). Total={_preloadTotalCount}");
        return true;
    }

    private static IEnumerator BuildItemListGradually(ItemDatabase db, int dbId)
    {
        const int itemsPerFrame = 4;
        int budget = itemsPerFrame;

        // Yield once to avoid doing preload work in the same frame that triggered F5/UI open.
        yield return null;

        foreach (var item in db.Objects)
        {
            _preloadProcessedCount++;

            try
            {
                if (item != null)
                {
                    string displayName = GetResolvedItemDisplayName(item);

                    if (!CategorySystem.ShouldHideItemFromBrowser(item))
                    {
                        ItemCategory category = CategorySystem.GetCategory(item, displayName);
                        Sprite? icon = IconResolver.GetItemIcon(item, allowHeavyFallback: false);
                        SharedState.ItemEntries.Add(new ItemEntry(item, displayName, category, icon));
                        _preloadAddedCount++;
                    }
                    else
                    {
                        Plugin.VerboseLog($"Item hidden: prefab='{item.name}', display='{displayName}'");
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[ItemBrowser] Preload item failed: {item?.name} ({e.GetType().Name} {e.Message})");
            }

            budget--;
            if (budget <= 0)
            {
                budget = itemsPerFrame;
                yield return null;
            }
        }

        SharedState.ItemPreloadRunning = false;
        _preloadCoroutine = null;
        SharedState.ItemListInitialized = true;
        SharedState.LoadedDatabaseId = SharedState.PreloadingDatabaseId != 0 ? SharedState.PreloadingDatabaseId : dbId;
        SharedState.PreloadingDatabaseId = 0;

        if (SharedState.ConfigVerboseLogs != null && SharedState.ConfigVerboseLogs.Value)
        {
            var breakdown = SharedState.ItemEntries
                .GroupBy(entry => entry.Category)
                .OrderBy(group => CategorySystem.GetCategoryOrder(group.Key))
                .Select(group => $"{CategorySystem.GetCategoryLabel(group.Key)}={group.Count()}");
            Plugin.Log.LogInfo($"[ItemBrowser] Item list built in background. Total={SharedState.ItemEntries.Count}, Added={_preloadAddedCount}, Categories: {string.Join(", ", breakdown)}");
        }

        VirtualList.MarkListDirty("Item preload completed");

        IconResolver.TryStartBackgroundIconPrewarm("PostPreload");
        VirtualList.TryStartBackgroundButtonPoolWarmup("PostPreload", VirtualList.CalculateButtonPoolWarmupTarget(SharedState.ItemEntries.Count));

        if (SharedState.PageOpen)
        {
            VirtualList.RefreshListIfNeeded();
        }
    }

    internal static string GetPreloadStatusText()
    {
        if (SharedState.ItemPreloadRunning)
        {
            if (_preloadTotalCount > 0)
            {
                return $"{Localization.GetText("STATUS_LOADING")} {_preloadProcessedCount}/{_preloadTotalCount}";
            }

            return Localization.GetText("STATUS_LOADING");
        }

        return Localization.GetText("STATUS_NOT_READY");
    }

    internal static string GetResolvedItemDisplayName(Item item)
    {
        if (item == null)
        {
            return string.Empty;
        }

        string localizedName = Localization.GetLocalizedItemName(item);
        string displayName = string.IsNullOrWhiteSpace(localizedName) ? item.name ?? string.Empty : localizedName;
        return GetDisplayNameOverride(item, displayName);
    }

    private static string GetDisplayNameOverride(Item item, string displayName)
    {
        if (item == null)
        {
            return displayName;
        }

        string prefabName = item.name ?? string.Empty;
        if (prefabName.Equals("EggTurkey", StringComparison.OrdinalIgnoreCase))
        {
            string localized = Localization.ResolveLocalizedText("NAME_COOKED BIRD");
            if (!string.IsNullOrWhiteSpace(localized))
            {
                return localized;
            }

            return displayName;
        }

        return displayName;
    }

    internal static void RefreshItemDisplayNamesForCurrentLanguage(bool force = false, string? currentLanguageMarker = null)
    {
        if (!SharedState.ItemListInitialized || SharedState.ItemEntries.Count == 0)
        {
            return;
        }

        int languageIndex = Localization.GetCurrentLanguageIndex();
        currentLanguageMarker ??= Localization.BuildLanguageMarker();
        bool markerChanged = !string.Equals(currentLanguageMarker, SharedState.ItemNamesLanguageMarker, StringComparison.Ordinal);

        if (!force && !markerChanged && languageIndex == SharedState.ItemNamesLanguageIndex)
        {
            return;
        }

        int renamedCount = 0;
        for (int i = 0; i < SharedState.ItemEntries.Count; i++)
        {
            ItemEntry entry = SharedState.ItemEntries[i];
            string displayName = GetResolvedItemDisplayName(entry.Prefab);

            if (!string.Equals(entry.DisplayName, displayName, StringComparison.Ordinal))
            {
                entry.UpdateDisplayName(displayName);
                renamedCount++;
            }
        }

        SharedState.ItemNamesLanguageIndex = languageIndex;
        SharedState.ItemNamesLanguageMarker = currentLanguageMarker;
        Plugin.VerboseLog($"Language refresh complete. index={languageIndex}, renamed={renamedCount}, total={SharedState.ItemEntries.Count}, markerChanged={markerChanged}");
    }

    internal static void ResetState()
    {
        StopTrackedCoroutine(ref _preloadCoroutine);
        _preloadTotalCount = 0;
        _preloadProcessedCount = 0;
        _preloadAddedCount = 0;
        _nextPreloadCheckTime = 0f;
    }

    private static void StopTrackedCoroutine(ref Coroutine? coroutine)
    {
        if (coroutine == null)
        {
            return;
        }

        if (SharedState.Instance != null)
        {
            SharedState.Instance.StopCoroutine(coroutine);
        }

        coroutine = null;
    }
}
