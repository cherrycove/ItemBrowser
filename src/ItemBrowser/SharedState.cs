using BepInEx.Configuration;

using PEAKLib.UI;
using PEAKLib.UI.Elements;

using System.Collections.Generic;

using UnityEngine;
using UnityEngine.UI;

namespace ItemBrowser;

internal static class SharedState
{
    // ── Config 字段 ──
    public static ConfigEntry<KeyCode>? ConfigToggleKey;
    public static ConfigEntry<bool>? ConfigAllowOnline;
    public static ConfigEntry<bool>? ConfigVerboseLogs;
    public static ConfigEntry<bool>? ConfigGhostSendToObserved;

    // ── 颜色常量 ──
    public static readonly Color TabInactiveBg = new Color(0.22f, 0.17f, 0.12f, 0.95f);
    public static readonly Color TabActiveBg = new Color(0.95f, 0.92f, 0.86f, 1f);
    public static readonly Color TabInactiveText = new Color(0.92f, 0.88f, 0.8f, 1f);
    public static readonly Color TabActiveText = new Color(0.2f, 0.16f, 0.12f, 1f);
    public static readonly Color TabHighlightedBg = new Color(0.26f, 0.2f, 0.14f, 1f);
    public static readonly Color TabPressedBg = new Color(0.16f, 0.12f, 0.09f, 1f);

    // ── UI 控件引用 ──
    public static PeakCustomPage? Page;
    public static PeakText? HeaderTitleText;
    public static PeakMenuButton? CloseMenuButton;
    public static PeakTextInput? SearchInput;
    public static PeakScrollableContent? ScrollContent;
    public static PeakHorizontalTabs? MajorTabs;
    public static PeakHorizontalTabs? SubCategoryTabs;
    public static GameObject? SubCategoryTabsRoot;
    public static RectTransform? TopControlsRect;
    public static RectTransform? ListContainerRect;
    public static ScrollRect? ListScrollRect;
    public static GridLayoutGroup? ItemGridLayout;

    // ── Plugin 实例 ──
    public static Plugin? Instance;

    // ── UI 状态标志 ──
    public static bool UiBuilt;
    public static bool PageOpen;
    public static bool ListNeedsRefresh = true;
    public static bool ListRenderRunning;
    public static int ListRenderGeneration;

    // ── 物品数据 ──
    public static readonly List<ItemEntry> ItemEntries = new();
    public static bool ItemListInitialized;
    public static bool ItemPreloadRunning;
    public static int PreloadingDatabaseId;
    public static int LoadedDatabaseId;
    public static int ItemNamesLanguageIndex = -1;
    public static string ItemNamesLanguageMarker = string.Empty;

    // ── 图标缓存 ──
    public static readonly Dictionary<string, Sprite?> ItemIconCache = new(System.StringComparer.OrdinalIgnoreCase);
    public static readonly Dictionary<int, Sprite> GeneratedTextureSpriteCache = new();

    // ── 按钮池 ──
    public static readonly List<PooledItemButton> ItemButtonPool = new();
    public static readonly List<ItemEntry> ActiveRenderEntries = new();

    // ── 过滤器 ──
    public static string CurrentSearch = string.Empty;
    public static MajorCategory CurrentMajorFilter = MajorCategory.All;
    public static ItemCategory? CurrentSubCategoryFilter;
    public static readonly List<MajorCategoryTab> MajorTabEntries = new();
    public static readonly List<CategoryTab> SubCategoryTabEntries = new();

    // ── 语言 ──
    public static string LastLanguageMarker = string.Empty;
}
