using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;

using PEAKLib.UI;
using PEAKLib.UI.Elements;

using Photon.Pun;

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

using UnityEngine;
using UnityEngine.UI;

using Zorro.Core;
using Zorro.Settings;

namespace ItemBrowser;

[BepInAutoPlugin]
[BepInDependency(PEAKLib.UI.UIPlugin.Id)]
public partial class Plugin : BaseUnityPlugin
{
    internal static ManualLogSource Log { get; private set; } = null!;

    private static ConfigEntry<KeyCode> configToggleKey;
    private static ConfigEntry<float> configSpawnDistance;
    private static ConfigEntry<bool> configAllowOnline;
    private static ConfigEntry<bool> configVerboseLogs;

    private static PeakCustomPage? page;
    private static PeakText? headerTitleText;
    private static PeakMenuButton? closeMenuButton;
    private static PeakTextInput? searchInput;
    private static PeakScrollableContent? scrollContent;

    private static Plugin? instance;
    private static Coroutine? itemPreloadCoroutine;
    private static bool itemPreloadRunning;
    private static int itemPreloadTotalCount;
    private static int itemPreloadProcessedCount;
    private static int itemPreloadAddedCount;
    private static float nextPreloadCheckTime;
    private static float nextUIWarmupCheckTime;
    private static int preloadingDatabaseId;
    private static int loadedDatabaseId;
    private static int itemNamesLanguageIndex = -1;
    private static string itemNamesLanguageMarker = string.Empty;
    private static string lastLanguageMarker = string.Empty;

    private static bool uiBuilt;
    private static bool pageOpen;
    private static bool listNeedsRefresh = true;
    private static bool listRenderRunning;
    private static bool firstOpenPrimed;
    private static bool hiddenMenuWindowPrimed;
    private static bool postSpawnPrimeLocked;
    private static float nextHiddenPrimeCheckTime;
    private static float nextPostSpawnPrimeCheckTime;
    private static string currentSearch = string.Empty;
    private static MajorCategory currentMajorFilter = MajorCategory.All;
    private static ItemCategory? currentSubCategoryFilter;
    private static bool templatesLogged;
    private static PeakHorizontalTabs? majorTabs;
    private static PeakHorizontalTabs? subCategoryTabs;
    private static GameObject? subCategoryTabsRoot;
    private static RectTransform? topControlsRect;
    private static RectTransform? listContainerRect;
    private static Scrollbar? listScrollbar;
    private static readonly List<MajorCategoryTab> majorTabEntries = new();
    private static readonly List<CategoryTab> subCategoryTabEntries = new();

    private static readonly List<ItemEntry> itemEntries = new();
    private static readonly Dictionary<string, Sprite?> itemIconCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<int, Sprite> generatedTextureSpriteCache = new();
    private static readonly Dictionary<string, List<Texture2D>> textureNameIndex = new(StringComparer.OrdinalIgnoreCase);
    private static bool textureNameIndexBuilt;
    private static GridLayoutGroup? itemGridLayout;
    private static bool itemListInitialized;
    private static Coroutine? listRenderCoroutine;
    private static int listRenderGeneration;

    private static readonly Dictionary<string, string> itemNameKeyMap = new(StringComparer.OrdinalIgnoreCase);
    private static bool itemNameKeyMapInitialized;
    private static readonly Dictionary<string, ItemCategory> wikiCategoryOverrides = BuildWikiCategoryOverrides();
    private static readonly HashSet<string> hiddenPrefabNames = BuildHiddenPrefabNameSet();

    private static bool buttonTemplateRecovered;

    private static readonly Dictionary<string, List<string>> localizedTextTable = new(StringComparer.OrdinalIgnoreCase);
    private static bool inputSystemChecked;
    private static bool inputSystemAvailable;
    private static int lastRenderedLanguageIndex = -1;
    private static PropertyInfo? inputSystemKeyboardCurrentProp;
    private static PropertyInfo? inputSystemKeyboardItemProp;
    private static PropertyInfo? inputSystemKeyControlPressedProp;
    private static Type? inputSystemKeyType;
    private static bool legacyInputAvailable = true;

    private static MethodInfo? menuWindowOpenMethod;
    private static MethodInfo? menuWindowCloseMethod;

    private enum MajorCategory
    {
        All,
        Food,
        Weapon
    }

    private enum ItemCategory
    {
        NaturalFood,
        MysticalFood,
        PackagedFood,
        Mushroom,
        Consumable,
        Deployable,
        MiscEquipment,
        MysticalItem
    }

    private sealed class ItemEntry
    {
        public Item Prefab { get; }
        public string PrefabName { get; }
        public string DisplayName { get; private set; } = string.Empty;
        public ItemCategory Category { get; }
        public Sprite? Icon { get; private set; }
        public string SearchText { get; private set; } = string.Empty;

        public ItemEntry(Item prefab, string displayName, ItemCategory category, Sprite? icon)
        {
            Prefab = prefab;
            PrefabName = prefab.name ?? string.Empty;
            Category = category;
            Icon = icon;
            UpdateDisplayName(displayName);
        }

        public void UpdateIcon(Sprite? icon)
        {
            Icon = icon;
        }

        public void UpdateDisplayName(string displayName)
        {
            string normalized = string.IsNullOrWhiteSpace(displayName) ? PrefabName : displayName.Trim();
            DisplayName = normalized;
            SearchText = $"{normalized} {PrefabName}".ToLowerInvariant();
        }
    }

    private void Awake()
    {
        instance = this;
        Log = Logger;
        configToggleKey = ((BaseUnityPlugin)this).Config.Bind<KeyCode>("ItemBrowser", "Toggle Key", KeyCode.F5, "Press to open/close the item browser.");
        configSpawnDistance = ((BaseUnityPlugin)this).Config.Bind<float>("ItemBrowser", "Spawn Distance", 1.5f, "Distance in front of the player to spawn items.");
        configAllowOnline = ((BaseUnityPlugin)this).Config.Bind<bool>("ItemBrowser", "Allow Online Spawn", true, "Allow spawning items while online.");
        configVerboseLogs = ((BaseUnityPlugin)this).Config.Bind<bool>("ItemBrowser", "Verbose Logs", false, "Enable detailed category/UI/spawn logs.");

        LoadLocalizedText();
    }

    private void OnDestroy()
    {
        if (instance == this)
        {
            instance = null;
        }

        if (itemPreloadCoroutine != null)
        {
            StopCoroutine(itemPreloadCoroutine);
            itemPreloadCoroutine = null;
        }

        itemPreloadRunning = false;

        if (listRenderCoroutine != null)
        {
            StopCoroutine(listRenderCoroutine);
            listRenderCoroutine = null;
        }

        listRenderRunning = false;
    }

    private void Update()
    {
        ValidateRuntimeState();
        TickBackgroundUIWarmup();
        TickBackgroundItemPreload();
        TickHiddenFirstOpenPrime();
        TickPostSpawnPrimeLock();

        if (!IsTogglePressed())
        {
            return;
        }

        ToggleUI();
    }

    private static void ValidateRuntimeState()
    {
        if (uiBuilt && page == null)
        {
            ResetUIRuntimeState("Cached UI page was destroyed.");
        }

        var db = SingletonAsset<ItemDatabase>.Instance;
        if (db == null || db.Objects == null || db.Objects.Count == 0)
        {
            return;
        }

        int dbId = db.GetInstanceID();

        if (itemPreloadRunning && preloadingDatabaseId != 0 && preloadingDatabaseId != dbId)
        {
            InvalidateItemListState($"ItemDatabase changed while preloading ({preloadingDatabaseId}->{dbId}).");
            return;
        }

        if (!itemPreloadRunning && itemListInitialized && loadedDatabaseId != 0 && loadedDatabaseId != dbId)
        {
            InvalidateItemListState($"ItemDatabase changed ({loadedDatabaseId}->{dbId}). Re-preloading.");
        }
    }

    private static void TickBackgroundUIWarmup()
    {
        if (uiBuilt)
        {
            return;
        }

        if (Time.unscaledTime < nextUIWarmupCheckTime)
        {
            return;
        }

        nextUIWarmupCheckTime = Time.unscaledTime + 0.1f;

        if (!IsUIReady(out _))
        {
            return;
        }

        BuildUI();
        uiBuilt = true;
        LogAvailableTemplates();
        VerboseLog("UI warmup build completed.");
    }

    private static void ResetUIRuntimeState(string reason)
    {
        page = null;
        headerTitleText = null;
        closeMenuButton = null;
        searchInput = null;
        scrollContent = null;
        majorTabs = null;
        subCategoryTabs = null;
        subCategoryTabsRoot = null;
        topControlsRect = null;
        listContainerRect = null;
        listScrollbar = null;
        itemGridLayout = null;
        majorTabEntries.Clear();
        subCategoryTabEntries.Clear();
        uiBuilt = false;
        pageOpen = false;
        listNeedsRefresh = true;
        listRenderRunning = false;
        listRenderGeneration++;
        firstOpenPrimed = false;
        hiddenMenuWindowPrimed = false;
        postSpawnPrimeLocked = false;
        nextHiddenPrimeCheckTime = 0f;
        nextPostSpawnPrimeCheckTime = 0f;

        VerboseLog(reason);
    }

    private static void InvalidateItemListState(string reason)
    {
        if (itemPreloadCoroutine != null && instance != null)
        {
            instance.StopCoroutine(itemPreloadCoroutine);
        }

        itemPreloadCoroutine = null;
        itemPreloadRunning = false;
        itemListInitialized = false;
        itemPreloadTotalCount = 0;
        itemPreloadProcessedCount = 0;
        itemPreloadAddedCount = 0;
        preloadingDatabaseId = 0;
        loadedDatabaseId = 0;
        itemNamesLanguageIndex = -1;
        itemNamesLanguageMarker = string.Empty;
        listNeedsRefresh = true;
        listRenderRunning = false;
        listRenderGeneration++;
        firstOpenPrimed = false;
        hiddenMenuWindowPrimed = false;
        postSpawnPrimeLocked = false;
        nextHiddenPrimeCheckTime = 0f;
        nextPostSpawnPrimeCheckTime = 0f;
        itemEntries.Clear();
        itemIconCache.Clear();
        generatedTextureSpriteCache.Clear();
        textureNameIndex.Clear();
        textureNameIndexBuilt = false;

        VerboseLog(reason);
        MarkListDirty(reason);

        if (pageOpen)
        {
            RefreshListIfNeeded(force: true);
        }
    }

    private static void ToggleUI()
    {
        if (!EnsureUIBuilt())
        {
            return;
        }

        if (pageOpen)
        {
            ClosePage();
        }
        else
        {
            OpenPage();
        }
    }

    private static bool EnsureUIBuilt()
    {
        if (uiBuilt)
        {
            if (page != null)
            {
                return true;
            }

            ResetUIRuntimeState("Cached UI page was destroyed.");
        }

        if (!IsUIReady(out string reason))
        {
            if (!string.IsNullOrWhiteSpace(reason))
            {
                Log.LogWarning($"[ItemBrowser] UI not ready yet ({reason}). Try again after fully entering a match.");
            }
            else
            {
                Log.LogWarning("[ItemBrowser] UI not ready yet. Try again after fully entering a match.");
            }
            return false;
        }

        BuildUI();
        uiBuilt = true;
        LogAvailableTemplates();
        VerboseLog("UI build completed.");
        return true;
    }

    private static bool IsUIReady(out string reason)
    {
        List<string> missing = new(3);

        var mapper = SingletonAsset<InputCellMapper>.Instance;
        if (mapper == null)
        {
            missing.Add("InputCellMapper.Instance == null");
        }
        else if (mapper.FloatSettingCell == null)
        {
            missing.Add("InputCellMapper.FloatSettingCell == null");
        }

        if (Templates.ButtonTemplate == null)
        {
            if (!TryRecoverButtonTemplate(out string templateReason))
            {
                if (string.IsNullOrWhiteSpace(templateReason))
                {
                    missing.Add("Templates.ButtonTemplate == null");
                }
                else
                {
                    missing.Add($"Templates.ButtonTemplate == null ({templateReason})");
                }
            }
        }

        if (missing.Count == 0)
        {
            reason = string.Empty;
            return true;
        }

        reason = string.Join(", ", missing);
        return false;
    }

