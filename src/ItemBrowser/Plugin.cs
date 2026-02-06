using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;

using PEAKLib.UI;
using PEAKLib.UI.Elements;

using Photon.Pun;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

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

    private static PeakCustomPage? page;
    private static PeakTextInput? searchInput;
    private static PeakScrollableContent? scrollContent;

    private static bool uiBuilt;
    private static bool pageOpen;
    private static string currentSearch = string.Empty;

    private static readonly List<ItemEntry> itemEntries = new();
    private static bool itemListInitialized;

    private static readonly Dictionary<string, string> itemNameKeyMap = new(StringComparer.OrdinalIgnoreCase);
    private static bool itemNameKeyMapInitialized;

    private static bool inputSystemChecked;
    private static bool inputSystemAvailable;
    private static PropertyInfo? inputSystemKeyboardCurrentProp;
    private static PropertyInfo? inputSystemKeyboardItemProp;
    private static PropertyInfo? inputSystemKeyControlPressedProp;
    private static Type? inputSystemKeyType;
    private static bool legacyInputAvailable = true;

    private static MethodInfo? menuWindowOpenMethod;
    private static MethodInfo? menuWindowCloseMethod;

    private enum ItemCategory
    {
        Consumable,
        Cookable,
        Weapon,
        Tool,
        Misc
    }

    private sealed class ItemEntry
    {
        public Item Prefab { get; }
        public string PrefabName { get; }
        public string DisplayName { get; }
        public ItemCategory Category { get; }
        public string SearchText { get; }

        public ItemEntry(Item prefab, string displayName, ItemCategory category)
        {
            Prefab = prefab;
            PrefabName = prefab.name ?? string.Empty;
            DisplayName = displayName;
            Category = category;
            SearchText = $"{displayName} {PrefabName}".ToLowerInvariant();
        }
    }

    private void Awake()
    {
        Log = Logger;
        configToggleKey = ((BaseUnityPlugin)this).Config.Bind<KeyCode>("ItemBrowser", "Toggle Key", KeyCode.F4, "Press to open/close the item browser.");
        configSpawnDistance = ((BaseUnityPlugin)this).Config.Bind<float>("ItemBrowser", "Spawn Distance", 1.5f, "Distance in front of the player to spawn items.");
        configAllowOnline = ((BaseUnityPlugin)this).Config.Bind<bool>("ItemBrowser", "Allow Online Spawn", true, "Allow spawning items while online.");

        LoadLocalizedText();
    }

    private void Update()
    {
        if (!IsTogglePressed())
        {
            return;
        }

        ToggleUI();
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
            return true;
        }

        if (!IsUIReady())
        {
            Log.LogWarning("[ItemBrowser] UI not ready yet. Try again after fully entering a match.");
            return false;
        }

        BuildUI();
        uiBuilt = true;
        return true;
    }

    private static bool IsUIReady()
    {
        return SingletonAsset<InputCellMapper>.Instance != null
            && SingletonAsset<InputCellMapper>.Instance.FloatSettingCell != null
            && Templates.ButtonTemplate != null;
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
            EnsureItemList();
            RefreshList();
        });

        page.SetOnClose(() =>
        {
            pageOpen = false;
        });

        var headerContainer = new GameObject("Header")
            .AddComponent<PeakElement>()
            .ParentTo(page)
            .SetAnchorMin(new Vector2(0, 1))
            .SetAnchorMax(new Vector2(0, 1))
            .SetPosition(new Vector2(60, -40))
            .SetSize(new Vector2(600, 80));

        var headerText = MenuAPI
            .CreateText(GetText("TITLE"), "HeaderText")
            .SetFontSize(48)
            .ParentTo(headerContainer)
            .ExpandToParent();
        headerText.TextMesh.alignment = TMPro.TextAlignmentOptions.MidlineLeft;

        MenuAPI
            .CreateText(GetText("SEARCH_LABEL"), "SearchLabel")
            .ParentTo(page)
            .SetPosition(new Vector2(60, -130));

        searchInput = MenuAPI
            .CreateTextInput("SearchInput")
            .ParentTo(page)
            .SetSize(new Vector2(420, 70))
            .SetPosition(new Vector2(210, -210))
            .SetPlaceholder(GetText("SEARCH_PLACEHOLDER"))
            .OnValueChanged(OnSearchChanged);

        var listContainer = new GameObject("ListContainer")
            .AddComponent<PeakElement>()
            .ParentTo(page)
            .SetAnchorMin(new Vector2(0, 0))
            .SetAnchorMax(new Vector2(1, 1))
            .SetOffsetMin(new Vector2(60, 60))
            .SetOffsetMax(new Vector2(-60, -260));

        scrollContent = MenuAPI
            .CreateScrollableContent("ItemList")
            .ParentTo(listContainer)
            .ExpandToParent();

        page.gameObject.SetActive(false);
    }

    private static void OnSearchChanged(string query)
    {
        currentSearch = query ?? string.Empty;
        RefreshList();
    }

    private static void RefreshList()
    {
        if (scrollContent == null)
        {
            return;
        }

        ClearContent(scrollContent.Content);

        if (!itemListInitialized)
        {
            AddStatusText(GetText("STATUS_NOT_READY"));
            return;
        }

        IEnumerable<ItemEntry> filtered = itemEntries;
        string search = currentSearch.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(search))
        {
            filtered = filtered.Where(entry => entry.SearchText.Contains(search));
        }

        List<ItemEntry> filteredList = filtered.ToList();
        if (filteredList.Count == 0)
        {
            AddStatusText(GetText("STATUS_EMPTY"));
            return;
        }

        foreach (var group in filteredList
            .GroupBy(entry => entry.Category)
            .OrderBy(group => GetCategoryOrder(group.Key)))
        {
            AddCategoryHeader(group.Key);
            AddSpacer(10);

            foreach (var entry in group)
            {
                AddItemButton(entry);
            }

            AddSpacer(18);
        }
    }

    private static void AddCategoryHeader(ItemCategory category)
    {
        if (scrollContent == null) return;

        string text = GetText(GetCategoryKey(category));
        MenuAPI.CreateText(text)
            .SetFontSize(28)
            .ParentTo(scrollContent.Content);
    }

    private static void AddItemButton(ItemEntry entry)
    {
        if (scrollContent == null) return;

        string display = entry.DisplayName;
        if (!string.Equals(display, entry.PrefabName, StringComparison.OrdinalIgnoreCase))
        {
            display = $"{display} <#888888>({entry.PrefabName})</color>";
        }

        MenuAPI
            .CreateMenuButton($"- {display}")
            .SetWidth(1200)
            .ParentTo(scrollContent.Content)
            .OnClick(() => SpawnItem(entry.Prefab));
    }

    private static void AddStatusText(string text)
    {
        if (scrollContent == null) return;

        MenuAPI.CreateText(text)
            .SetFontSize(24)
            .ParentTo(scrollContent.Content);
    }

    private static void AddSpacer(float height)
    {
        if (scrollContent == null) return;

        var spacer = new GameObject("Spacer", typeof(RectTransform), typeof(LayoutElement));
        var layout = spacer.GetComponent<LayoutElement>();
        layout.minHeight = height;
        layout.preferredHeight = height;
        spacer.transform.SetParent(scrollContent.Content, false);
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
        if (itemListInitialized)
        {
            return;
        }

        itemEntries.Clear();

        var db = SingletonAsset<ItemDatabase>.Instance;
        if (db == null || db.Objects == null || db.Objects.Count == 0)
        {
            return;
        }

        foreach (var item in db.Objects)
        {
            if (item == null) continue;

            string localizedName = GetLocalizedItemName(item);
            string displayName = string.IsNullOrWhiteSpace(localizedName) ? item.name : localizedName;
            ItemCategory category = GetCategory(item);

            itemEntries.Add(new ItemEntry(item, displayName, category));
        }

        itemListInitialized = true;
    }

    private static ItemCategory GetCategory(Item item)
    {
        if (item == null)
        {
            return ItemCategory.Misc;
        }

        if (IsConsumable(item))
        {
            return ItemCategory.Consumable;
        }

        if (item.GetComponent<ItemCooking>() != null)
        {
            return ItemCategory.Cookable;
        }

        if (HasKeywordComponent(item, "Explode")
            || HasKeywordComponent(item, "Dynamite")
            || HasKeywordComponent(item, "AOE")
            || HasKeywordComponent(item, "Grenade")
            || HasKeywordComponent(item, "Bomb")
            || HasKeywordComponent(item, "Weapon"))
        {
            return ItemCategory.Weapon;
        }

        if (HasKeywordComponent(item, "Rope")
            || HasKeywordComponent(item, "Compass")
            || HasKeywordComponent(item, "Binocular")
            || HasKeywordComponent(item, "Parasol")
            || HasKeywordComponent(item, "Hook"))
        {
            return ItemCategory.Tool;
        }

        return ItemCategory.Misc;
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

    private static int GetCategoryOrder(ItemCategory category)
    {
        return category switch
        {
            ItemCategory.Consumable => 0,
            ItemCategory.Cookable => 1,
            ItemCategory.Weapon => 2,
            ItemCategory.Tool => 3,
            _ => 4
        };
    }

    private static string GetCategoryKey(ItemCategory category)
    {
        return category switch
        {
            ItemCategory.Consumable => "CATEGORY_CONSUMABLE",
            ItemCategory.Cookable => "CATEGORY_COOKABLE",
            ItemCategory.Weapon => "CATEGORY_WEAPON",
            ItemCategory.Tool => "CATEGORY_TOOL",
            _ => "CATEGORY_MISC"
        };
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

        try
        {
            PhotonNetwork.InstantiateItemRoom(prefab.name, spawnPos, Quaternion.identity);
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
        return string.Format(LocalizedText.GetText($"Mod_{Name}_{key}".ToUpper()), args);
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
                return;
            }

            foreach (var item in table)
            {
                var values = item.Value;
                string firstValue = values[0];
                values = values.Select(x => string.IsNullOrEmpty(x) ? firstValue : x).ToList();
                string localizedKey = $"Mod_{Name}_{item.Key}".ToUpper();
                LocalizedText.MAIN_TABLE[localizedKey] = values;
            }
        }
        catch (Exception e)
        {
            Log.LogWarning($"[ItemBrowser] Failed to load Localized_Text.json: {e.GetType().Name} {e.Message}");
        }
    }
}
