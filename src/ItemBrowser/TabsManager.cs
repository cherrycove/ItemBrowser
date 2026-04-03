using PEAKLib.UI.Elements;

using UnityEngine;
using UnityEngine.UI;

namespace ItemBrowser;

internal static class TabsManager
{
    internal static void BuildMajorTabs(PeakHorizontalTabs tabs)
    {
        if (tabs == null)
        {
            return;
        }

        SharedState.MajorTabEntries.Clear();

        AddMajorTab(tabs, CategorySystem.GetMajorCategoryLabel(MajorCategory.All), MajorCategory.All, 226.67f);
        AddMajorTab(tabs, CategorySystem.GetMajorCategoryLabel(MajorCategory.Food), MajorCategory.Food, 226.67f);
        AddMajorTab(tabs, CategorySystem.GetMajorCategoryLabel(MajorCategory.Weapon), MajorCategory.Weapon, 226.67f);

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
        SharedState.MajorTabEntries.Add(tabEntry);

        button.onClick.AddListener(() =>
        {
            SharedState.CurrentMajorFilter = category;
            SharedState.CurrentSubCategoryFilter = null;
            UpdateMajorTabs();
            RebuildSubCategoryTabs();
            UpdateSubCategoryVisibility();
            VirtualList.MarkListDirty("Major category changed");
            VirtualList.RefreshListIfNeeded(force: true);
        });

        ApplyMajorTabStyle(tabEntry, category == SharedState.CurrentMajorFilter);
    }

    internal static void UpdateMajorTabs()
    {
        for (int i = 0; i < SharedState.MajorTabEntries.Count; i++)
        {
            var tab = SharedState.MajorTabEntries[i];
            bool selected = tab.Category == SharedState.CurrentMajorFilter;
            ApplyMajorTabStyle(tab, selected);
        }
    }

