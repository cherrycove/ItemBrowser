using System;
using System.Linq;
using System.Reflection;

using PEAKLib.UI;
using PEAKLib.UI.Elements;

using UnityEngine;
using UnityEngine.UI;

using Zorro.Core;
using Zorro.Settings;

namespace ItemBrowser;

internal static class UIBuilder
{
    private static bool _buttonTemplateRecovered;
    private static Scrollbar? _listScrollbar;
    private static MethodInfo? _menuWindowOpenMethod;
    private static MethodInfo? _menuWindowCloseMethod;

    internal static bool IsUIReady(out string reason)
    {
        var missing = new System.Collections.Generic.List<string>(3);

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

            GameObject template = UnityEngine.Object.Instantiate(source);
            template.name = "PeakUIButton";
            RemoveLocalizedTextComponent(template);
            UnityEngine.Object.DontDestroyOnLoad(template);

            if (!TrySetTemplatesProperty("ButtonTemplate", template, out string setError))
            {
                reason = setError;
                return false;
            }

            if (!_buttonTemplateRecovered)
            {
                Plugin.Log.LogInfo("[ItemBrowser] Recovered Templates.ButtonTemplate from Resources.");
                _buttonTemplateRecovered = true;
            }

            return true;
        }
        catch (Exception ex)
        {
            reason = $"{ex.GetType().Name}: {ex.Message}";
            return false;
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

    internal static void RemoveLocalizedTextComponent(GameObject target)
    {
        if (target == null) return;

        Component[] components = target.GetComponentsInChildren<Component>(true);
        for (int i = 0; i < components.Length; i++)
        {
            Component comp = components[i];
            if (comp == null) continue;
            if (string.Equals(comp.GetType().Name, "LocalizedText", StringComparison.Ordinal))
            {
                UnityEngine.Object.DestroyImmediate(comp);
            }
        }
    }

    internal static void BuildUI()
    {
        var page = MenuAPI.CreatePageWithBackground("ItemBrowserPage");
        SharedState.Page = page;
        page.OpenOnStart = false;
        page.CloseOnUICancel = true;
        page.AutoHideOnClose = true;

        page.SetOnOpen(() =>
        {
            SharedState.PageOpen = true;
            TabsManager.RefreshLanguageDependentContent(force: false);
            ItemLoader.EnsureItemList();
            VirtualList.RefreshListIfNeeded();
        });

        page.SetOnClose(() =>
        {
            SharedState.PageOpen = false;
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

        SharedState.HeaderTitleText = MenuAPI
            .CreateText(Localization.GetText("TITLE"), "HeaderText")
            .SetFontSize(26)
            .ParentTo(headerContainer)
            .ExpandToParent();
        SharedState.HeaderTitleText.TextMesh.alignment = TMPro.TextAlignmentOptions.Midline;
        SharedState.LastLanguageMarker = Localization.BuildLanguageMarker();

        var closeMenuButton = MenuAPI
            .CreateMenuButton(Localization.GetTextOrFallback("CLOSE_BUTTON", "Close"))
            .ParentTo(panel)
            .OnClick(ClosePage);
        SharedState.CloseMenuButton = closeMenuButton;

        RemoveLocalizedTextComponent(closeMenuButton.gameObject);

        var closeRect = closeMenuButton.GetComponent<RectTransform>();

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
        SharedState.TopControlsRect = topRect;
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

        SharedState.SearchInput = MenuAPI
            .CreateTextInput("SearchInput")
            .ParentTo(searchContainer)
            .ExpandToParent()
            .SetPlaceholder(Localization.GetText("SEARCH_PLACEHOLDER"))
            .OnValueChanged(VirtualList.OnSearchChanged);

        if (SharedState.SearchInput.InputField != null)
        {
            var inputField = SharedState.SearchInput.InputField;
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

        SharedState.MajorTabs = majorTabsObj.AddComponent<PeakHorizontalTabs>();
        SharedState.MajorTabs.SetBackgroundColor(new Color(0.18f, 0.14f, 0.1f, 0.95f));

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

        SharedState.SubCategoryTabsRoot = subTabsObj;
        SharedState.SubCategoryTabs = subTabsObj.AddComponent<PeakHorizontalTabs>();
        SharedState.SubCategoryTabs.SetBackgroundColor(new Color(0.18f, 0.14f, 0.1f, 0.95f));

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

        TabsManager.BuildMajorTabs(SharedState.MajorTabs);
        TabsManager.RebuildSubCategoryTabs();
        TabsManager.UpdateSubCategoryVisibility();

        ConfigureTabsContentLayout(majorTabsObj.transform.Find("Content"), 0f, true);
        ConfigureTabsContentLayout(subTabsObj.transform.Find("Content"), 0f, false);

        var listContainer = new GameObject("ListContainer")
            .AddComponent<PeakElement>()
            .ParentTo(panel)
            .SetAnchorMin(new Vector2(0f, 0f))
            .SetAnchorMax(new Vector2(1f, 1f))
            .SetOffsetMin(new Vector2(20f, 20f))
            .SetOffsetMax(new Vector2(-20f, SharedState.CurrentMajorFilter == MajorCategory.All ? -192f : -236f));

        SharedState.ListContainerRect = listContainer.GetComponent<RectTransform>();

        var listBg = listContainer.gameObject.AddComponent<Image>();
        listBg.color = new Color(0.11f, 0.09f, 0.07f, 0.95f);

        var listOutline = listContainer.gameObject.AddComponent<Outline>();
        listOutline.effectColor = new Color(1f, 1f, 1f, 0.14f);
        listOutline.effectDistance = new Vector2(1f, -1f);

        SharedState.ScrollContent = MenuAPI
            .CreateScrollableContent("ItemList")
            .ParentTo(listContainer)
            .ExpandToParent()
            .SetOffsetMin(new Vector2(12f, 12f))
            .SetOffsetMax(new Vector2(-26f, -12f));

        SharedState.ListScrollRect = SharedState.ScrollContent.GetComponent<ScrollRect>();
        if (SharedState.ListScrollRect != null)
        {
            SharedState.ListScrollRect.scrollSensitivity = 18f;
            SharedState.ListScrollRect.decelerationRate = 0.18f;
            SharedState.ListScrollRect.onValueChanged.AddListener(_ => VirtualList.OnVirtualizedScrollChanged());
        }

        SetupListScrollbar(listContainer);

        VirtualList.EnsureListLayoutReady();
        VirtualList.UpdateItemGridCellSize();

        page.gameObject.SetActive(false);
        VirtualList.MarkListDirty("UI built");
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

    private static void SetupListScrollbar(PeakElement listContainer)
    {
        if (SharedState.ScrollContent == null || listContainer == null)
        {
            return;
        }

        var scrollRect = SharedState.ScrollContent.GetComponent<ScrollRect>();
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

        _listScrollbar = barObj.GetComponent<Scrollbar>();
        _listScrollbar.direction = Scrollbar.Direction.BottomToTop;
        _listScrollbar.handleRect = handleRect;
        _listScrollbar.targetGraphic = handleImage;
        _listScrollbar.size = 0.2f;

        scrollRect.verticalScrollbar = _listScrollbar;
        scrollRect.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;
        scrollRect.verticalScrollbarSpacing = 2f;
        scrollRect.movementType = ScrollRect.MovementType.Clamped;
        scrollRect.elasticity = 0f;
        scrollRect.inertia = true;
        scrollRect.scrollSensitivity = 18f;
        scrollRect.decelerationRate = 0.18f;
    }

    internal static void NormalizeButtonLayout(PeakElement button, Vector2? size = null)
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

    internal static void ApplyItemButtonStyle(PeakMenuButton button)
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

    internal static Image CreateItemIconImage(PeakMenuButton button)
    {
        if (button == null)
        {
            throw new ArgumentNullException(nameof(button));
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
        iconImage.preserveAspect = true;
        iconImage.raycastTarget = false;

        return iconImage;
    }

    internal static void ClearStatusTextOverlay()
    {
        if (SharedState.ScrollContent == null)
        {
            return;
        }

        Transform? existing = SharedState.ScrollContent.transform.Find("StatusOverlay");
        if (existing != null)
        {
            UnityEngine.Object.Destroy(existing.gameObject);
        }
    }

    internal static void AddStatusText(string text)
    {
        if (SharedState.ScrollContent == null)
        {
            return;
        }

        ClearStatusTextOverlay();

        var overlay = new GameObject("StatusOverlay", typeof(RectTransform));
        overlay.transform.SetParent(SharedState.ScrollContent.transform, false);

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

    internal static void OpenPage()
    {
        if (SharedState.Page == null) return;

        if (_menuWindowOpenMethod == null)
        {
            _menuWindowOpenMethod = typeof(MenuWindow).GetMethod("Open", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        }

        if (_menuWindowOpenMethod != null)
        {
            _menuWindowOpenMethod.Invoke(SharedState.Page, null);
        }
        else
        {
            SharedState.Page.gameObject.SetActive(true);
        }
    }

    internal static void ClosePage()
    {
        if (SharedState.Page == null) return;

        if (_menuWindowCloseMethod == null)
        {
            _menuWindowCloseMethod = typeof(MenuWindow).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m => m.Name == "Close" && m.GetParameters().Length == 0)
                ?? typeof(MenuWindow).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(m => m.Name == "Close" && m.GetParameters().Length == 1);
        }

        if (_menuWindowCloseMethod != null)
        {
            var parameters = _menuWindowCloseMethod.GetParameters();
            object?[] args = parameters.Length == 1 ? new object?[] { true } : Array.Empty<object?>();
            _menuWindowCloseMethod.Invoke(SharedState.Page, args);
        }
        else
        {
            SharedState.Page.gameObject.SetActive(false);
        }
    }

    internal static void ResetState(string reason)
    {
        SharedState.Page = null;
        SharedState.HeaderTitleText = null;
        SharedState.CloseMenuButton = null;
        SharedState.SearchInput = null;
        SharedState.ScrollContent = null;
        SharedState.MajorTabs = null;
        SharedState.SubCategoryTabs = null;
        SharedState.SubCategoryTabsRoot = null;
        SharedState.TopControlsRect = null;
        SharedState.ListContainerRect = null;
        SharedState.ListScrollRect = null;
        SharedState.ItemGridLayout = null;
        _listScrollbar = null;
        _buttonTemplateRecovered = false;

        SharedState.ItemButtonPool.Clear();
        SharedState.ActiveRenderEntries.Clear();
        SharedState.MajorTabEntries.Clear();
        SharedState.SubCategoryTabEntries.Clear();

        SharedState.UiBuilt = false;
        SharedState.PageOpen = false;
        SharedState.ListNeedsRefresh = true;
        SharedState.ListRenderRunning = false;
        SharedState.ListRenderGeneration++;

        VirtualList.ResetState();

        Plugin.VerboseLog(reason);
    }
}
