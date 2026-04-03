using BepInEx;
using BepInEx.Logging;

using UnityEngine;
using UnityEngine.UI;

using Zorro.Core;

namespace ItemBrowser;

[BepInAutoPlugin]
[BepInDependency(PEAKLib.UI.UIPlugin.Id)]
public partial class Plugin : BaseUnityPlugin
{
    internal static ManualLogSource Log { get; private set; } = null!;

    private static float _nextUIWarmupCheckTime;
    private static bool _firstOpenPrimed;
    private static bool _hiddenMenuWindowPrimed;
    private static bool _postSpawnPrimeLocked;
    private static float _nextHiddenPrimeCheckTime;
    private static float _nextPostSpawnPrimeCheckTime;

    private void Awake()
    {
        SharedState.Instance = this;
        Log = Logger;

        SharedState.ConfigToggleKey = Config.Bind<KeyCode>("ItemBrowser", "Toggle Key", KeyCode.F5, "Press to open/close the item browser.");
        SharedState.ConfigAllowOnline = Config.Bind<bool>("ItemBrowser", "Allow Online Spawn", true, "Allow spawning items while online.");
        SharedState.ConfigVerboseLogs = Config.Bind<bool>("ItemBrowser", "Verbose Logs", false, "Enable detailed category/UI/spawn logs.");
        SharedState.ConfigGhostSendToObserved = Config.Bind<bool>("ItemBrowser", "Ghost Send To Observed", true, "When dead or in ghost/spectate mode, send spawned items to the currently observed teammate instead of the local (dead) character.");

        Localization.LoadLocalizedText();
        ItemSpawner.RefreshConsoleCommands();
    }

    private void OnDestroy()
    {
        if (SharedState.Instance == this)
        {
            SharedState.Instance = null;
        }

        ItemLoader.ResetState();
        IconResolver.ResetIconPrewarmState(stopCoroutine: true);
        VirtualList.ResetState();
    }

    private void Update()
    {
        ValidateRuntimeState();
        VirtualList.TickVirtualizedScrollApply();
        TickBackgroundUIWarmup();
        ItemLoader.TickBackgroundItemPreload();
        IconResolver.TickBackgroundIconPrewarm();
        VirtualList.TickBackgroundButtonPoolWarmup();
        TickHiddenFirstOpenPrime();
        TickPostSpawnPrimeLock();

        if (!InputHandler.IsTogglePressed())
        {
            return;
        }

        ToggleUI();
    }

    private static void ValidateRuntimeState()
    {
        if (SharedState.UiBuilt && SharedState.Page == null)
        {
            UIBuilder.ResetState("Cached UI page was destroyed.");
        }

        var db = SingletonAsset<ItemDatabase>.Instance;
        if (db == null || db.Objects == null || db.Objects.Count == 0)
        {
            return;
        }

        int dbId = db.GetInstanceID();

        if (SharedState.ItemPreloadRunning && SharedState.PreloadingDatabaseId != 0 && SharedState.PreloadingDatabaseId != dbId)
        {
            InvalidateItemListState($"ItemDatabase changed while preloading ({SharedState.PreloadingDatabaseId}->{dbId}).");
            return;
        }

        if (!SharedState.ItemPreloadRunning && SharedState.ItemListInitialized && SharedState.LoadedDatabaseId != 0 && SharedState.LoadedDatabaseId != dbId)
        {
            InvalidateItemListState($"ItemDatabase changed ({SharedState.LoadedDatabaseId}->{dbId}). Re-preloading.");
        }
    }

    private static void InvalidateItemListState(string reason)
    {
        ItemLoader.ResetState();
        IconResolver.ResetIconPrewarmState(stopCoroutine: true);

        SharedState.ItemListInitialized = false;
        SharedState.PreloadingDatabaseId = 0;
        SharedState.LoadedDatabaseId = 0;
        SharedState.ItemNamesLanguageIndex = -1;
        SharedState.ItemNamesLanguageMarker = string.Empty;
        SharedState.ListNeedsRefresh = true;
        SharedState.ListRenderRunning = false;
        SharedState.ListRenderGeneration++;
        _firstOpenPrimed = false;
        _hiddenMenuWindowPrimed = false;
        _postSpawnPrimeLocked = false;
        _nextHiddenPrimeCheckTime = 0f;
        _nextPostSpawnPrimeCheckTime = 0f;

        SharedState.ItemEntries.Clear();
        SharedState.ItemIconCache.Clear();
        SharedState.GeneratedTextureSpriteCache.Clear();
        SharedState.ItemButtonPool.Clear();
        SharedState.ActiveRenderEntries.Clear();

        VirtualList.ResetState();
        VirtualList.ResetButtonPoolWarmupState(stopCoroutine: true);

        VerboseLog(reason);
        VirtualList.MarkListDirty(reason);

        if (SharedState.PageOpen)
        {
            VirtualList.RefreshListIfNeeded(force: true);
        }
    }