    internal static void RebuildSubCategoryTabs()
    {
        if (SharedState.SubCategoryTabs == null)
        {
            return;
        }

        var content = SharedState.SubCategoryTabs.transform.Find("Content");
        if (content != null)
        {
            for (int i = content.childCount - 1; i >= 0; i--)
            {
                Object.Destroy(content.GetChild(i).gameObject);
            }
        }

        SharedState.SubCategoryTabEntries.Clear();

        ItemCategory[] categories = CategorySystem.GetSubCategories(SharedState.CurrentMajorFilter);
        if (categories.Length == 0)
        {
            return;
        }

        if (!SharedState.CurrentSubCategoryFilter.HasValue || !System.Array.Exists(categories, c => c == SharedState.CurrentSubCategoryFilter.Value))
        {
            SharedState.CurrentSubCategoryFilter = categories[0];
        }

        for (int i = 0; i < categories.Length; i++)
        {
            var category = categories[i];
            AddSubCategoryTab(SharedState.SubCategoryTabs, CategorySystem.GetCategoryLabel(category), category, 170f);
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
        SharedState.SubCategoryTabEntries.Add(tabEntry);

        button.onClick.AddListener(() =>
        {
            SharedState.CurrentSubCategoryFilter = category;
            UpdateSubCategoryTabs();
            VirtualList.MarkListDirty("Sub category changed");
            VirtualList.RefreshListIfNeeded(force: true);
        });

        ApplySubCategoryTabStyle(tabEntry, category == SharedState.CurrentSubCategoryFilter);
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

    internal static void UpdateSubCategoryVisibility()
    {
        bool showSubCategories = SharedState.CurrentMajorFilter != MajorCategory.All;

        if (SharedState.SubCategoryTabsRoot != null)
        {
            SharedState.SubCategoryTabsRoot.SetActive(showSubCategories);
        }

        if (SharedState.TopControlsRect != null)
        {
            SharedState.TopControlsRect.sizeDelta = new Vector2(SharedState.TopControlsRect.sizeDelta.x, showSubCategories ? 166f : 122f);
        }

        if (SharedState.ListContainerRect != null)
        {
            SharedState.ListContainerRect.offsetMax = showSubCategories
                ? new Vector2(-20f, -236f)
                : new Vector2(-20f, -192f);
        }
    }

    internal static void RefreshLanguageDependentContent(bool force = false)
    {
        int languageIndex = Localization.GetCurrentLanguageIndex();
        string titleText = Localization.GetText("TITLE");
        string searchPlaceholder = Localization.GetText("SEARCH_PLACEHOLDER");
        string languageMarker = $"{titleText}|{searchPlaceholder}";
        bool languageChanged = !string.Equals(languageMarker, SharedState.LastLanguageMarker, System.StringComparison.Ordinal);

        if (!force && !languageChanged)
        {
            return;
        }

        SharedState.LastLanguageMarker = languageMarker;
        Localization.LastRenderedLanguageIndex = languageIndex;

        if (SharedState.HeaderTitleText != null)
        {
            SharedState.HeaderTitleText.TextMesh.text = titleText;
        }

        if (SharedState.CloseMenuButton?.Text != null)
        {
            SharedState.CloseMenuButton.Text.text = Localization.GetTextOrFallback("CLOSE_BUTTON", "Close");
        }

        if (SharedState.SearchInput != null)
        {
            SharedState.SearchInput.SetPlaceholder(searchPlaceholder);
        }

        for (int i = 0; i < SharedState.MajorTabEntries.Count; i++)
        {
            var tab = SharedState.MajorTabEntries[i];
            tab.Label.TextMesh.text = CategorySystem.GetMajorCategoryLabel(tab.Category);
        }

        for (int i = 0; i < SharedState.SubCategoryTabEntries.Count; i++)
        {
            var tab = SharedState.SubCategoryTabEntries[i];
            string text = tab.Category.HasValue ? CategorySystem.GetCategoryLabel(tab.Category.Value) : CategorySystem.GetAllSubCategoryLabel();
            tab.Label.TextMesh.text = text;
        }

        ItemLoader.RefreshItemDisplayNamesForCurrentLanguage(force: false, currentLanguageMarker: languageMarker);
        VirtualList.MarkListDirty("Language changed");
    }

    private static void UpdateSubCategoryTabs()
    {
        for (int i = 0; i < SharedState.SubCategoryTabEntries.Count; i++)
        {
            var tab = SharedState.SubCategoryTabEntries[i];
            bool selected = tab.Category == SharedState.CurrentSubCategoryFilter;
            ApplySubCategoryTabStyle(tab, selected);
        }
    }

    internal static void ApplyMajorTabStyle(MajorCategoryTab tab, bool selected)
    {
        tab.Background.color = SharedState.TabInactiveBg;
        tab.Selected.color = SharedState.TabActiveBg;
        tab.Selected.enabled = selected;
        tab.Label.TextMesh.color = selected ? SharedState.TabActiveText : SharedState.TabInactiveText;

        var colors = tab.Button.colors;
        colors.normalColor = SharedState.TabInactiveBg;
        colors.highlightedColor = SharedState.TabHighlightedBg;
        colors.pressedColor = SharedState.TabPressedBg;
        colors.selectedColor = colors.normalColor;
        tab.Button.colors = colors;
    }

    internal static void ApplySubCategoryTabStyle(CategoryTab tab, bool selected)
    {
        tab.Background.color = SharedState.TabInactiveBg;
        tab.Selected.color = SharedState.TabActiveBg;
        tab.Selected.enabled = selected;
        tab.Label.TextMesh.color = selected ? SharedState.TabActiveText : SharedState.TabInactiveText;

        var colors = tab.Button.colors;
        colors.normalColor = SharedState.TabInactiveBg;
        colors.highlightedColor = SharedState.TabHighlightedBg;
        colors.pressedColor = SharedState.TabPressedBg;
        colors.selectedColor = colors.normalColor;
        tab.Button.colors = colors;
    }

    internal static void ResetState()
    {
        SharedState.MajorTabEntries.Clear();
        SharedState.SubCategoryTabEntries.Clear();
    }
}