    private static bool TryRecoverButtonTemplate(out string reason)
    {
        reason = string.Empty;

        if (Templates.ButtonTemplate != null)
        {
            return true;
        }

        try
        {
            GameObject[] allObjects = Resources.FindObjectsOfTypeAll<GameObject>();
            if (allObjects == null || allObjects.Length == 0)
            {
                reason = "Resources empty";
                return false;
            }

            GameObject? source = allObjects.FirstOrDefault(obj => obj != null && obj.name == "UI_MainMenuButton_LeaveGame (2)");
            if (source == null)
            {
                source = allObjects.FirstOrDefault(obj =>
                    obj != null && obj.name.StartsWith("UI_MainMenuButton_LeaveGame", StringComparison.Ordinal));
            }

            if (source == null)
            {
                reason = "MainMenu button prefab not found";
                return false;
            }

            GameObject template = Instantiate(source);
            template.name = "PeakUIButton";
            RemoveLocalizedTextComponent(template);
            DontDestroyOnLoad(template);

            if (!TrySetTemplatesProperty("ButtonTemplate", template, out string setError))
            {
                reason = setError;
                return false;
            }

            if (!buttonTemplateRecovered)
            {
                Log.LogInfo("[ItemBrowser] Recovered Templates.ButtonTemplate from Resources.");
                buttonTemplateRecovered = true;
            }

            return true;
        }
        catch (Exception ex)
        {
            reason = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    private sealed class CategoryTab
    {
        public ItemCategory? Category { get; }
        public GameObject Root { get; }
        public Button Button { get; }
        public Image Background { get; }
        public Image Selected { get; }
        public PeakText Label { get; }

        public CategoryTab(
            ItemCategory? category,
            GameObject root,
            Button button,
            Image background,
            Image selected,
            PeakText label
        )
        {
            Category = category;
            Root = root;
            Button = button;
            Background = background;
            Selected = selected;
            Label = label;
        }
    }

    private sealed class MajorCategoryTab
    {
        public MajorCategory Category { get; }
        public GameObject Root { get; }
        public Button Button { get; }
        public Image Background { get; }
        public Image Selected { get; }
        public PeakText Label { get; }

        public MajorCategoryTab(
            MajorCategory category,
            GameObject root,
            Button button,
            Image background,
            Image selected,
            PeakText label
        )
        {
            Category = category;
            Root = root;
            Button = button;
            Background = background;
            Selected = selected;
            Label = label;
        }
    }

    private static void RemoveLocalizedTextComponent(GameObject target)
    {
        if (target == null) return;

        Component[] components = target.GetComponentsInChildren<Component>(true);
        for (int i = 0; i < components.Length; i++)
        {
            Component comp = components[i];
            if (comp == null) continue;
            if (string.Equals(comp.GetType().Name, "LocalizedText", StringComparison.Ordinal))
            {
                DestroyImmediate(comp);
            }
        }
    }

    private static bool TrySetTemplatesProperty(string propertyName, object value, out string reason)
    {
        reason = string.Empty;

        var prop = typeof(Templates).GetProperty(propertyName, BindingFlags.Public | BindingFlags.Static);
        if (prop == null)
        {
            reason = $"Templates.{propertyName} property not found";
            return false;
        }

        MethodInfo? setter = prop.GetSetMethod(true);
        if (setter == null)
        {
            reason = $"Templates.{propertyName} setter not accessible";
            return false;
        }

        setter.Invoke(null, new[] { value });
        return true;
    }

    private static void LogAvailableTemplates()
    {
        if (templatesLogged)
        {
            return;
        }

        templatesLogged = true;

        try
        {
            var templateType = typeof(Templates);
            var props = templateType.GetProperties(BindingFlags.Public | BindingFlags.Static);
            if (props.Length == 0)
            {
                Log.LogInfo("[ItemBrowser] Templates has no public static properties.");
                return;
            }

            foreach (var prop in props)
            {
                object? value = null;
                try
                {
                    value = prop.GetValue(null);
                }
                catch
                {
                    // ignore
                }

                string status = value == null ? "null" : "ok";
                Log.LogInfo($"[ItemBrowser] Template {prop.Name} ({prop.PropertyType.Name}) = {status}");
            }
        }
        catch (Exception e)
        {
            Log.LogWarning($"[ItemBrowser] Failed to enumerate Templates: {e.GetType().Name} {e.Message}");
        }
    }

    private static void VerboseLog(string message)
    {
        if (configVerboseLogs != null && configVerboseLogs.Value)
        {
            Log.LogInfo($"[ItemBrowser] {message}");
        }
    }

    private static void WarnFallbackCategory(Item item, string displayName)
    {
        if (configVerboseLogs != null && configVerboseLogs.Value)
        {
            string prefabName = item?.name ?? "<null>";
            Log.LogWarning($"[ItemBrowser] Category fallback -> MiscEquipment. Prefab='{prefabName}', Display='{displayName}'");
        }
    }

    private static void BuildUI()
    {
        page = MenuAPI.CreatePageWithBackground("ItemBrowserPage");
        page.OpenOnStart = false;
        page.CloseOnUICancel = true;
        page.AutoHideOnClose = true;

        page.SetOnOpen(() =>
        {
            pageOpen = true;
            RefreshLanguageDependentContent(force: false);
            EnsureItemList();
            RefreshListIfNeeded();
        });

        page.SetOnClose(() =>
        {
            pageOpen = false;
        });

        var panel = new GameObject("Panel")
            .AddComponent<PeakElement>()
            .ParentTo(page)
            .SetAnchorMin(new Vector2(0.5f, 0.5f))
            .SetAnchorMax(new Vector2(0.5f, 0.5f))
            .SetPosition(Vector2.zero)
            .SetSize(new Vector2(760, 760));

        var panelRect = panel.GetComponent<RectTransform>();
        if (panelRect != null)
        {
            panelRect.pivot = new Vector2(0.5f, 0.5f);
            panelRect.anchoredPosition = Vector2.zero;
            panelRect.sizeDelta = new Vector2(760, 760);
        }

        var panelImage = panel.gameObject.AddComponent<Image>();
        panelImage.color = new Color(0.05f, 0.04f, 0.03f, 0.9f);

        var panelOutline = panel.gameObject.AddComponent<Outline>();
        panelOutline.effectColor = new Color(1f, 1f, 1f, 0.3f);
        panelOutline.effectDistance = new Vector2(2f, -2f);

        panel.transform.SetAsLastSibling();

        var headerContainer = new GameObject("Header")
            .AddComponent<PeakElement>()
            .ParentTo(panel)
            .SetAnchorMin(new Vector2(0.5f, 1f))
            .SetAnchorMax(new Vector2(0.5f, 1f))
            .SetPosition(new Vector2(0f, -16f))
            .SetSize(new Vector2(360f, 34f));

        var headerRect = headerContainer.GetComponent<RectTransform>();
        if (headerRect != null)
        {
            headerRect.pivot = new Vector2(0.5f, 1f);
        }

        headerTitleText = MenuAPI
            .CreateText(GetText("TITLE"), "HeaderText")
            .SetFontSize(26)
            .ParentTo(headerContainer)
            .ExpandToParent();
        headerTitleText.TextMesh.alignment = TMPro.TextAlignmentOptions.Midline;
        lastLanguageMarker = BuildLanguageMarker();

        closeMenuButton = MenuAPI
            .CreateMenuButton(GetTextOrFallback("CLOSE_BUTTON", "Close"))
            .ParentTo(panel)
            .OnClick(ClosePage);

        RemoveLocalizedTextComponent(closeMenuButton.gameObject);

        var closeRect = closeMenuButton.GetComponent<RectTransform>();
        if (closeRect != null)
        {
            closeRect.anchorMin = new Vector2(1f, 1f);
            closeRect.anchorMax = new Vector2(1f, 1f);
            closeRect.pivot = new Vector2(1f, 1f);
            closeRect.anchoredPosition = new Vector2(-24f, -10f);
            closeRect.sizeDelta = new Vector2(82f, 32f);
        }

        closeMenuButton.SetColor(new Color(0.22f, 0.16f, 0.12f, 0.95f), false);
        closeMenuButton.SetBorderColor(new Color(0.62f, 0.54f, 0.44f, 0.55f));

        if (closeMenuButton.Text != null)
        {
            closeMenuButton.Text.fontSize = 16;
            closeMenuButton.Text.alignment = TMPro.TextAlignmentOptions.Center;
            closeMenuButton.Text.textWrappingMode = TMPro.TextWrappingModes.NoWrap;
            closeMenuButton.Text.overflowMode = TMPro.TextOverflowModes.Truncate;
            closeMenuButton.Text.margin = Vector4.zero;
            closeMenuButton.Text.color = new Color(0.95f, 0.92f, 0.86f, 1f);
            closeMenuButton.Text.raycastTarget = false;
        }

        NormalizeButtonLayout(closeMenuButton, new Vector2(82f, 32f));

        if (closeRect != null)
        {
            closeRect.anchorMin = new Vector2(1f, 1f);
            closeRect.anchorMax = new Vector2(1f, 1f);
            closeRect.pivot = new Vector2(1f, 1f);
            closeRect.anchoredPosition = new Vector2(-12f, -10f);
            closeRect.sizeDelta = new Vector2(82f, 32f);
        }

        var topControls = new GameObject("TopControls")
            .AddComponent<PeakElement>()
            .ParentTo(panel)
            .SetAnchorMin(new Vector2(0.5f, 1f))
            .SetAnchorMax(new Vector2(0.5f, 1f))
            .SetPosition(new Vector2(0f, -58f))
            .SetSize(new Vector2(720f, 166f));

        var topRect = topControls.GetComponent<RectTransform>();
        topControlsRect = topRect;
        if (topRect != null)
        {
            topRect.pivot = new Vector2(0.5f, 1f);
        }

        var topBg = topControls.gameObject.AddComponent<Image>();
        topBg.color = new Color(0.11f, 0.09f, 0.07f, 0.97f);

        var topOutline = topControls.gameObject.AddComponent<Outline>();
        topOutline.effectColor = new Color(1f, 1f, 1f, 0.2f);
        topOutline.effectDistance = new Vector2(1f, -1f);

        var searchContainer = new GameObject("SearchContainer")
            .AddComponent<PeakElement>()
            .ParentTo(topControls)
            .SetAnchorMin(new Vector2(0.5f, 1f))
            .SetAnchorMax(new Vector2(0.5f, 1f))
            .SetPosition(new Vector2(0f, -8f))
            .SetSize(new Vector2(680f, 48f));

        var searchRect = searchContainer.GetComponent<RectTransform>();
        if (searchRect != null)
        {
            searchRect.pivot = new Vector2(0.5f, 1f);
        }

        var searchBg = searchContainer.gameObject.AddComponent<Image>();
        searchBg.color = new Color(0.16f, 0.13f, 0.1f, 0.95f);

        searchInput = MenuAPI
            .CreateTextInput("SearchInput")
            .ParentTo(searchContainer)
            .ExpandToParent()
            .SetPlaceholder(GetText("SEARCH_PLACEHOLDER"))
            .OnValueChanged(OnSearchChanged);

        if (searchInput.InputField != null)
        {
            var inputField = searchInput.InputField;
            inputField.textComponent.color = new Color(0.42f, 0.36f, 0.3f, 1f);
            inputField.textComponent.fontSize = 30f;
            inputField.textComponent.enableAutoSizing = false;
            inputField.caretColor = new Color(0.42f, 0.36f, 0.3f, 1f);

            if (inputField.placeholder is TMPro.TMP_Text placeholder)
            {
                placeholder.color = new Color(0.42f, 0.36f, 0.3f, 1f);
                placeholder.fontSize = 30f;
                placeholder.enableAutoSizing = false;

                inputField.textComponent.color = placeholder.color;
                inputField.textComponent.fontSize = placeholder.fontSize;
                inputField.textComponent.fontStyle = placeholder.fontStyle;
                inputField.textComponent.alignment = placeholder.alignment;
                inputField.caretColor = placeholder.color;
            }
        }

        var majorTabsObj = new GameObject(
            "MajorTabs",
            typeof(RectTransform),
            typeof(ScrollRect),
            typeof(RectMask2D)
        );
        majorTabsObj.transform.SetParent(topControls.transform, false);

        majorTabs = majorTabsObj.AddComponent<PeakHorizontalTabs>();
        majorTabs.SetBackgroundColor(new Color(0.18f, 0.14f, 0.1f, 0.95f));

        var majorRect = majorTabsObj.GetComponent<RectTransform>();
        if (majorRect != null)
        {
            majorRect.anchorMin = new Vector2(0.5f, 1f);
            majorRect.anchorMax = new Vector2(0.5f, 1f);
            majorRect.pivot = new Vector2(0.5f, 1f);
            majorRect.anchoredPosition = new Vector2(0f, -56f);
            majorRect.sizeDelta = new Vector2(680f, 44f);
        }

        var majorBg = majorTabsObj.AddComponent<Image>();
        majorBg.color = new Color(0.16f, 0.13f, 0.1f, 0.95f);

        var subTabsObj = new GameObject(
            "SubCategoryTabs",
            typeof(RectTransform),
            typeof(ScrollRect),
            typeof(RectMask2D)
        );
        subTabsObj.transform.SetParent(topControls.transform, false);

        subCategoryTabsRoot = subTabsObj;
        subCategoryTabs = subTabsObj.AddComponent<PeakHorizontalTabs>();
        subCategoryTabs.SetBackgroundColor(new Color(0.18f, 0.14f, 0.1f, 0.95f));

        var subRect = subTabsObj.GetComponent<RectTransform>();
        if (subRect != null)
        {
            subRect.anchorMin = new Vector2(0.5f, 1f);
            subRect.anchorMax = new Vector2(0.5f, 1f);
            subRect.pivot = new Vector2(0.5f, 1f);
            subRect.anchoredPosition = new Vector2(0f, -100f);
            subRect.sizeDelta = new Vector2(680f, 44f);
        }

        var subBg = subTabsObj.AddComponent<Image>();
        subBg.color = new Color(0.16f, 0.13f, 0.1f, 0.95f);

        BuildMajorTabs(majorTabs);
        RebuildSubCategoryTabs();
        UpdateSubCategoryVisibility();

        ConfigureTabsContentLayout(majorTabsObj.transform.Find("Content"), 0f, true);
        ConfigureTabsContentLayout(subTabsObj.transform.Find("Content"), 0f, false);

        var listContainer = new GameObject("ListContainer")
            .AddComponent<PeakElement>()
            .ParentTo(panel)
            .SetAnchorMin(new Vector2(0f, 0f))
            .SetAnchorMax(new Vector2(1f, 1f))
            .SetOffsetMin(new Vector2(20f, 20f))
            .SetOffsetMax(new Vector2(-20f, currentMajorFilter == MajorCategory.All ? -192f : -236f));

        listContainerRect = listContainer.GetComponent<RectTransform>();

        var listBg = listContainer.gameObject.AddComponent<Image>();
        listBg.color = new Color(0.11f, 0.09f, 0.07f, 0.95f);

        var listOutline = listContainer.gameObject.AddComponent<Outline>();
        listOutline.effectColor = new Color(1f, 1f, 1f, 0.14f);
        listOutline.effectDistance = new Vector2(1f, -1f);

        scrollContent = MenuAPI
            .CreateScrollableContent("ItemList")
            .ParentTo(listContainer)
            .ExpandToParent()
            .SetOffsetMin(new Vector2(12f, 12f))
            .SetOffsetMax(new Vector2(-26f, -12f));

        SetupListScrollbar(listContainer);

        EnsureListLayoutReady();
        UpdateItemGridCellSize();

        page.gameObject.SetActive(false);
        MarkListDirty("UI built");
    }

    private static RectTransform? GetListContent()
    {
        if (scrollContent == null)
        {
            return null;
        }

        if (scrollContent.Content != null)
        {
            return scrollContent.Content;
        }

        return scrollContent.transform.Find("Content") as RectTransform;
    }

    private static void EnsureListLayoutReady()
    {
        RectTransform? content = GetListContent();
        if (content == null)
        {
            return;
        }

        var contentLayout = content.GetComponent<VerticalLayoutGroup>();
        if (contentLayout != null)
        {
            // Content starts with VerticalLayoutGroup from PEAKLib; remove it to switch to grid layout.
            DestroyImmediate(contentLayout);
        }

        itemGridLayout = content.GetComponent<GridLayoutGroup>();
        if (itemGridLayout == null)
        {
            try
            {
                itemGridLayout = content.gameObject.AddComponent<GridLayoutGroup>();
            }
            catch (Exception e)
            {
                Log.LogError($"[ItemBrowser] Failed to add GridLayoutGroup: {e.GetType().Name} {e.Message}");
                return;
            }
        }

        if (itemGridLayout == null)
        {
            return;
        }

        itemGridLayout.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
        itemGridLayout.constraintCount = 2;
        itemGridLayout.spacing = new Vector2(12f, 8f);
        itemGridLayout.startAxis = GridLayoutGroup.Axis.Horizontal;
        itemGridLayout.startCorner = GridLayoutGroup.Corner.UpperLeft;
        itemGridLayout.childAlignment = TextAnchor.UpperLeft;
    }

    private static void ConfigureTabsContentLayout(Transform? tabsContent, float spacing, bool forceExpandWidth)
    {
        if (tabsContent == null)
        {
            return;
        }

        var layout = tabsContent.GetComponent<HorizontalLayoutGroup>();
        if (layout != null)
        {
            layout.spacing = spacing;
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.childControlWidth = true;
            layout.childForceExpandWidth = forceExpandWidth;
            layout.childControlHeight = true;
            layout.childForceExpandHeight = true;
        }

        var fitter = tabsContent.GetComponent<ContentSizeFitter>();
        if (fitter != null)
        {
            fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
        }
    }

    private static void UpdateItemGridCellSize()
    {
        EnsureListLayoutReady();

        RectTransform? contentRect = GetListContent();
        if (itemGridLayout == null || contentRect == null)
        {
            return;
        }

        float contentWidth = contentRect.rect.width;
        if (contentWidth <= 0f)
        {
            contentWidth = 680f;
        }

        int columns = Math.Max(1, itemGridLayout.constraintCount);
        float horizontalPadding = itemGridLayout.padding.left + itemGridLayout.padding.right;
        float spacing = itemGridLayout.spacing.x * (columns - 1);
        float width = (contentWidth - horizontalPadding - spacing) / columns;
        width = Mathf.Clamp(width, 320f, 420f);

        itemGridLayout.cellSize = new Vector2(width, 72f);
        itemGridLayout.padding = new RectOffset(4, 4, 4, 4);
    }

    private static void SetupListScrollbar(PeakElement listContainer)
    {
        if (scrollContent == null || listContainer == null)
        {
            return;
        }

        var scrollRect = scrollContent.GetComponent<ScrollRect>();
        if (scrollRect == null)
        {
            return;
        }

        var barObj = new GameObject("ListScrollbar", typeof(RectTransform), typeof(Image), typeof(Scrollbar));
        barObj.transform.SetParent(listContainer.transform, false);

        var barRect = barObj.GetComponent<RectTransform>();
        barRect.anchorMin = new Vector2(1f, 0f);
        barRect.anchorMax = new Vector2(1f, 1f);
        barRect.pivot = new Vector2(1f, 1f);
        barRect.offsetMin = new Vector2(-12f, 12f);
        barRect.offsetMax = new Vector2(-4f, -12f);

        var barBg = barObj.GetComponent<Image>();
        barBg.color = new Color(0.18f, 0.14f, 0.1f, 0.95f);
        barBg.raycastTarget = true;

        var slidingArea = new GameObject("Sliding Area", typeof(RectTransform));
        slidingArea.transform.SetParent(barObj.transform, false);

        var slidingRect = slidingArea.GetComponent<RectTransform>();
        slidingRect.anchorMin = Vector2.zero;
        slidingRect.anchorMax = Vector2.one;
        slidingRect.offsetMin = new Vector2(1f, 1f);
        slidingRect.offsetMax = new Vector2(-1f, -1f);

        var handleObj = new GameObject("Handle", typeof(RectTransform), typeof(Image));
        handleObj.transform.SetParent(slidingArea.transform, false);

        var handleRect = handleObj.GetComponent<RectTransform>();
        handleRect.anchorMin = new Vector2(0f, 0f);
        handleRect.anchorMax = new Vector2(1f, 1f);
        handleRect.offsetMin = Vector2.zero;
        handleRect.offsetMax = Vector2.zero;

        var handleImage = handleObj.GetComponent<Image>();
        handleImage.color = new Color(0.92f, 0.88f, 0.8f, 0.95f);
        handleImage.raycastTarget = true;

        listScrollbar = barObj.GetComponent<Scrollbar>();
        listScrollbar.direction = Scrollbar.Direction.BottomToTop;
        listScrollbar.handleRect = handleRect;
        listScrollbar.targetGraphic = handleImage;
        listScrollbar.size = 0.2f;

        scrollRect.verticalScrollbar = listScrollbar;
        scrollRect.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;
        scrollRect.verticalScrollbarSpacing = 2f;
    }

    private static void OnSearchChanged(string query)
    {
        currentSearch = query ?? string.Empty;
        MarkListDirty();
        RefreshList();
    }

    private static void MarkListDirty(string? reason = null)
    {
        listNeedsRefresh = true;
        listRenderRunning = false;
        listRenderGeneration++;

        if (listRenderCoroutine != null && instance != null)
        {
            instance.StopCoroutine(listRenderCoroutine);
            listRenderCoroutine = null;
        }

        firstOpenPrimed = false;
        postSpawnPrimeLocked = false;

        if (!string.IsNullOrWhiteSpace(reason))
        {
            VerboseLog($"List marked dirty: {reason}");
        }
    }

    private static void RefreshListIfNeeded(bool force = false)
    {
        if (force || listNeedsRefresh)
        {
            RefreshList();
        }
    }

    private static void RefreshList()
    {
        if (scrollContent == null)
        {
            return;
        }

        UpdateItemGridCellSize();

        RectTransform? listContent = GetListContent();
        if (listContent == null)
        {
            Log.LogWarning("[ItemBrowser] Scroll content not ready yet.");
            return;
        }

        CancelListRender();
        ClearContent(listContent);
        ClearStatusTextOverlay();

        if (!itemListInitialized)
        {
            EnsureItemList();
            AddStatusText(GetPreloadStatusText());
            return;
        }

        IEnumerable<ItemEntry> filtered = itemEntries;
        string search = currentSearch.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(search))
        {
            filtered = filtered.Where(entry => entry.SearchText.Contains(search));
        }

        filtered = filtered.Where(entry => IsEntryInMajorCategory(entry, currentMajorFilter));

        if (currentSubCategoryFilter.HasValue)
        {
            filtered = filtered.Where(entry => entry.Category == currentSubCategoryFilter.Value);
        }

        List<ItemEntry> filteredList = filtered.ToList();
        if (filteredList.Count == 0)
        {
            AddStatusText(GetText("STATUS_EMPTY"));
            listNeedsRefresh = false;
            listRenderRunning = false;
            return;
        }

        if (currentSubCategoryFilter.HasValue)
        {
            filteredList = filteredList.OrderBy(entry => entry.DisplayName).ToList();
        }

        listNeedsRefresh = false;
        StartListRender(listContent, filteredList);
    }

    private static void StartListRender(RectTransform listContent, List<ItemEntry> entries)
    {
        if (listContent == null)
        {
            return;
        }

        int generation = ++listRenderGeneration;
        if (instance == null)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                AddItemButton(entries[i]);
            }

            listRenderRunning = false;
            return;
        }