    private static void TickBackgroundUIWarmup()
    {
        if (SharedState.UiBuilt)
        {
            return;
        }

        if (Time.unscaledTime < _nextUIWarmupCheckTime)
        {
            return;
        }

        _nextUIWarmupCheckTime = Time.unscaledTime + 0.1f;

        if (!UIBuilder.IsUIReady(out _))
        {
            return;
        }

        UIBuilder.BuildUI();
        SharedState.UiBuilt = true;
        VerboseLog("UI warmup build completed.");
    }

    private static void ToggleUI()
    {
        if (!EnsureUIBuilt())
        {
            return;
        }

        if (SharedState.PageOpen)
        {
            UIBuilder.ClosePage();
        }
        else
        {
            UIBuilder.OpenPage();
        }
    }

    private static bool EnsureUIBuilt()
    {
        if (SharedState.UiBuilt)
        {
            if (SharedState.Page != null)
            {
                return true;
            }

            UIBuilder.ResetState("Cached UI page was destroyed.");
        }

        if (!UIBuilder.IsUIReady(out string reason))
        {
            Log.LogWarning($"[ItemBrowser] UI not ready yet ({reason}). Try again after fully entering a match.");
            return false;
        }

        UIBuilder.BuildUI();
        SharedState.UiBuilt = true;
        VerboseLog("UI build completed.");
        return true;
    }

    private static void TickHiddenFirstOpenPrime()
    {
        if (_firstOpenPrimed || SharedState.PageOpen || !SharedState.UiBuilt || SharedState.ScrollContent == null)
        {
            return;
        }

        if (!SharedState.ItemListInitialized || SharedState.ItemPreloadRunning)
        {
            return;
        }

        if (Character.localCharacter != null)
        {
            return;
        }

        if (Time.unscaledTime < _nextHiddenPrimeCheckTime)
        {
            return;
        }

        _nextHiddenPrimeCheckTime = Time.unscaledTime + 0.25f;

        TabsManager.RefreshLanguageDependentContent(force: false);
        VirtualList.RefreshListIfNeeded();

        if (!_hiddenMenuWindowPrimed && Character.localCharacter == null)
        {
            PrimeMenuWindowOpenClose();
        }

        if (!SharedState.ListNeedsRefresh && !SharedState.ListRenderRunning)
        {
            _firstOpenPrimed = true;
            VerboseLog("Hidden first-open cache primed.");
        }
    }

    private static void PrimeMenuWindowOpenClose()
    {
        if (_hiddenMenuWindowPrimed || SharedState.Page == null || SharedState.PageOpen)
        {
            return;
        }

        try
        {
            UIBuilder.OpenPage();
            Canvas.ForceUpdateCanvases();

            RectTransform? content = VirtualList.GetListContent();
            if (content != null)
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(content);
            }

            UIBuilder.ClosePage();
            Canvas.ForceUpdateCanvases();

            _hiddenMenuWindowPrimed = true;
            VerboseLog("Menu window warmup completed before first manual F5.");
        }
        catch (System.Exception e)
        {
            VerboseLog($"Menu window warmup failed: {e.GetType().Name} {e.Message}");
        }
    }

    private static void TickPostSpawnPrimeLock()
    {
        if (_postSpawnPrimeLocked || SharedState.PageOpen || !SharedState.UiBuilt || SharedState.ScrollContent == null)
        {
            return;
        }

        if (Character.localCharacter == null)
        {
            return;
        }

        if (Time.unscaledTime < _nextPostSpawnPrimeCheckTime)
        {
            return;
        }

        _nextPostSpawnPrimeCheckTime = Time.unscaledTime + 0.1f;

        if (!_hiddenMenuWindowPrimed)
        {
            PrimeMenuWindowOpenClose();
        }

        if (!SharedState.ItemListInitialized || SharedState.ItemPreloadRunning)
        {
            return;
        }

        TabsManager.RefreshLanguageDependentContent(force: false);
        VirtualList.RefreshListIfNeeded();

        if (!SharedState.ListNeedsRefresh && !SharedState.ListRenderRunning && _hiddenMenuWindowPrimed)
        {
            _postSpawnPrimeLocked = true;
            VerboseLog("Post-spawn prime lock completed.");
        }
    }

    internal static void VerboseLog(string message)
    {
        if (SharedState.ConfigVerboseLogs!.Value)
        {
            Log.LogInfo($"[ItemBrowser] {message}");
        }
    }
}