        listRenderRunning = true;
        listRenderCoroutine = instance.StartCoroutine(RenderListGradually(generation, entries));
    }

    private static IEnumerator RenderListGradually(int generation, List<ItemEntry> entries)
    {
        const int itemsPerFrame = 10;
        int budget = itemsPerFrame;

        for (int i = 0; i < entries.Count; i++)
        {
            if (generation != listRenderGeneration)
            {
                yield break;
            }

            AddItemButton(entries[i]);

            budget--;
            if (budget <= 0)
            {
                budget = itemsPerFrame;
                yield return null;
            }
        }

        if (generation == listRenderGeneration)
        {
            listRenderRunning = false;
            listRenderCoroutine = null;
        }
    }

    private static void CancelListRender()
    {
        listRenderGeneration++;

        if (listRenderCoroutine != null && instance != null)
        {
            instance.StopCoroutine(listRenderCoroutine);
            listRenderCoroutine = null;
        }

        listRenderRunning = false;
    }

    private static void AddCategoryHeader(ItemCategory category)
    {
        if (scrollContent == null) return;

        RectTransform? listContent = GetListContent();
        if (listContent == null)
        {
            return;
        }

        string text = GetCategoryLabel(category);
        MenuAPI.CreateText(text)
            .SetFontSize(22)
            .ParentTo(listContent);
    }

    private static void AddItemButton(ItemEntry entry)
    {
        if (scrollContent == null) return;

        RectTransform? listContent = GetListContent();
        if (listContent == null)
        {
            return;
        }

        string display = entry.DisplayName;

        var button = MenuAPI
            .CreateMenuButton(display)
            .ParentTo(listContent)
            .OnClick(() => SpawnItem(entry.Prefab));

        var rect = button.GetComponent<RectTransform>();
        if (rect != null)
        {
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(0f, 72f);
            rect.localScale = Vector3.one;
        }

        var layout = button.gameObject.GetComponent<LayoutElement>();
        if (layout == null)
        {
            layout = button.gameObject.AddComponent<LayoutElement>();
        }
        layout.preferredHeight = 72f;
        layout.minHeight = 72f;
        layout.flexibleHeight = 0f;
        layout.preferredWidth = 0f;
        layout.minWidth = 0f;
        layout.flexibleWidth = 0f;

        Sprite? icon = ResolveEntryIcon(entry);
        AddItemIcon(button, icon);
        ApplyItemButtonStyle(button);
        NormalizeButtonLayout(button);
    }

    private static Sprite? ResolveEntryIcon(ItemEntry entry)
    {
        if (entry.Icon != null)
        {
            return entry.Icon;
        }

        // Keep preload lightweight: do expensive icon fallback only when an item is actually visible.
        Sprite? icon = GetItemIcon(entry.Prefab, allowHeavyFallback: true);
        if (icon != null)
        {
            entry.UpdateIcon(icon);
        }

        return icon;
    }

    private static void ApplyItemButtonStyle(PeakMenuButton button)
    {
        if (button == null) return;

        Color bg = new Color(0.18f, 0.14f, 0.1f, 0.96f);
        Color border = new Color(0.6f, 0.52f, 0.42f, 0.6f);
        Color text = new Color(0.95f, 0.92f, 0.86f, 1f);

        button.SetColor(bg, false);
        button.SetBorderColor(border);

        if (button.Button != null && button.Panel != null)
        {
            button.Button.targetGraphic = button.Panel;

            var colors = button.Button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = Color.white;
            colors.pressedColor = new Color(0.92f, 0.88f, 0.8f, 1f);
            colors.selectedColor = Color.white;
            colors.disabledColor = new Color(1f, 1f, 1f, 0.35f);
            button.Button.colors = colors;
        }

        if (button.Text != null)
        {
            button.Text.color = text;
            button.Text.fontSize = 20;
            button.Text.enableAutoSizing = false;
            button.Text.alignment = TMPro.TextAlignmentOptions.MidlineLeft;
            button.Text.margin = new Vector4(78f, 0f, 12f, 0f);
            button.Text.raycastTarget = false;
        }
    }

    private static void AddItemIcon(PeakMenuButton button, Sprite? icon)
    {
        if (button == null)
        {
            return;
        }

        var iconObj = new GameObject("ItemIcon", typeof(RectTransform), typeof(Image));
        iconObj.transform.SetParent(button.transform, false);

        var iconRect = iconObj.GetComponent<RectTransform>();
        iconRect.anchorMin = new Vector2(0f, 0.5f);
        iconRect.anchorMax = new Vector2(0f, 0.5f);
        iconRect.pivot = new Vector2(0f, 0.5f);
        iconRect.anchoredPosition = new Vector2(14f, 0f);
        iconRect.sizeDelta = new Vector2(48f, 48f);

        var iconImage = iconObj.GetComponent<Image>();
        iconImage.sprite = icon;
        iconImage.preserveAspect = true;
        iconImage.raycastTarget = false;

        if (icon != null)
        {
            iconImage.color = Color.white;
        }
        else
        {
            iconImage.color = new Color(0.48f, 0.42f, 0.35f, 0.45f);
        }
    }

    private static void ClearStatusTextOverlay()
    {
        if (scrollContent == null)
        {
            return;
        }

        Transform? existing = scrollContent.transform.Find("StatusOverlay");
        if (existing != null)
        {
            Destroy(existing.gameObject);
        }
    }

    private static void AddStatusText(string text)
    {
        if (scrollContent == null)
        {
            return;
        }

        ClearStatusTextOverlay();

        var overlay = new GameObject("StatusOverlay", typeof(RectTransform));
        overlay.transform.SetParent(scrollContent.transform, false);

        var overlayRect = overlay.GetComponent<RectTransform>();
        overlayRect.anchorMin = Vector2.zero;
        overlayRect.anchorMax = Vector2.one;
        overlayRect.offsetMin = Vector2.zero;
        overlayRect.offsetMax = Vector2.zero;

        var statusText = MenuAPI.CreateText(text)
            .SetFontSize(30)
            .ParentTo(overlay.transform)
            .ExpandToParent();

        statusText.TextMesh.alignment = TMPro.TextAlignmentOptions.Center;
        statusText.TextMesh.color = new Color(0.9f, 0.86f, 0.78f, 1f);
        statusText.TextMesh.enableAutoSizing = false;
        statusText.TextMesh.raycastTarget = false;
    }

    private static void AddSpacer(float height)
    {
        if (scrollContent == null) return;

        RectTransform? listContent = GetListContent();
        if (listContent == null)
        {
            return;
        }

        var spacer = new GameObject("Spacer", typeof(RectTransform), typeof(LayoutElement));
        var layout = spacer.GetComponent<LayoutElement>();
        layout.minHeight = height;
        layout.preferredHeight = height;
        spacer.transform.SetParent(listContent, false);
    }

    private static void ClearContent(Transform content)
    {
        for (int i = content.childCount - 1; i >= 0; i--)
        {
            Destroy(content.GetChild(i).gameObject);
        }
    }

    private static void EnsureItemList()
    {
        if (itemListInitialized || itemPreloadRunning)
        {
            return;
        }

        TryStartBackgroundItemPreload("EnsureItemList");
    }

    private static void TickBackgroundItemPreload()
    {
        if (itemListInitialized || itemPreloadRunning)
        {
            return;
        }

        if (Time.unscaledTime < nextPreloadCheckTime)
        {
            return;
        }

        nextPreloadCheckTime = Time.unscaledTime + 0.1f;
        TryStartBackgroundItemPreload("AutoWarmup");
    }

    private static void TickHiddenFirstOpenPrime()
    {
        if (firstOpenPrimed || pageOpen || !uiBuilt || scrollContent == null)
        {
            return;
        }

        if (!itemListInitialized || itemPreloadRunning)
        {
            return;
        }

        if (Character.localCharacter != null)
        {
            return;
        }

        if (Time.unscaledTime < nextHiddenPrimeCheckTime)
        {
            return;
        }

        nextHiddenPrimeCheckTime = Time.unscaledTime + 0.25f;

        RefreshLanguageDependentContent(force: false);
        RefreshListIfNeeded();

        // Prime MenuWindow open/close before player spawns so first manual F5 is already warmed up.
        if (!hiddenMenuWindowPrimed && Character.localCharacter == null)
        {
            PrimeMenuWindowOpenClose();
        }

        if (!listNeedsRefresh && !listRenderRunning)
        {
            firstOpenPrimed = true;
            VerboseLog("Hidden first-open cache primed.");
        }
    }

    private static void PrimeMenuWindowOpenClose()
    {
        if (hiddenMenuWindowPrimed || page == null || pageOpen)
        {
            return;
        }

        try
        {
            OpenPage();
            ClosePage();
            hiddenMenuWindowPrimed = true;
            VerboseLog("Menu window warmup completed before first manual F5.");
        }
        catch (Exception e)
        {
            VerboseLog($"Menu window warmup failed: {e.GetType().Name} {e.Message}");
        }
    }

    private static void TickPostSpawnPrimeLock()
    {
        if (postSpawnPrimeLocked || pageOpen || !uiBuilt || scrollContent == null)
        {
            return;
        }

        if (Character.localCharacter == null)
        {
            return;
        }

        if (!itemListInitialized || itemPreloadRunning)
        {
            return;
        }

        if (Time.unscaledTime < nextPostSpawnPrimeCheckTime)
        {
            return;
        }

        nextPostSpawnPrimeCheckTime = Time.unscaledTime + 0.25f;

        RefreshLanguageDependentContent(force: false);
        RefreshListIfNeeded();

        if (!hiddenMenuWindowPrimed)
        {
            PrimeMenuWindowOpenClose();
        }

        if (!listNeedsRefresh && !listRenderRunning && hiddenMenuWindowPrimed)
        {
            postSpawnPrimeLocked = true;
            VerboseLog("Post-spawn prime lock completed.");
        }
    }

    private static bool TryStartBackgroundItemPreload(string reason)
    {
        if (itemListInitialized || itemPreloadRunning)
        {
            return false;
        }

        if (instance == null)
        {
            return false;
        }

        var db = SingletonAsset<ItemDatabase>.Instance;
        if (db == null || db.Objects == null || db.Objects.Count == 0)
        {
            return false;
        }

        itemEntries.Clear();
        itemIconCache.Clear();

        MarkListDirty($"Item preload started ({reason})");

        itemPreloadTotalCount = db.Objects.Count;
        itemPreloadProcessedCount = 0;
        itemPreloadAddedCount = 0;
        itemPreloadRunning = true;
        itemNamesLanguageIndex = GetCurrentLanguageIndex();
        itemNamesLanguageMarker = BuildLanguageMarker();
        int dbId = db.GetInstanceID();
        preloadingDatabaseId = dbId;
        loadedDatabaseId = 0;
        itemPreloadCoroutine = instance.StartCoroutine(BuildItemListGradually(db, dbId));

        VerboseLog($"Item preload started ({reason}). Total={itemPreloadTotalCount}");
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
            itemPreloadProcessedCount++;

            try
            {
                if (item != null)
                {
                    string localizedName = GetLocalizedItemName(item);
                    string displayName = string.IsNullOrWhiteSpace(localizedName) ? item.name : localizedName;
                    displayName = GetDisplayNameOverride(item, displayName);

                    if (!ShouldHideItemFromBrowser(item, displayName))
                    {
                        ItemCategory category = GetCategory(item, displayName);
                        Sprite? icon = GetItemIcon(item, allowHeavyFallback: false);
                        itemEntries.Add(new ItemEntry(item, displayName, category, icon));
                        itemPreloadAddedCount++;
                    }
                    else
                    {
                        VerboseLog($"Item hidden: prefab='{item.name}', display='{displayName}'");
                    }
                }
            }
            catch (Exception e)
            {
                Log.LogWarning($"[ItemBrowser] Preload item failed: {item?.name} ({e.GetType().Name} {e.Message})");
            }

            budget--;
            if (budget <= 0)
            {
                budget = itemsPerFrame;
                yield return null;
            }
        }

        itemPreloadRunning = false;
        itemPreloadCoroutine = null;
        itemListInitialized = true;
        loadedDatabaseId = preloadingDatabaseId != 0 ? preloadingDatabaseId : dbId;
        preloadingDatabaseId = 0;

        if (configVerboseLogs != null && configVerboseLogs.Value)
        {
            var breakdown = itemEntries
                .GroupBy(entry => entry.Category)
                .OrderBy(group => GetCategoryOrder(group.Key))
                .Select(group => $"{GetCategoryLabel(group.Key)}={group.Count()}");
            Log.LogInfo($"[ItemBrowser] Item list built in background. Total={itemEntries.Count}, Added={itemPreloadAddedCount}, Categories: {string.Join(", ", breakdown)}");
        }

        MarkListDirty("Item preload completed");

        if (pageOpen)
        {
            RefreshListIfNeeded();
        }
    }

    private static string GetPreloadStatusText()
    {
        if (itemPreloadRunning)
        {
            if (itemPreloadTotalCount > 0)
            {
                return $"{GetText("STATUS_LOADING")} {itemPreloadProcessedCount}/{itemPreloadTotalCount}";
            }

            return GetText("STATUS_LOADING");
        }

        return GetText("STATUS_NOT_READY");
    }

    private static Sprite? GetItemIcon(Item item, bool allowHeavyFallback = true)
    {
        if (item == null)
        {
            return null;
        }

        string key = item.name ?? string.Empty;
        if (itemIconCache.TryGetValue(key, out Sprite? cached))
        {
            return cached;
        }

        Sprite? icon = null;
        List<string>? probe = configVerboseLogs != null && configVerboseLogs.Value ? new List<string>() : null;
        var visited = new HashSet<int>();

        try
        {
            icon = TryExtractUiDataIcon(item, probe);

            if (icon == null)
            {
                icon = TryExtractSprite(item, probe, "Item", 0, visited);
            }

            if (icon == null)
            {
                Component[] components = item.GetComponents<Component>();
                for (int i = 0; i < components.Length && icon == null; i++)
                {
                    Component component = components[i];
                    if (component == null)
                    {
                        continue;
                    }

                    icon = TryExtractSprite(component, probe, component.GetType().Name, 0, visited);
                }
            }

            if (icon == null)
            {
                var spriteRenderer = item.GetComponentInChildren<SpriteRenderer>(true);
                if (spriteRenderer != null)
                {
                    icon = spriteRenderer.sprite;
                    if (probe != null)
                    {
                        probe.Add(icon != null
                            ? $"SpriteRenderer(sprite)={DescribeSprite(icon)}"
                            : "SpriteRenderer(sprite)=null");
                    }
                }
                else if (probe != null)
                {
                    probe.Add("SpriteRenderer=missing");
                }
            }

            if (allowHeavyFallback && icon == null)
            {
                icon = TryFindTextureByName(item, probe);
            }

            if (allowHeavyFallback && icon == null)
            {
                icon = TryExtractMaterialTextureSprite(item, probe);
            }

            if (icon == null)
            {
                var uiImage = item.GetComponentInChildren<Image>(true);
                if (uiImage != null)
                {
                    icon = uiImage.sprite;
                    if (probe != null)
                    {
                        probe.Add(icon != null
                            ? $"UI.Image(sprite)={DescribeSprite(icon)}"
                            : "UI.Image(sprite)=null");
                    }
                }
                else if (probe != null)
                {
                    probe.Add("UI.Image=missing");
                }
            }
        }
        catch (Exception e)
        {
            VerboseLog($"GetItemIcon failed for {item.name}: {e.GetType().Name} {e.Message}");
        }

        if (probe != null)
        {
            if (icon != null)
            {
                VerboseLog($"Icon resolved for '{item.name}': {DescribeSprite(icon)} | {FormatProbe(probe)}");
            }
            else
            {
                Log.LogWarning($"[ItemBrowser] Icon missing for '{item.name}'. Probe: {FormatProbe(probe)}");
            }
        }

        itemIconCache[key] = icon;
        return icon;
    }

    private static Sprite? TryExtractUiDataIcon(Item item, List<string>? probe)
    {
        if (item == null)
        {
            return null;
        }

        try
        {
            Item.ItemUIData uiData = item.UIData;

            Texture2D? iconTexture = uiData.icon;
            if (iconTexture != null)
            {
                return ConvertTextureToSprite(iconTexture, "Item.UIData.icon", probe);
            }

            if (uiData.hasAltIcon && uiData.altIcon != null)
            {
                return ConvertTextureToSprite(uiData.altIcon, "Item.UIData.altIcon", probe);
            }

            probe?.Add("Item.UIData.icon=null");
        }
        catch (Exception e)
        {
            probe?.Add($"Item.UIData=failed({e.GetType().Name})");
        }

        return null;
    }

    private static Sprite? TryExtractSprite(
        object target,
        List<string>? probe = null,
        string? source = null,
        int depth = 0,
        HashSet<int>? visited = null)
    {
        if (target == null || depth > 2)
        {
            return null;
        }

        visited ??= new HashSet<int>();

        if (target is not string && !target.GetType().IsValueType)
        {
            int token = RuntimeHelpers.GetHashCode(target);
            if (!visited.Add(token))
            {
                return null;
            }
        }

        string sourceName = string.IsNullOrWhiteSpace(source) ? target.GetType().Name : source;

        if (TryExtractSpriteFromValue(target, sourceName, probe, depth, visited, out Sprite? directSprite))
        {
            return directSprite;
        }

        Type type = target.GetType();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        string[] likelyMembers =
        {
            "Icon", "icon", "ItemIcon", "itemIcon", "IconSprite", "iconSprite",
            "Sprite", "sprite", "Thumbnail", "thumbnail", "Icons", "icons",
            "ItemIcons", "itemIcons", "Atlas", "atlas", "Texture", "texture"
        };

        for (int i = 0; i < likelyMembers.Length; i++)
        {
            string member = likelyMembers[i];

            var field = type.GetField(member, flags);
            if (field != null)
            {
                object? value = null;
                try
                {
                    value = field.GetValue(target);
                }
                catch
                {
                    // ignore getter failures
                }

                if (TryExtractSpriteFromValue(value, $"{sourceName}.{field.Name}", probe, depth + 1, visited, out Sprite? spriteFromField))
                {
                    return spriteFromField;
                }

                probe?.Add($"{sourceName}.{field.Name}<{field.FieldType.Name}>={DescribeProbeValue(value)}");
            }

            var prop = type.GetProperty(member, flags);
            if (prop != null && prop.CanRead && prop.GetIndexParameters().Length == 0)
            {
                object? value = null;
                try
                {
                    value = prop.GetValue(target);
                }
                catch
                {
                    value = "<get_error>";
                }

                if (TryExtractSpriteFromValue(value, $"{sourceName}.{prop.Name}", probe, depth + 1, visited, out Sprite? spriteFromProp))
                {
                    return spriteFromProp;
                }

                probe?.Add($"{sourceName}.{prop.Name}<{prop.PropertyType.Name}>={DescribeProbeValue(value)}");
            }
        }

        foreach (var field in type.GetFields(flags))
        {
            if (!IsIconProbeMember(field.Name, field.FieldType))
            {
                continue;
            }

            object? value = null;
            try
            {
                value = field.GetValue(target);
            }
            catch
            {
                // ignored on purpose
            }

            if (TryExtractSpriteFromValue(value, $"{sourceName}.{field.Name}", probe, depth + 1, visited, out Sprite? sprite))
            {
                return sprite;
            }

            probe?.Add($"{sourceName}.{field.Name}<{field.FieldType.Name}>={DescribeProbeValue(value)}");
        }

        foreach (var prop in type.GetProperties(flags))
        {
            if (!prop.CanRead || prop.GetIndexParameters().Length != 0)
            {
                continue;
            }

            if (!IsIconProbeMember(prop.Name, prop.PropertyType))
            {
                continue;
            }

            object? value = null;
            try
            {
                value = prop.GetValue(target);
            }
            catch
            {
                value = "<get_error>";
            }

            if (TryExtractSpriteFromValue(value, $"{sourceName}.{prop.Name}", probe, depth + 1, visited, out Sprite? sprite))
            {
                return sprite;
            }

            probe?.Add($"{sourceName}.{prop.Name}<{prop.PropertyType.Name}>={DescribeProbeValue(value)}");
        }

        return null;
    }

    private static bool TryExtractSpriteFromValue(
        object? value,
        string source,
        List<string>? probe,
        int depth,
        HashSet<int> visited,
        out Sprite? sprite)
    {
        sprite = null;

        if (value == null)
        {
            return false;
        }

        if (value is Sprite directSprite)
        {
            probe?.Add($"{source}={DescribeSprite(directSprite)}");
            sprite = directSprite;
            return true;
        }

        if (value is Texture2D texture2D)
        {
            sprite = ConvertTextureToSprite(texture2D, source, probe);
            return sprite != null;
        }

        if (value is Texture texture)
        {
            if (texture is Texture2D asTexture2D)
            {
                sprite = ConvertTextureToSprite(asTexture2D, source, probe);
                return sprite != null;
            }

            probe?.Add($"{source}=texture('{texture.name}') unsupported:{texture.GetType().Name}");
            return false;
        }

        if (value is IEnumerable enumerable && value is not string && ShouldEnumerateIconCollection(source, value))
        {
            int index = 0;
            foreach (object? element in enumerable)
            {
                if (TryExtractSpriteFromValue(element, $"{source}[{index}]", probe, depth + 1, visited, out sprite))
                {
                    return true;
                }

                index++;
                if (index >= 8)
                {
                    break;
                }
            }

            return false;
        }

        if (depth >= 2)
        {
            return false;
        }

        Type valueType = value.GetType();
        if (valueType.IsPrimitive || valueType.IsEnum || value is string || value is decimal)
        {
            return false;
        }

        if (!ShouldDeepProbeSource(source, valueType))
        {
            return false;
        }

        sprite = TryExtractSprite(value, probe, source, depth + 1, visited);
        return sprite != null;
    }

    private static Sprite? ConvertTextureToSprite(Texture2D texture, string source, List<string>? probe)
    {
        if (texture == null)
        {
            return null;
        }

        int id = texture.GetInstanceID();
        if (generatedTextureSpriteCache.TryGetValue(id, out Sprite cached) && cached != null)
        {
            probe?.Add($"{source}=cached:{DescribeSprite(cached)}");
            return cached;
        }

        if (texture.width <= 0 || texture.height <= 0)
        {
            probe?.Add($"{source}=texture('{texture.name}') invalid_size");
            return null;
        }

        try
        {
            var created = Sprite.Create(
                texture,
                new Rect(0f, 0f, texture.width, texture.height),
                new Vector2(0.5f, 0.5f),
                100f,
                0,
                SpriteMeshType.FullRect);

            created.name = $"{texture.name}_AutoIcon";
            generatedTextureSpriteCache[id] = created;
            probe?.Add($"{source}=texture->sprite:{DescribeSprite(created)}");
            return created;
        }
        catch (Exception e)
        {
            probe?.Add($"{source}=texture_convert_failed({e.GetType().Name})");
            return null;
        }
    }

    private static Sprite? TryExtractMaterialTextureSprite(Item item, List<string>? probe)
    {
        if (item == null)
        {
            return null;
        }

        var renderers = item.GetComponentsInChildren<Renderer>(true);
        if (renderers == null || renderers.Length == 0)
        {
            probe?.Add("Renderer=missing");
            return null;
        }

        Texture2D? bestTexture = null;
        string bestSource = string.Empty;
        int bestScore = int.MinValue;

        for (int i = 0; i < renderers.Length; i++)
        {
            var renderer = renderers[i];
            if (renderer == null)
            {
                continue;
            }

            if (ShouldSkipRendererForIcon(renderer))
            {
                probe?.Add($"Renderer({renderer.GetType().Name}:{renderer.name})=skip");
                continue;
            }

            Material[] materials = renderer.sharedMaterials;
            if (materials == null || materials.Length == 0)
            {
                probe?.Add($"Renderer({renderer.GetType().Name}:{renderer.name}).sharedMaterials=empty");
                continue;
            }

            for (int m = 0; m < materials.Length; m++)
            {
                var material = materials[m];
                if (material == null)
                {
                    continue;
                }

                if (ShouldSkipMaterialForIcon(material))
                {
                    probe?.Add($"Renderer({renderer.GetType().Name}:{renderer.name}).Material({material.name})=skip");
                    continue;
                }

                string sourcePrefix = $"Renderer({renderer.GetType().Name}:{renderer.name}).Material({material.name})";
                EvaluateTextureCandidate(item.name, material.mainTexture as Texture2D, $"{sourcePrefix}.mainTexture", ref bestTexture, ref bestSource, ref bestScore, probe);

                string[] textureProps;
                try
                {
                    textureProps = material.GetTexturePropertyNames();
                }
                catch
                {
                    textureProps = Array.Empty<string>();
                }

                for (int t = 0; t < textureProps.Length; t++)
                {
                    string prop = textureProps[t];
                    if (string.IsNullOrWhiteSpace(prop))
                    {
                        continue;
                    }

                    Texture2D? tex = null;
                    try
                    {
                        tex = material.GetTexture(prop) as Texture2D;
                    }
                    catch
                    {
                        // ignored
                    }

                    EvaluateTextureCandidate(item.name, tex, $"{sourcePrefix}.{prop}", ref bestTexture, ref bestSource, ref bestScore, probe);
                }
            }
        }

        if (bestTexture == null)
        {
            probe?.Add("MaterialTexture=no_candidate");
            return null;
        }

        if (bestScore < 80)
        {
            probe?.Add($"MaterialTexture.skip(score={bestScore})");
            return null;
        }

        probe?.Add($"MaterialTexture=choose(score={bestScore}):{bestSource}");
        return ConvertTextureToSprite(bestTexture, bestSource, probe);
    }

    private static bool ShouldSkipRendererForIcon(Renderer renderer)
    {
        if (renderer == null)
        {
            return true;
        }

        string lowered = renderer.name?.ToLowerInvariant() ?? string.Empty;
        return lowered.Contains("hand")
            || lowered.Contains("arm")
            || lowered.Contains("player")
            || lowered.Contains("viewmodel")
            || lowered.Contains("firstperson")
            || lowered.Contains("fp_")
            || lowered.Contains("character");
    }

    private static bool ShouldSkipMaterialForIcon(Material material)
    {
        if (material == null)
        {
            return true;
        }

        string lowered = material.name?.ToLowerInvariant() ?? string.Empty;
        return lowered.Contains("m_player")
            || lowered.Contains("player")
            || lowered.Contains("hand")
            || lowered.Contains("skin")
            || lowered.Contains("hair")
            || lowered.Contains("eye")
            || lowered.Contains("face");
    }

    private static Sprite? TryFindTextureByName(Item item, List<string>? probe)
    {
        if (item == null)
        {
            return null;
        }

        EnsureTextureNameIndex();
        if (textureNameIndex.Count == 0)
        {
            probe?.Add("TextureNameIndex=empty");
            return null;
        }

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string prefabName = item.name ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(prefabName))
        {
            keys.Add(prefabName);
            keys.Add(prefabName.Replace("_", " "));
            int variantIndex = prefabName.IndexOf(" Variant", StringComparison.OrdinalIgnoreCase);
            if (variantIndex > 0)
            {
                keys.Add(prefabName.Substring(0, variantIndex));
            }
        }

        Texture2D? bestTexture = null;
        int bestScore = int.MinValue;
        string bestSource = string.Empty;

        foreach (string raw in keys)
        {
            string token = NormalizeLookupToken(raw);
            if (string.IsNullOrEmpty(token))
            {
                continue;
            }

            if (!textureNameIndex.TryGetValue(token, out var textures) || textures == null)
            {
                continue;
            }

            for (int i = 0; i < textures.Count; i++)
            {
                Texture2D tex = textures[i];
                if (tex == null)
                {
                    continue;
                }

                int score = ScoreTextureCandidate(prefabName, tex, $"TextureNameIndex.{token}");
                if (IsLikelyGenericTextureName(tex.name))
                {
                    score -= 120;
                }

                score += 220; // strong boost for exact name-token matches.
                probe?.Add($"TextureNameIndex.{token}=texture('{tex.name}') score={score}");

                if (score > bestScore)
                {
                    bestScore = score;
                    bestTexture = tex;
                    bestSource = $"TextureNameIndex.{token}";
                }
            }
        }

        if (bestTexture == null)
        {
            return null;
        }

        if (bestScore < 120)
        {
            probe?.Add($"TextureNameIndex.skip(score={bestScore})");
            return null;
        }

        probe?.Add($"TextureNameIndex.choose(score={bestScore}):{bestSource}");
        return ConvertTextureToSprite(bestTexture, bestSource, probe);
    }

    private static void EnsureTextureNameIndex()
    {
        if (textureNameIndexBuilt)
        {
            return;
        }

        textureNameIndexBuilt = true;
        textureNameIndex.Clear();

        try
        {
            Texture2D[] textures = Resources.FindObjectsOfTypeAll<Texture2D>();
            if (textures == null || textures.Length == 0)
            {
                return;
            }

            for (int i = 0; i < textures.Length; i++)
            {
                Texture2D texture = textures[i];
                if (texture == null || string.IsNullOrWhiteSpace(texture.name))
                {
                    continue;
                }

                string token = NormalizeLookupToken(texture.name);
                if (string.IsNullOrEmpty(token))
                {
                    continue;
                }

                if (!textureNameIndex.TryGetValue(token, out var list))
                {
                    list = new List<Texture2D>();
                    textureNameIndex[token] = list;
                }

                list.Add(texture);
            }

            VerboseLog($"TextureNameIndex built: {textureNameIndex.Count} keys");
        }
        catch (Exception e)
        {
            Log.LogWarning($"[ItemBrowser] Failed to build texture index: {e.GetType().Name} {e.Message}");
        }
    }

    private static string NormalizeLookupToken(string value)
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

    private static void EvaluateTextureCandidate(
        string itemName,
        Texture2D? texture,
        string source,
        ref Texture2D? bestTexture,
        ref string bestSource,
        ref int bestScore,
        List<string>? probe)
    {
        if (texture == null)
        {
            probe?.Add($"{source}=null");
            return;
        }

        int score = ScoreTextureCandidate(itemName, texture, source);
        probe?.Add($"{source}=texture('{texture.name}') score={score}");

        if (score > bestScore)
        {
            bestScore = score;
            bestTexture = texture;
            bestSource = source;
        }
    }

    private static bool IsLikelyGenericTextureName(string textureName)
    {
        if (string.IsNullOrWhiteSpace(textureName))
        {
            return true;
        }

        string lowered = textureName.ToLowerInvariant();
        return lowered.StartsWith("a_texture")
            || lowered.StartsWith("a_paint")
            || lowered.StartsWith("a_noise")
            || lowered.Contains("noise")
            || lowered.Contains("default")
            || lowered.Contains("gradient")
            || lowered.Contains("cloud")
            || lowered.Contains("checker");
    }

    private static int ScoreTextureCandidate(string itemName, Texture2D texture, string source)
    {
        string sourceLower = source.ToLowerInvariant();
        string textureName = texture.name ?? string.Empty;
        string textureLower = textureName.ToLowerInvariant();
        string itemLower = (itemName ?? string.Empty).ToLowerInvariant();

        int score = 0;

        if (sourceLower.Contains("icon") || sourceLower.Contains("thumb")) score += 120;
        if (textureLower.Contains("icon") || textureLower.Contains("thumb")) score += 100;
        if (sourceLower.Contains("atlas")) score += 25;

        string itemToken = new string(itemLower.Where(char.IsLetterOrDigit).ToArray());
        string textureToken = new string(textureLower.Where(char.IsLetterOrDigit).ToArray());
        if (!string.IsNullOrEmpty(itemToken) && textureToken.Contains(itemToken)) score += 90;

        if (sourceLower.Contains("_basemap") || sourceLower.Contains("_maintex") || sourceLower.Contains("_basetexture")) score += 15;

        if (texture.width <= 1024 && texture.height <= 1024) score += 20;
        if (texture.width <= 512 && texture.height <= 512) score += 20;
        if (texture.width <= 256 && texture.height <= 256) score += 10;

        if (sourceLower.Contains("renderer(meshrenderer:hand)") || sourceLower.Contains("material(m_player)")) score -= 300;

        if (textureLower.StartsWith("a_texture") || textureLower.StartsWith("a_paint") || textureLower.StartsWith("a_noise")) score -= 80;
        if (textureLower.Contains("default") || textureLower.Contains("fallback") || textureLower.Contains("noise")) score -= 60;
        if (textureLower.Contains("soft noise") || textureLower == "t_leaves" || textureLower.Contains("leaves") || textureLower.Contains("foliage")) score -= 80;

        return score;
    }

    private static bool ShouldEnumerateIconCollection(string source, object value)
    {
        if (value == null)
        {
            return false;
        }

        if (value is Array array)
        {
            Type elementType = array.GetType().GetElementType() ?? typeof(object);
            if (typeof(Sprite).IsAssignableFrom(elementType) || typeof(Texture).IsAssignableFrom(elementType))
            {
                return true;
            }
        }

        return source.IndexOf("icon", StringComparison.OrdinalIgnoreCase) >= 0
            || source.IndexOf("sprite", StringComparison.OrdinalIgnoreCase) >= 0
            || source.IndexOf("thumb", StringComparison.OrdinalIgnoreCase) >= 0
            || source.IndexOf("texture", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool ShouldDeepProbeSource(string source, Type valueType)
    {
        if (valueType == null)
        {
            return false;
        }

        if (typeof(Texture).IsAssignableFrom(valueType) || typeof(Sprite).IsAssignableFrom(valueType))
        {
            return true;
        }

        return source.IndexOf("icon", StringComparison.OrdinalIgnoreCase) >= 0
            || source.IndexOf("sprite", StringComparison.OrdinalIgnoreCase) >= 0
            || source.IndexOf("thumb", StringComparison.OrdinalIgnoreCase) >= 0
            || source.IndexOf("atlas", StringComparison.OrdinalIgnoreCase) >= 0
            || source.IndexOf("texture", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsIconProbeMember(string name, Type memberType)
    {
        if (memberType == null)
        {
            return false;
        }

        if (typeof(Sprite).IsAssignableFrom(memberType)
            || typeof(Texture).IsAssignableFrom(memberType)
            || memberType.IsArray)
        {
            return true;
        }

        string lowered = name?.ToLowerInvariant() ?? string.Empty;
        if (string.IsNullOrEmpty(lowered))
        {
            return false;
        }

        return lowered.Contains("icon")
            || lowered.Contains("sprite")
            || lowered.Contains("thumbnail")
            || lowered.Contains("atlas")
            || lowered.Contains("texture")
            || lowered.Contains("tex");
    }

    private static string DescribeSprite(Sprite sprite)
    {
        if (sprite == null)
        {
            return "sprite:null";
        }

        string texture = sprite.texture != null ? sprite.texture.name : "null";
        return $"sprite='{sprite.name}', texture='{texture}', rect={sprite.rect.width}x{sprite.rect.height}";
    }

    private static string DescribeProbeValue(object? value)
    {
        if (value == null)
        {
            return "null";
        }

        if (value is Sprite sprite)
        {
            return DescribeSprite(sprite);
        }

        if (value is Texture texture)
        {
            return $"texture='{texture.name}' ({texture.GetType().Name})";
        }

        if (value is IEnumerable enumerable && value is not string)
        {
            int count = 0;
            foreach (var _ in enumerable)
            {
                count++;
                if (count >= 12)
                {
                    break;
                }
            }

            return $"enumerable<{value.GetType().Name}> count~{count}";
        }

        if (value is UnityEngine.Object unityObject)
        {
            return $"unity='{unityObject.name}' ({unityObject.GetType().Name})";
        }

        string text = value.ToString() ?? value.GetType().Name;
        if (text.Length > 80)
        {
            text = text.Substring(0, 80) + "...";
        }

        return text;
    }

    private static string FormatProbe(List<string> probe)
    {
        if (probe == null || probe.Count == 0)
        {
            return "probe=empty";
        }

        const int maxEntries = 20;
        if (probe.Count <= maxEntries)
        {
            return string.Join(" | ", probe);
        }

        return string.Join(" | ", probe.Take(maxEntries)) + $" | ...(+{probe.Count - maxEntries})";
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
            string localized = ResolveLocalizedText("NAME_COOKED BIRD");
            if (!string.IsNullOrWhiteSpace(localized))
            {
                return localized;
            }

            return displayName;
        }

        return displayName;
    }

    private static bool ShouldHideItemFromBrowser(Item item, string displayName)
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
        if (hiddenPrefabNames.Contains(normalized))
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

    private static ItemCategory GetCategory(Item item, string displayName)
    {
        if (item == null)
        {
            return ItemCategory.MiscEquipment;
        }

        if (TryGetCategoryByComponent(item, out ItemCategory componentCategory))
        {
            if (TryGetCategoryOverride(item, displayName, out ItemCategory overrideCategory))
            {
                return overrideCategory;
            }

            return componentCategory;
        }

        if (TryGetCategoryOverride(item, displayName, out ItemCategory mappedCategory))
        {
            return mappedCategory;
        }

        if (IsNameKeyword(item, displayName, "berry", "berrynana", "clusterberry", "kingberry", "prickleberry", "shroomberry", "winterberry", "napberry", "scorchberry"))
        {
            return ItemCategory.NaturalFood;
        }

        if (IsNameKeyword(item, displayName, "mushroom", "fungus", "shroom"))
        {
            return ItemCategory.Mushroom;
        }

        if (IsNameKeyword(item, displayName, "packaged", "granola", "trail mix", "cookies", "airplane food", "ration"))
        {
            return ItemCategory.PackagedFood;
        }

        if (IsNameKeyword(item, displayName, "food", "coconut", "hot dog", "marshmallow", "egg", "honeycomb", "aloe"))
        {
            return ItemCategory.NaturalFood;
        }

        WarnFallbackCategory(item, displayName);
        return ItemCategory.MiscEquipment;
    }

    private static bool TryGetCategoryByComponent(Item item, out ItemCategory category)
    {
        category = default;
        if (item == null)
        {
            return false;
        }

        if (IsConsumable(item))
        {
            category = ItemCategory.Consumable;
            return true;
        }

        if (item.GetComponent<ItemCooking>() != null || HasKeywordComponent(item, "Cooking"))
        {
            category = ItemCategory.NaturalFood;
            return true;
        }

        if (HasAnyComponentKeyword(item, "Enemy", "Monster", "Creature", "Scorpion", "Beehive"))
        {
            category = ItemCategory.MiscEquipment;
            return true;
        }

        if (HasAnyComponentKeyword(item, "Magic", "Mystic", "Relic", "Artifact", "Ancient", "Curse"))
        {
            category = ItemCategory.MysticalItem;
            return true;
        }

        if (HasAnyComponentKeyword(item, "Deploy", "Place", "Plantable", "Balloon", "PortableStovetop", "RopeShooter", "ClimbingSpike", "ScoutCannon", "ChainShooter"))
        {
            category = ItemCategory.Deployable;
            return true;
        }

        if (HasAnyComponentKeyword(item, "Equipment", "Wearable", "Equip", "Compass", "Lantern", "Backpack", "Parasol", "Guidebook", "Binocular"))
        {
            category = ItemCategory.MiscEquipment;
            return true;
        }

        return false;
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
            if (wikiCategoryOverrides.TryGetValue(key, out category))
            {
                VerboseLog($"Category override hit: key='{key}' -> {category} (prefab='{item.name}', display='{displayName}')");
                return true;
            }
        }

        int bestScore = int.MinValue;
        string bestAlias = string.Empty;
        ItemCategory bestCategory = default;

        for (int i = 0; i < candidateKeys.Count; i++)
        {
            string key = candidateKeys[i];
            foreach (var pair in wikiCategoryOverrides)
            {
                int score = ScoreCategoryKeyMatch(key, pair.Key);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestAlias = pair.Key;
                    bestCategory = pair.Value;
                }
            }
        }

        if (bestScore >= 260)
        {
            category = bestCategory;
            VerboseLog($"Category fuzzy hit: score={bestScore}, alias='{bestAlias}', prefab='{item.name}', display='{displayName}' -> {category}");
            return true;
        }

        return false;
    }

    private static int ScoreCategoryKeyMatch(string candidate, string alias)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(alias))
        {
            return int.MinValue;
        }

        if (string.Equals(candidate, alias, StringComparison.OrdinalIgnoreCase))
        {
            return 1000;
        }

        int minLength = Math.Min(candidate.Length, alias.Length);
        int score = 0;

        if (candidate.Contains(alias, StringComparison.OrdinalIgnoreCase)
            || alias.Contains(candidate, StringComparison.OrdinalIgnoreCase))
        {
            score = Math.Max(score, 280 + minLength * 4);
        }

        int overlap = LongestCommonSubstringLength(candidate, alias);
        score = Math.Max(score, overlap * 24 - Math.Abs(candidate.Length - alias.Length) * 3);

        if (minLength >= 6)
        {
            if (candidate.StartsWith(alias.Substring(0, 6), StringComparison.OrdinalIgnoreCase)
                || alias.StartsWith(candidate.Substring(0, 6), StringComparison.OrdinalIgnoreCase))
            {
                score += 30;
            }
        }

        return score;
    }

    private static int LongestCommonSubstringLength(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
        {
            return 0;
        }

        int[,] dp = new int[a.Length + 1, b.Length + 1];
        int best = 0;

        for (int i = 1; i <= a.Length; i++)
        {
            for (int j = 1; j <= b.Length; j++)
            {
                if (char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]))
                {
                    dp[i, j] = dp[i - 1, j - 1] + 1;
                    if (dp[i, j] > best)
                    {
                        best = dp[i, j];
                    }
                }
            }
        }

        return best;
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

    private static Dictionary<string, ItemCategory> BuildWikiCategoryOverrides()
    {
        var map = new Dictionary<string, ItemCategory>(StringComparer.OrdinalIgnoreCase);

        // Food -> Natural food
        AddWikiOverride(map, ItemCategory.NaturalFood,
            "Coconut", "Half-Coconut", "Red Crispberry", "Yellow Crispberry", "Green Crispberry",
            "Honeycomb", "Scorchberry", "Purple Kingberry", "Yellow Kingberry", "Green Kingberry",
            "Pink Berrynana", "Blue Berrynana", "Brown Berrynana",
            "Yellow Clusterberry", "Red Clusterberry", "Black Clusterberry",
            "Orange Winterberry", "Yellow Winterberry",
            "Red Prickleberry", "Gold Prickleberry",
            "Medicinal Root", "Mandrake", "Marshmallow", "Hot Dog", "Egg", "Cooked Bird", "Tick", "Scorpion",
            "Napberry", "Red Shroomberry", "Yellow Shroomberry", "Green Shroomberry", "Blue Shroomberry", "Purple Shroomberry",
            "Item_Coconut", "Item_Coconut_half", "Item_Honeycomb",
            "Apple Berry Red", "Apple Berry Yellow", "Apple Berry Green", "Pepper Berry",
            "Kingberry Purple", "Kingberry Yellow", "Kingberry Green",
            "Berrynana Pink", "Berrynana Blue", "Berrynana Brown",
            "Clusterberry Red", "Clusterberry Yellow", "Clusterberry Black",
            "Winterberry Orange", "Prickleberry_Red", "Prickleberry_Gold",
            "MedicinalRoot", "Glizzy", "EggTurkey");

        // Food -> Mystical food
        AddWikiOverride(map, ItemCategory.MysticalFood,
            "Cure-All", "Pandora's Lunchbox", "PandorasBox");

        // Food -> Packaged food
        AddWikiOverride(map, ItemCategory.PackagedFood,
            "Airline Food", "Big Lollipop", "Energy Drink", "Fortified Milk", "Granola Bar", "Scout Cookies", "Sports Drink", "Trail Mix",
            "Airplane Food", "Lollipop", "FortifiedMilk", "ScoutCookies", "TrailMix");

        // Food -> Mushrooms
        AddWikiOverride(map, ItemCategory.Mushroom,
            "Bugle Shroom", "Bugle Shroom Poison", "Button Shroom", "Button Shroom Poison", "Chubby Shroom", "Cluster Shroom", "Cluster Shroom Poison",
            "Mushroom Lace", "Mushroom Lace Poison", "Mushroom Normie", "Mushroom Normie Poison", "Mushroom Chubby", "Mushroom Cluster", "Mushroom Cluster Poison");

        // Weapon -> Consumable
        AddWikiOverride(map, ItemCategory.Consumable,
            "Antidote", "Balloon", "Balloon Bunch", "Bandages", "Blowgun", "First Aid Kit", "Flare", "Heat Pack", "Remedy Fungus", "Rescue Claw", "Scroll", "Sunscreen",
            "BalloonBunch", "FirstAidKit", "HealingDart", "HealingDart Variant", "RescueHook", "GuidebookPageScroll", "GuidebookPageScroll Variant");

        // Weapon -> Deployable
        AddWikiOverride(map, ItemCategory.Deployable,
            "Bounce Fungus", "Chain Launcher", "Checkpoint Flag", "Cloud Fungus", "Magic Bean", "Piton", "Portable Stove", "Rope Cannon", "Rope Spool", "Scout Cannon", "Shelf Fungus",
            "BounceShroom", "ChainShooter", "Flag_Plantable_Checkpoint", "CloudFungus", "ClimbingSpike", "PortableStovetopItem", "RopeShooter", "ScoutCannonItem", "ShelfShroom");

        // Weapon -> Misc Equipment
        AddWikiOverride(map, ItemCategory.MiscEquipment,
            "Backpack", "Bing Bong", "Binoculars", "Bugle", "Compass", "Flying Disc", "Guidebook", "Lantern", "Parasol", "Pirate's Compass", "Torch",
            "BingBong", "Frisbee", "Pirate Compass");

        // Weapon -> Mystical Items
        AddWikiOverride(map, ItemCategory.MysticalItem,
            "Ancient Idol", "Anti-Rope Cannon", "Anti-Rope Spool", "Bugle of Friendship", "Cursed Skull", "Faerie Lantern", "Scout Effigy", "Scoutmaster's Bugle", "The Book of Bones",
            "AncientIdol", "RopeShooterAnti", "Bugle_Magic", "Lantern_Faerie", "ScoutEffigy", "Bugle_Scoutmaster", "Bugle_Scoutmaster Variant", "BookOfBones");

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

    private static bool IsNameKeyword(Item item, string displayName, params string[] keywords)
    {
        if (keywords == null || keywords.Length == 0)
        {
            return false;
        }

        string prefabName = item?.name ?? string.Empty;
        string combined = $"{displayName} {prefabName}".ToLowerInvariant();
        for (int i = 0; i < keywords.Length; i++)
        {
            string keyword = keywords[i];
            if (string.IsNullOrWhiteSpace(keyword))
            {
                continue;
            }

            if (combined.Contains(keyword.ToLowerInvariant()))
            {
                return true;
            }
        }

        return false;
    }
    private static bool IsConsumable(Item item)
    {
        if (item == null) return false;

        if (item.GetComponent<Action_Consume>() != null) return true;
        if (item.GetComponent<Action_RestoreHunger>() != null) return true;
        if (item.GetComponent<Action_GiveExtraStamina>() != null) return true;
        if (item.GetComponent<Action_InflictPoison>() != null) return true;

        ItemUseFeedback feedback = item.GetComponent<ItemUseFeedback>();
        if (feedback != null)
        {
            string anim = feedback.useAnimation ?? string.Empty;
            if (anim.Equals("Eat", StringComparison.OrdinalIgnoreCase)
                || anim.Equals("Drink", StringComparison.OrdinalIgnoreCase)
                || anim.Equals("Heal", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasKeywordComponent(Item item, string keyword)
    {
        if (item == null || string.IsNullOrWhiteSpace(keyword)) return false;

        Component[] components = item.GetComponents<Component>();
        for (int i = 0; i < components.Length; i++)
        {
            var comp = components[i];
            if (comp == null) continue;
            if (comp.GetType().Name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasAnyComponentKeyword(Item item, params string[] keywords)
    {
        if (item == null || keywords == null || keywords.Length == 0)
        {
            return false;
        }

        for (int i = 0; i < keywords.Length; i++)
        {
            if (HasKeywordComponent(item, keywords[i]))
            {
                return true;
            }
        }

        return false;
    }
    private static int GetCategoryOrder(ItemCategory category)
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

    private static bool IsFoodCategory(ItemCategory category)
    {
        return category == ItemCategory.NaturalFood
            || category == ItemCategory.MysticalFood
            || category == ItemCategory.PackagedFood
            || category == ItemCategory.Mushroom;
    }

    private static bool IsEntryInMajorCategory(ItemEntry entry, MajorCategory major)
    {
        if (major == MajorCategory.All)
        {
            return true;
        }

        bool isFood = IsFoodCategory(entry.Category);
        return major == MajorCategory.Food ? isFood : !isFood;
    }

    private static ItemCategory[] GetSubCategories(MajorCategory major)
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

    private static string GetMajorCategoryLabel(MajorCategory category)
    {
        return category switch
        {
            MajorCategory.All => GetTextOrFallback("CATEGORY_ALL", "All"),
            MajorCategory.Food => GetTextOrFallback("CATEGORY_FOOD", "Food"),
            MajorCategory.Weapon => GetTextOrFallback("CATEGORY_WEAPON", "Weapon"),
            _ => "Unknown"
        };
    }

    private static string GetCategoryLabel(ItemCategory category)
    {
        return category switch
        {
            ItemCategory.NaturalFood => GetTextOrFallback("CATEGORY_NATURAL_FOOD", "Natural Food"),
            ItemCategory.MysticalFood => GetTextOrFallback("CATEGORY_MYSTICAL_FOOD", "Mystical Food"),
            ItemCategory.PackagedFood => GetTextOrFallback("CATEGORY_PACKAGED_FOOD", "Packaged Food"),
            ItemCategory.Mushroom => GetTextOrFallback("CATEGORY_MUSHROOM", "Mushroom"),
            ItemCategory.Consumable => GetTextOrFallback("CATEGORY_CONSUMABLE", "Consumables"),
            ItemCategory.Deployable => GetTextOrFallback("CATEGORY_DEPLOYABLE", "Deployable"),
            ItemCategory.MiscEquipment => GetTextOrFallback("CATEGORY_MISC", "Misc"),
            ItemCategory.MysticalItem => GetTextOrFallback("CATEGORY_MYSTICAL_ITEM", "Mystical Item"),
            _ => "Other"
        };
    }

    private static string GetAllSubCategoryLabel()
    {
        return GetTextOrFallback("CATEGORY_ALL", "All");
    }

    private static string GetTextOrFallback(string key, string fallback)
    {
        string text = GetText(key);
        if (string.IsNullOrWhiteSpace(text) || string.Equals(text, key, StringComparison.OrdinalIgnoreCase))
        {
            return fallback;
        }

        return text;
    }

    private static void BuildMajorTabs(PeakHorizontalTabs tabs)
    {
        if (tabs == null)
        {
            return;
        }

        majorTabEntries.Clear();

        AddMajorTab(tabs, GetMajorCategoryLabel(MajorCategory.All), MajorCategory.All, 226.67f);
        AddMajorTab(tabs, GetMajorCategoryLabel(MajorCategory.Food), MajorCategory.Food, 226.67f);
        AddMajorTab(tabs, GetMajorCategoryLabel(MajorCategory.Weapon), MajorCategory.Weapon, 226.67f);

        UpdateMajorTabs();
    }

    private static void AddMajorTab(PeakHorizontalTabs tabs, string label, MajorCategory category, float width)
    {
        GameObject tab = tabs.AddTab(label);
        if (!TrySetupTabVisual(tab, width, 44f, out Button button, out Image background, out Image selected, out PeakText labelText))
        {
            return;
        }

        var tabEntry = new MajorCategoryTab(category, tab, button, background, selected, labelText);
        majorTabEntries.Add(tabEntry);

        button.onClick.AddListener(() =>
        {
            currentMajorFilter = category;
            currentSubCategoryFilter = null;
            UpdateMajorTabs();
            RebuildSubCategoryTabs();
            UpdateSubCategoryVisibility();
            MarkListDirty("Major category changed");
            RefreshListIfNeeded(force: true);
        });

        ApplyMajorTabStyle(tabEntry, category == currentMajorFilter);
    }

    private static void UpdateMajorTabs()
    {
        for (int i = 0; i < majorTabEntries.Count; i++)
        {
            var tab = majorTabEntries[i];
            bool selected = tab.Category == currentMajorFilter;
            ApplyMajorTabStyle(tab, selected);
        }
    }

    private static void RebuildSubCategoryTabs()
    {
        if (subCategoryTabs == null)
        {
            return;
        }

        var content = subCategoryTabs.transform.Find("Content");
        if (content != null)
        {
            for (int i = content.childCount - 1; i >= 0; i--)
            {
                Destroy(content.GetChild(i).gameObject);
            }
        }

        subCategoryTabEntries.Clear();

        ItemCategory[] categories = GetSubCategories(currentMajorFilter);
        if (categories.Length == 0)
        {
            return;
        }

        if (!currentSubCategoryFilter.HasValue || !categories.Contains(currentSubCategoryFilter.Value))
        {
            currentSubCategoryFilter = categories[0];
        }

        for (int i = 0; i < categories.Length; i++)
        {
            var category = categories[i];
            AddSubCategoryTab(subCategoryTabs, GetCategoryLabel(category), category, 170f);
        }

        UpdateSubCategoryTabs();
    }

    private static void AddSubCategoryTab(PeakHorizontalTabs tabs, string label, ItemCategory? category, float width)
    {
        GameObject tab = tabs.AddTab(label);
        if (!TrySetupTabVisual(tab, width, 40f, out Button button, out Image background, out Image selected, out PeakText labelText))
        {
            return;
        }

        var tabEntry = new CategoryTab(category, tab, button, background, selected, labelText);
        subCategoryTabEntries.Add(tabEntry);

        button.onClick.AddListener(() =>
        {
            currentSubCategoryFilter = category;
            UpdateSubCategoryTabs();
            MarkListDirty("Sub category changed");
            RefreshListIfNeeded(force: true);
        });

        ApplySubCategoryTabStyle(tabEntry, category == currentSubCategoryFilter);
    }

    private static bool TrySetupTabVisual(
        GameObject tab,
        float width,
        float height,
        out Button button,
        out Image background,
        out Image selected,
        out PeakText labelText)
    {
        button = null!;
        background = null!;
        selected = null!;
        labelText = null!;

        if (tab == null)
        {
            return false;
        }

        var layout = tab.GetComponent<LayoutElement>();
        if (layout == null)
        {
            layout = tab.AddComponent<LayoutElement>();
        }
        layout.minWidth = width;
        layout.preferredWidth = width;
        layout.flexibleWidth = 0f;
        layout.preferredHeight = height;

        button = tab.GetComponent<Button>();
        background = tab.transform.Find("Image")?.GetComponent<Image>()!;
        selected = tab.transform.Find("Selected")?.GetComponent<Image>()!;
        labelText = tab.GetComponentInChildren<PeakText>(true);

        if (button == null || background == null || selected == null || labelText == null)
        {
            return false;
        }

        labelText.TextMesh.fontStyle = TMPro.FontStyles.Normal;
        labelText.TextMesh.enableAutoSizing = false;
        labelText.TextMesh.fontSize = 18;
        labelText.TextMesh.alignment = TMPro.TextAlignmentOptions.Center;

        background.raycastTarget = true;
        selected.raycastTarget = false;
        labelText.TextMesh.raycastTarget = false;

        return true;
    }

    private static void UpdateSubCategoryVisibility()
    {
        bool showSubCategories = currentMajorFilter != MajorCategory.All;

        if (subCategoryTabsRoot != null)
        {
            subCategoryTabsRoot.SetActive(showSubCategories);
        }

        if (topControlsRect != null)
        {
            topControlsRect.sizeDelta = new Vector2(topControlsRect.sizeDelta.x, showSubCategories ? 166f : 122f);
        }

        if (listContainerRect != null)
        {
            listContainerRect.offsetMax = showSubCategories
                ? new Vector2(-20f, -236f)
                : new Vector2(-20f, -192f);
        }
    }

    private static void RefreshLanguageDependentContent(bool force = false)
    {
        int languageIndex = GetCurrentLanguageIndex();
        string titleText = GetText("TITLE");
        string searchPlaceholder = GetText("SEARCH_PLACEHOLDER");
        string languageMarker = BuildLanguageMarker(titleText, searchPlaceholder);
        bool languageChanged = !string.Equals(languageMarker, lastLanguageMarker, StringComparison.Ordinal);

        if (!force && !languageChanged)
        {
            return;
        }

        lastLanguageMarker = languageMarker;
        lastRenderedLanguageIndex = languageIndex;

        if (headerTitleText != null)
        {
            headerTitleText.TextMesh.text = titleText;
        }

        if (closeMenuButton?.Text != null)
        {
            closeMenuButton.Text.text = GetTextOrFallback("CLOSE_BUTTON", "Close");
        }

        if (searchInput != null)
        {
            searchInput.SetPlaceholder(searchPlaceholder);
        }

        for (int i = 0; i < majorTabEntries.Count; i++)
        {
            var tab = majorTabEntries[i];
            tab.Label.TextMesh.text = GetMajorCategoryLabel(tab.Category);
        }

        for (int i = 0; i < subCategoryTabEntries.Count; i++)
        {
            var tab = subCategoryTabEntries[i];
            string text = tab.Category.HasValue ? GetCategoryLabel(tab.Category.Value) : GetAllSubCategoryLabel();
            tab.Label.TextMesh.text = text;
        }

        RefreshItemDisplayNamesForCurrentLanguage(force: false, currentLanguageMarker: languageMarker);
        MarkListDirty("Language changed");
    }

    private static void RefreshItemDisplayNamesForCurrentLanguage(bool force = false, string? currentLanguageMarker = null)
    {
        if (!itemListInitialized || itemEntries.Count == 0)
        {
            return;
        }

        int languageIndex = GetCurrentLanguageIndex();
        currentLanguageMarker ??= BuildLanguageMarker();
        bool markerChanged = !string.Equals(currentLanguageMarker, itemNamesLanguageMarker, StringComparison.Ordinal);

        if (!force && !markerChanged && languageIndex == itemNamesLanguageIndex)
        {
            return;
        }

        int renamedCount = 0;
        for (int i = 0; i < itemEntries.Count; i++)
        {
            ItemEntry entry = itemEntries[i];
            string localizedName = GetLocalizedItemName(entry.Prefab);
            string displayName = string.IsNullOrWhiteSpace(localizedName) ? entry.PrefabName : localizedName;
            displayName = GetDisplayNameOverride(entry.Prefab, displayName);

            if (!string.Equals(entry.DisplayName, displayName, StringComparison.Ordinal))
            {
                entry.UpdateDisplayName(displayName);
                renamedCount++;
            }
        }

        itemNamesLanguageIndex = languageIndex;
        itemNamesLanguageMarker = currentLanguageMarker;
        VerboseLog($"Language refresh complete. index={languageIndex}, renamed={renamedCount}, total={itemEntries.Count}, markerChanged={markerChanged}");
    }

    private static string BuildLanguageMarker()
    {
        return BuildLanguageMarker(GetText("TITLE"), GetText("SEARCH_PLACEHOLDER"));
    }

    private static string BuildLanguageMarker(string titleText, string searchPlaceholder)
    {
        return $"{titleText}|{searchPlaceholder}";
    }

    private static void UpdateSubCategoryTabs()
    {
        for (int i = 0; i < subCategoryTabEntries.Count; i++)
        {
            var tab = subCategoryTabEntries[i];
            bool selected = tab.Category == currentSubCategoryFilter;
            ApplySubCategoryTabStyle(tab, selected);
        }
    }

    private static void ApplyMajorTabStyle(MajorCategoryTab tab, bool selected)
    {
        Color inactiveBg = new Color(0.22f, 0.17f, 0.12f, 0.95f);
        Color activeBg = new Color(0.95f, 0.92f, 0.86f, 1f);
        Color inactiveText = new Color(0.92f, 0.88f, 0.8f, 1f);
        Color activeText = new Color(0.2f, 0.16f, 0.12f, 1f);

        tab.Background.color = inactiveBg;
        tab.Selected.color = activeBg;
        tab.Selected.enabled = selected;
        tab.Label.TextMesh.color = selected ? activeText : inactiveText;

        var colors = tab.Button.colors;
        colors.normalColor = inactiveBg;
        colors.highlightedColor = new Color(0.26f, 0.2f, 0.14f, 1f);
        colors.pressedColor = new Color(0.16f, 0.12f, 0.09f, 1f);
        colors.selectedColor = colors.normalColor;
        tab.Button.colors = colors;
    }

    private static void ApplySubCategoryTabStyle(CategoryTab tab, bool selected)
    {
        Color inactiveBg = new Color(0.22f, 0.17f, 0.12f, 0.95f);
        Color activeBg = new Color(0.95f, 0.92f, 0.86f, 1f);
        Color inactiveText = new Color(0.92f, 0.88f, 0.8f, 1f);
        Color activeText = new Color(0.2f, 0.16f, 0.12f, 1f);

        tab.Background.color = inactiveBg;
        tab.Selected.color = activeBg;
        tab.Selected.enabled = selected;
        tab.Label.TextMesh.color = selected ? activeText : inactiveText;

        var colors = tab.Button.colors;
        colors.normalColor = inactiveBg;
        colors.highlightedColor = new Color(0.26f, 0.2f, 0.14f, 1f);
        colors.pressedColor = new Color(0.16f, 0.12f, 0.09f, 1f);
        colors.selectedColor = colors.normalColor;
        tab.Button.colors = colors;
    }

    private static void NormalizeButtonLayout(PeakElement button, Vector2? size = null)
    {
        if (button == null) return;

        var rect = button.GetComponent<RectTransform>();
        if (rect != null)
        {
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.localScale = Vector3.one;
            if (size.HasValue)
            {
                rect.sizeDelta = size.Value;
            }
        }

        var innerButton = button.GetComponentInChildren<Button>(true);
        if (innerButton != null && innerButton.gameObject != button.gameObject)
        {
            var innerRect = innerButton.GetComponent<RectTransform>();
            if (innerRect != null)
            {
                innerRect.anchorMin = Vector2.zero;
                innerRect.anchorMax = Vector2.one;
                innerRect.offsetMin = Vector2.zero;
                innerRect.offsetMax = Vector2.zero;
                innerRect.localScale = Vector3.one;
            }
        }

        var panelRect = button.transform.Find("Panel") as RectTransform;
        if (panelRect != null)
        {
            panelRect.anchorMin = Vector2.zero;
            panelRect.anchorMax = Vector2.one;
            panelRect.offsetMin = Vector2.zero;
            panelRect.offsetMax = Vector2.zero;
        }

        var textRect = button.transform.Find("Text") as RectTransform;
        if (textRect != null)
        {
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;
        }
    }

    private static void SpawnItem(Item prefab)
    {
        if (prefab == null)
        {
            return;
        }

        Character player = Character.localCharacter;
        if (player == null)
        {
            Log.LogWarning("[ItemBrowser] No local character available. Enter a match to spawn items.");
            return;
        }

        if (!configAllowOnline.Value)
        {
            if (!PhotonNetwork.OfflineMode && (!PhotonNetwork.IsConnected || !PhotonNetwork.InRoom))
            {
                Log.LogWarning("[ItemBrowser] Online spawn disabled or not in room.");
                return;
            }
        }

        Vector3 spawnPos = player.Center + player.transform.forward * configSpawnDistance.Value;

        VerboseLog($"Spawn request: prefab='{prefab.name}', position={spawnPos}, online={PhotonNetwork.IsConnected}, inRoom={PhotonNetwork.InRoom}");

        try
        {
            PhotonNetwork.InstantiateItemRoom(prefab.name, spawnPos, Quaternion.identity);
            VerboseLog($"Spawn success: {prefab.name}");
        }
        catch (Exception e)
        {
            Log.LogError($"[ItemBrowser] Spawn failed for {prefab.name}: {e.Message}");
        }
    }

    private static void OpenPage()
    {
        if (page == null) return;

        if (menuWindowOpenMethod == null)
        {
            menuWindowOpenMethod = typeof(MenuWindow).GetMethod("Open", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        }

        if (menuWindowOpenMethod != null)
        {
            menuWindowOpenMethod.Invoke(page, null);
        }
        else
        {
            page.gameObject.SetActive(true);
        }
    }

    private static void ClosePage()
    {
        if (page == null) return;

        if (menuWindowCloseMethod == null)
        {
            menuWindowCloseMethod = typeof(MenuWindow).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m => m.Name == "Close" && m.GetParameters().Length == 0)
                ?? typeof(MenuWindow).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(m => m.Name == "Close" && m.GetParameters().Length == 1);
        }

        if (menuWindowCloseMethod != null)
        {
            var parameters = menuWindowCloseMethod.GetParameters();
            object?[] args = parameters.Length == 1 ? new object?[] { true } : Array.Empty<object?>();
            menuWindowCloseMethod.Invoke(page, args);
        }
        else
        {
            page.gameObject.SetActive(false);
        }
    }

    private static bool IsTogglePressed()
    {
        KeyCode key = configToggleKey.Value;

        if (TryGetInputSystemKeyDown(key, out bool pressedByInputSystem))
        {
            return pressedByInputSystem;
        }

        if (TryGetLegacyInputKeyDown(key, out bool pressedByLegacy))
        {
            return pressedByLegacy;
        }

        return false;
    }

    private static bool TryGetInputSystemKeyDown(KeyCode keyCode, out bool pressed)
    {
        pressed = false;

        if (!inputSystemChecked)
        {
            inputSystemAvailable = InitializeInputSystemReflection();
            inputSystemChecked = true;
        }

        if (!inputSystemAvailable) return false;

        try
        {
            var keyboard = inputSystemKeyboardCurrentProp?.GetValue(null, null);
            if (keyboard == null) return true;

            var keyEnum = Enum.Parse(inputSystemKeyType, keyCode.ToString());
            var keyControl = inputSystemKeyboardItemProp?.GetValue(keyboard, new[] { keyEnum });
            if (keyControl == null) return true;

            pressed = (bool)inputSystemKeyControlPressedProp!.GetValue(keyControl, null);
            return true;
        }
        catch
        {
            inputSystemAvailable = false;
            return false;
        }
    }

    private static bool InitializeInputSystemReflection()
    {
        try
        {
            var keyboardType = Type.GetType("UnityEngine.InputSystem.Keyboard, Unity.InputSystem");
            inputSystemKeyType = Type.GetType("UnityEngine.InputSystem.Key, Unity.InputSystem");
            var keyControlType = Type.GetType("UnityEngine.InputSystem.Controls.KeyControl, Unity.InputSystem");
            if (keyboardType == null || inputSystemKeyType == null || keyControlType == null) return false;

            inputSystemKeyboardCurrentProp = keyboardType.GetProperty("current", BindingFlags.Public | BindingFlags.Static);
            inputSystemKeyboardItemProp = keyboardType.GetProperty("Item", BindingFlags.Public | BindingFlags.Instance);
            inputSystemKeyControlPressedProp = keyControlType.GetProperty("wasPressedThisFrame", BindingFlags.Public | BindingFlags.Instance);

            return inputSystemKeyboardCurrentProp != null
                && inputSystemKeyboardItemProp != null
                && inputSystemKeyControlPressedProp != null;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetLegacyInputKeyDown(KeyCode keyCode, out bool pressed)
    {
        pressed = false;
        if (!legacyInputAvailable) return false;

        try
        {
            pressed = Input.GetKeyDown(keyCode);
            return true;
        }
        catch
        {
            legacyInputAvailable = false;
            return false;
        }
    }

    private static string GetLocalizedItemName(Item item)
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
        if (!string.IsNullOrEmpty(normalizedName) && itemNameKeyMap.TryGetValue(normalizedName, out string mappedKey))
        {
            return ResolveLocalizedText(mappedKey);
        }

        return string.Empty;
    }

    private static void EnsureItemNameKeyMap()
    {
        if (itemNameKeyMapInitialized)
        {
            return;
        }

        LoadItemNameKeyMap();
        itemNameKeyMapInitialized = true;
    }

    private static void LoadItemNameKeyMap()
    {
        itemNameKeyMap.Clear();

        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ItemNameKeyMap.json");
            if (stream == null)
            {
                Log.LogWarning("[ItemBrowser] Embedded ItemNameKeyMap.json not found.");
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
                    itemNameKeyMap[kvp.Key] = kvp.Value;
                }
            }
        }
        catch (Exception e)
        {
            Log.LogWarning($"[ItemBrowser] Failed to load embedded ItemNameKeyMap.json: {e.GetType().Name} {e.Message}");
        }
    }

    private static string NormalizeItemNameForMap(string name)
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

    private static string ResolveLocalizedText(string key)
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

    private static string GetText(string key, params string[] args)
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

    private static void LoadLocalizedText()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Localized_Text.json");
            if (stream == null)
            {
                Log.LogWarning("[ItemBrowser] Embedded Localized_Text.json not found.");
                return;
            }

            using var reader = new StreamReader(stream);
            string json = reader.ReadToEnd();
            var table = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, List<string>>>(json);
            if (table == null)
            {
                Log.LogWarning("[ItemBrowser] Localized_Text.json deserialized to null.");
                return;
            }

            localizedTextTable.Clear();
            foreach (var item in table)
            {
                if (string.IsNullOrWhiteSpace(item.Key))
                {
                    continue;
                }

                var rawValues = item.Value;
                if (rawValues == null || rawValues.Count == 0)
                {
                    localizedTextTable[item.Key] = new List<string> { item.Key };
                    continue;
                }

                string firstValue = rawValues.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? item.Key;
                var values = rawValues.Select(x => string.IsNullOrWhiteSpace(x) ? firstValue : x).ToList();
                if (values.Count == 0)
                {
                    values.Add(firstValue);
                }

                localizedTextTable[item.Key] = values;
            }

            EnsureLocalizedTextInjected();
        }
        catch (Exception e)
        {
            Log.LogWarning($"[ItemBrowser] Failed to load Localized_Text.json: {e.GetType().Name} {e.Message}");
        }
    }

    private static string GetLocalizationPrefix()
    {
        return $"Mod_{Name}";
    }

    private static void EnsureLocalizedTextInjected()
    {
        if (localizedTextTable.Count == 0) return;

        if (LocalizedText.MAIN_TABLE == null)
        {
            Log.LogWarning("[ItemBrowser] LocalizedText.MAIN_TABLE is null. Skip localization injection.");
            return;
        }

        string prefix = GetLocalizationPrefix();
        foreach (var item in localizedTextTable)
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
        if (!localizedTextTable.TryGetValue(key, out var values) || values == null || values.Count == 0)
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

    private static int GetCurrentLanguageIndex()
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
}
