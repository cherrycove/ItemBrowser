using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

using PEAKLib.UI;

using UnityEngine;
using UnityEngine.UI;

namespace ItemBrowser;

internal static class VirtualList
{
    private const int VirtualizedColumnCount = 2;
    private const float VirtualizedCellHeight = 72f;
    private const float VirtualizedHorizontalSpacing = 12f;
    private const float VirtualizedVerticalSpacing = 8f;
    private const float VirtualizedPadding = 4f;
    private const int VirtualizedOverscanRows = 2;
    private const float VirtualizedScrollApplyInterval = 0.01f;

    private static Coroutine? _listRenderCoroutine;
    private static readonly List<int> _pendingDataIndices = new();
    private static readonly List<PooledItemButton> _freePoolButtons = new();
    private static int _firstDataIndex = -1;
    private static float _cellWidth = 320f;
    private static bool _suppressScrollUpdate;
    private static bool _scrollDirty;
    private static float _nextScrollApplyTime;

    private static Coroutine? _buttonPoolWarmupCoroutine;
    private static bool _buttonPoolWarmupRunning;
    private static int _buttonPoolWarmupTargetCount;
    private static float _nextButtonPoolWarmupCheckTime;

    internal static void TickVirtualizedScrollApply()
    {
        if (!_scrollDirty || SharedState.ListRenderRunning)
        {
            return;
        }

        if (Time.unscaledTime < _nextScrollApplyTime)
        {
            return;
        }

        _nextScrollApplyTime = Time.unscaledTime + VirtualizedScrollApplyInterval;
        UpdateVirtualizedVisibleWindow(force: false);
    }

    internal static void OnSearchChanged(string query)
    {
        SharedState.CurrentSearch = query ?? string.Empty;
        MarkListDirty();
        RefreshList();
    }

    internal static void MarkListDirty(string? reason = null)
    {
        SharedState.ListNeedsRefresh = true;
        StopListRenderCore(incrementGeneration: true);

        if (!string.IsNullOrWhiteSpace(reason))
        {
            Plugin.VerboseLog($"List marked dirty: {reason}");
        }
    }

    internal static void RefreshListIfNeeded(bool force = false)
    {
        if (force || SharedState.ListNeedsRefresh)
        {
            RefreshList();
        }
    }

    internal static void RefreshList()
    {
        if (SharedState.ScrollContent == null)
        {
            return;
        }

        UpdateItemGridCellSize();

        RectTransform? listContent = GetListContent();
        if (listContent == null)
        {
            Plugin.Log.LogWarning("[ItemBrowser] Scroll content not ready yet.");
            return;
        }

        CancelListRender();
        HideAllPooledItemButtons();
        UIBuilder.ClearStatusTextOverlay();

        if (!SharedState.ItemListInitialized)
        {
            ItemLoader.EnsureItemList();
            UIBuilder.AddStatusText(ItemLoader.GetPreloadStatusText());
            return;
        }

        IEnumerable<ItemEntry> filtered = SharedState.ItemEntries;
        string search = SharedState.CurrentSearch.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(search))
        {
            filtered = filtered.Where(entry => entry.SearchText.Contains(search));
        }

        filtered = filtered.Where(entry => CategorySystem.IsEntryInMajorCategory(entry, SharedState.CurrentMajorFilter));

        if (SharedState.CurrentSubCategoryFilter.HasValue)
        {
            filtered = filtered.Where(entry => entry.Category == SharedState.CurrentSubCategoryFilter.Value);
        }

        List<ItemEntry> filteredList = filtered.ToList();
        if (filteredList.Count == 0)
        {
            SharedState.ActiveRenderEntries.Clear();
            _firstDataIndex = -1;
            ResetVirtualizedContentHeight(0);
            HideAllPooledItemButtons();
            UIBuilder.AddStatusText(Localization.GetText("STATUS_EMPTY"));
            SharedState.ListNeedsRefresh = false;
            SharedState.ListRenderRunning = false;
            return;
        }

        if (SharedState.CurrentSubCategoryFilter.HasValue)
        {
            filteredList = filteredList
                .OrderBy(entry => CategorySystem.GetWikiSortOrder(entry))
                .ToList();
        }

        SharedState.ActiveRenderEntries.Clear();
        SharedState.ActiveRenderEntries.AddRange(filteredList);
        SharedState.ListNeedsRefresh = false;
        StartListRender(listContent, filteredList);
    }

    private static void StartListRender(RectTransform listContent, List<ItemEntry> entries)
    {
        if (listContent == null)
        {
            return;
        }

        int targetVisiblePool = CalculateVirtualizedVisiblePoolTarget();
        _buttonPoolWarmupTargetCount = Math.Max(_buttonPoolWarmupTargetCount, targetVisiblePool);

        ResetVirtualizedContentHeight(entries.Count);
        ResetVirtualizedScrollTop();

        int generation = ++SharedState.ListRenderGeneration;
        if (SharedState.Instance == null)
        {
            int initialCount = Math.Min(entries.Count, targetVisiblePool);
            for (int i = 0; i < initialCount; i++)
            {
                RenderVirtualizedSlot(i, i, entries[i]);
            }

            UpdateVirtualizedVisibleWindow(force: true);
            SharedState.ListRenderRunning = false;
            return;
        }

        SharedState.ListRenderRunning = true;
        _listRenderCoroutine = SharedState.Instance.StartCoroutine(RenderListGradually(generation, entries));
    }

    private static IEnumerator RenderListGradually(int generation, List<ItemEntry> entries)
    {
        int visibleTarget = CalculateVirtualizedVisiblePoolTarget();
        int renderCount = Math.Min(entries.Count, visibleTarget);

        const int firstFrameItems = 2;
        const int itemsPerFrame = 6;
        int budget = itemsPerFrame;

        yield return null;

        int initialBurst = Math.Min(renderCount, firstFrameItems);
        for (int i = 0; i < initialBurst; i++)
        {
            if (generation != SharedState.ListRenderGeneration)
            {
                yield break;
            }

            RenderVirtualizedSlot(i, i, entries[i]);
        }

        if (initialBurst > 0)
        {
            yield return null;
        }

        for (int i = initialBurst; i < renderCount; i++)
        {
            if (generation != SharedState.ListRenderGeneration)
            {
                yield break;
            }

            RenderVirtualizedSlot(i, i, entries[i]);

            budget--;
            if (budget <= 0)
            {
                budget = itemsPerFrame;
                yield return null;
            }
        }

        if (generation == SharedState.ListRenderGeneration)
        {
            _firstDataIndex = 0;
            UpdateVirtualizedVisibleWindow(force: true);
            SharedState.ListRenderRunning = false;
            _listRenderCoroutine = null;
        }
    }

    private static int CalculateVirtualizedVisiblePoolTarget()
    {
        float viewportHeight = 560f;

        if (SharedState.ListScrollRect != null && SharedState.ListScrollRect.viewport != null)
        {
            viewportHeight = Mathf.Max(SharedState.ListScrollRect.viewport.rect.height, VirtualizedCellHeight);
        }
        else if (SharedState.ListContainerRect != null)
        {
            viewportHeight = Mathf.Max(SharedState.ListContainerRect.rect.height - 24f, VirtualizedCellHeight);
        }

        float rowStride = VirtualizedCellHeight + VirtualizedVerticalSpacing;
        int visibleRows = Mathf.Max(1, Mathf.CeilToInt((viewportHeight + VirtualizedVerticalSpacing) / rowStride));
        int totalRows = visibleRows + VirtualizedOverscanRows * 2;
        return Mathf.Max(VirtualizedColumnCount * 2, totalRows * VirtualizedColumnCount);
    }

    private static void ResetVirtualizedContentHeight(int itemCount)
    {
        RectTransform? listContent = GetListContent();
        if (listContent == null)
        {
            return;
        }

        int rowCount = itemCount <= 0
            ? 0
            : (itemCount + VirtualizedColumnCount - 1) / VirtualizedColumnCount;

        float contentWidth = listContent.rect.width;
        if (contentWidth <= 0f && SharedState.ListScrollRect != null && SharedState.ListScrollRect.viewport != null)
        {
            contentWidth = SharedState.ListScrollRect.viewport.rect.width;
        }

        if (contentWidth <= 0f)
        {
            contentWidth = 680f;
        }

        float availableWidth = contentWidth - (VirtualizedPadding * 2f) - VirtualizedHorizontalSpacing;
        _cellWidth = Mathf.Clamp(availableWidth / VirtualizedColumnCount, 320f, 420f);

        float contentHeight = VirtualizedPadding * 2f;
        if (rowCount > 0)
        {
            contentHeight += (rowCount * VirtualizedCellHeight) + ((rowCount - 1) * VirtualizedVerticalSpacing);
        }

        if (SharedState.ListScrollRect != null && SharedState.ListScrollRect.viewport != null)
        {
            contentHeight = Mathf.Max(contentHeight, SharedState.ListScrollRect.viewport.rect.height + 1f);
        }

        Vector2 size = listContent.sizeDelta;
        size.y = contentHeight;
        listContent.sizeDelta = size;

        if (SharedState.ItemGridLayout != null)
        {
            SharedState.ItemGridLayout.enabled = false;
        }

        ClampVirtualizedContentPosition(listContent, contentHeight);
    }

    private static void ResetVirtualizedScrollTop()
    {
        if (SharedState.ListScrollRect == null)
        {
            _firstDataIndex = -1;
            return;
        }

        _suppressScrollUpdate = true;
        SharedState.ListScrollRect.StopMovement();
        SharedState.ListScrollRect.verticalNormalizedPosition = 1f;

        RectTransform? listContent = GetListContent();
        if (listContent != null)
        {
            Vector2 anchoredPos = listContent.anchoredPosition;
            if (anchoredPos.y != 0f)
            {
                anchoredPos.y = 0f;
                listContent.anchoredPosition = anchoredPos;
            }
        }

        _suppressScrollUpdate = false;
        _firstDataIndex = -1;
    }

    internal static void OnVirtualizedScrollChanged()
    {
        if (_suppressScrollUpdate || SharedState.ListRenderRunning)
        {
            return;
        }

        _scrollDirty = true;

        float now = Time.unscaledTime;
        bool hasMouseWheelInput = Mathf.Abs(Input.mouseScrollDelta.y) > 0.001f;
        if (hasMouseWheelInput && now >= _nextScrollApplyTime)
        {
            _nextScrollApplyTime = now + VirtualizedScrollApplyInterval;
            UpdateVirtualizedVisibleWindow(force: false);
        }
    }

    private static void UpdateVirtualizedVisibleWindow(bool force)
    {
        if (SharedState.ActiveRenderEntries.Count == 0 || SharedState.ScrollContent == null)
        {
            HideAllPooledItemButtons();
            _firstDataIndex = -1;
            _scrollDirty = false;
            _nextScrollApplyTime = Time.unscaledTime + VirtualizedScrollApplyInterval;
            return;
        }

        RectTransform? listContent = GetListContent();
        if (listContent == null)
        {
            return;
        }

        int visibleTarget = CalculateVirtualizedVisiblePoolTarget();
        int maxFirstIndex = Mathf.Max(0, SharedState.ActiveRenderEntries.Count - visibleTarget);

        float scrollY = Mathf.Max(0f, listContent.anchoredPosition.y - VirtualizedPadding);
        float rowStride = VirtualizedCellHeight + VirtualizedVerticalSpacing;
        int firstRow = Mathf.Max(0, Mathf.FloorToInt(scrollY / rowStride) - VirtualizedOverscanRows);
        int firstIndex = Mathf.Clamp(firstRow * VirtualizedColumnCount, 0, maxFirstIndex);
        if (firstIndex % VirtualizedColumnCount != 0)
        {
            firstIndex--;
        }

        if (!force && firstIndex == _firstDataIndex && SharedState.ItemButtonPool.Count >= visibleTarget)
        {
            return;
        }

        int renderCount = Mathf.Min(visibleTarget, SharedState.ActiveRenderEntries.Count - firstIndex);
        int previousFirstIndex = _firstDataIndex;
        int previousLastIndex = previousFirstIndex >= 0
            ? previousFirstIndex + Mathf.Min(visibleTarget, SharedState.ActiveRenderEntries.Count - previousFirstIndex) - 1
            : -1;
        int currentLastIndex = renderCount > 0 ? firstIndex + renderCount - 1 : -1;

        if (!force && previousFirstIndex >= 0 && currentLastIndex >= 0 && firstIndex != previousFirstIndex)
        {
            bool hasOverlap = !(currentLastIndex < previousFirstIndex || firstIndex > previousLastIndex);
            if (hasOverlap)
            {
                bool incrementalApplied = BindVirtualizedWindowIncremental(previousFirstIndex, previousLastIndex, firstIndex, currentLastIndex, renderCount);
                if (incrementalApplied)
                {
                    _firstDataIndex = firstIndex;
                    return;
                }
            }
        }

        for (int i = 0; i < renderCount; i++)
        {
            int dataIndex = firstIndex + i;
            RenderVirtualizedSlot(i, dataIndex, SharedState.ActiveRenderEntries[dataIndex]);
        }

        for (int i = renderCount; i < SharedState.ItemButtonPool.Count; i++)
        {
            SharedState.ItemButtonPool[i].Button.gameObject.SetActive(false);
        }

        _firstDataIndex = firstIndex;
        _scrollDirty = false;

        float contentHeight = listContent.sizeDelta.y;
        ClampVirtualizedContentPosition(listContent, contentHeight);
    }

    private static void ClampVirtualizedContentPosition(RectTransform listContent, float contentHeight)
    {
        if (SharedState.ListScrollRect == null || SharedState.ListScrollRect.viewport == null)
        {
            return;
        }

        float viewportHeight = Mathf.Max(1f, SharedState.ListScrollRect.viewport.rect.height);
        float maxScroll = Mathf.Max(0f, contentHeight - viewportHeight);

        Vector2 anchoredPos = listContent.anchoredPosition;
        float clampedY = Mathf.Clamp(anchoredPos.y, 0f, maxScroll);
        if (!Mathf.Approximately(clampedY, anchoredPos.y))
        {
            anchoredPos.y = clampedY;
            listContent.anchoredPosition = anchoredPos;
        }
    }

    private static bool BindVirtualizedWindowIncremental(
        int previousFirstIndex,
        int previousLastIndex,
        int currentFirstIndex,
        int currentLastIndex,
        int renderCount)
    {
        _pendingDataIndices.Clear();
        _freePoolButtons.Clear();

        for (int dataIndex = currentFirstIndex; dataIndex <= currentLastIndex; dataIndex++)
        {
            if (dataIndex < previousFirstIndex || dataIndex > previousLastIndex)
            {
                _pendingDataIndices.Add(dataIndex);
            }
        }

        if (_pendingDataIndices.Count == 0)
        {
            return true;
        }

        int poolSearchCount = Mathf.Min(SharedState.ItemButtonPool.Count, renderCount);
        for (int i = 0; i < poolSearchCount; i++)
        {
            PooledItemButton pooled = SharedState.ItemButtonPool[i];
            int boundDataIndex = pooled.BoundDataIndex;
            if (boundDataIndex < currentFirstIndex || boundDataIndex > currentLastIndex)
            {
                _freePoolButtons.Add(pooled);
            }
        }

        int assignCount = Mathf.Min(_pendingDataIndices.Count, _freePoolButtons.Count);
        if (assignCount < _pendingDataIndices.Count)
        {
            return false;
        }

        for (int i = 0; i < assignCount; i++)
        {
            int dataIndex = _pendingDataIndices[i];
            PooledItemButton pooled = _freePoolButtons[i];
            pooled.BoundDataIndex = -1;
            RenderVirtualizedSlotByButton(pooled, dataIndex, SharedState.ActiveRenderEntries[dataIndex]);
        }

        return true;
    }

    private static void RenderVirtualizedSlotByButton(PooledItemButton pooled, int dataIndex, ItemEntry entry)
    {
        if (pooled == null)
        {
            return;
        }

        if (!pooled.Button.gameObject.activeSelf)
        {
            pooled.Button.gameObject.SetActive(true);
        }

        if (pooled.Button.Text != null)
        {
            pooled.Button.Text.text = entry.DisplayName;
        }

        pooled.Binder.Prefab = entry.Prefab;

        Sprite? icon = IconResolver.ResolveEntryIcon(entry);
        pooled.IconImage.sprite = icon;
        pooled.IconImage.color = icon != null ? Color.white : new Color(0.48f, 0.42f, 0.35f, 0.45f);
        pooled.BoundDataIndex = dataIndex;

        if (pooled.PoolIndex >= 0)
        {
            PositionVirtualizedButton(pooled.PoolIndex, dataIndex);
        }
    }

    private static void RenderVirtualizedSlot(int poolIndex, int dataIndex, ItemEntry entry)
    {
        if (poolIndex < 0)
        {
            return;
        }

        RectTransform? listContent = GetListContent();
        if (listContent == null)
        {
            return;
        }

        PooledItemButton pooled = EnsurePooledItemButton(poolIndex, listContent);
        if (!pooled.Button.gameObject.activeSelf)
        {
            pooled.Button.gameObject.SetActive(true);
        }

        bool needsRebind = pooled.BoundDataIndex != dataIndex;
        if (needsRebind)
        {
            if (pooled.Button.Text != null)
            {
                pooled.Button.Text.text = entry.DisplayName;
            }

            pooled.Binder.Prefab = entry.Prefab;

            Sprite? icon = IconResolver.ResolveEntryIcon(entry);
            pooled.IconImage.sprite = icon;
            pooled.IconImage.color = icon != null ? Color.white : new Color(0.48f, 0.42f, 0.35f, 0.45f);
            pooled.BoundDataIndex = dataIndex;
        }

        PositionVirtualizedButton(poolIndex, dataIndex);
    }

    private static void PositionVirtualizedButton(int poolIndex, int dataIndex)
    {
        if (poolIndex < 0 || poolIndex >= SharedState.ItemButtonPool.Count)
        {
            return;
        }

        RectTransform? rect = SharedState.ItemButtonPool[poolIndex].ButtonRect;
        if (rect == null)
        {
            return;
        }

        int row = dataIndex / VirtualizedColumnCount;
        int column = dataIndex % VirtualizedColumnCount;
        float x = VirtualizedPadding + column * (_cellWidth + VirtualizedHorizontalSpacing);
        float y = VirtualizedPadding + row * (VirtualizedCellHeight + VirtualizedVerticalSpacing);

        Vector2 anchorTopLeft = new Vector2(0f, 1f);
        if (rect.anchorMin != anchorTopLeft)
        {
            rect.anchorMin = anchorTopLeft;
        }

        if (rect.anchorMax != anchorTopLeft)
        {
            rect.anchorMax = anchorTopLeft;
        }

        if (rect.pivot != anchorTopLeft)
        {
            rect.pivot = anchorTopLeft;
        }

        Vector2 targetSize = new Vector2(_cellWidth, VirtualizedCellHeight);
        if (rect.sizeDelta != targetSize)
        {
            rect.sizeDelta = targetSize;
        }

        Vector2 targetPos = new Vector2(x, -y);
        if (rect.anchoredPosition != targetPos)
        {
            rect.anchoredPosition = targetPos;
        }

        if (rect.localScale != Vector3.one)
        {
            rect.localScale = Vector3.one;
        }
    }

    private static void CancelListRender()
    {
        StopListRenderCore(incrementGeneration: true);
    }

    internal static PooledItemButton EnsurePooledItemButton(int index, RectTransform listContent)
    {
        while (SharedState.ItemButtonPool.Count <= index)
        {
            var button = MenuAPI
                .CreateMenuButton(string.Empty)
                .ParentTo(listContent)
                .OnClick(() => { });

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

            Image iconImage = UIBuilder.CreateItemIconImage(button);
            UIBuilder.ApplyItemButtonStyle(button);
            UIBuilder.NormalizeButtonLayout(button);

            var binder = button.gameObject.GetComponent<ItemButtonBinder>();
            if (binder == null)
            {
                binder = button.gameObject.AddComponent<ItemButtonBinder>();
            }

            if (button.Button != null)
            {
                button.Button.onClick.RemoveAllListeners();
                button.Button.onClick.AddListener(binder.HandleClick);
            }

            button.gameObject.SetActive(false);

            RectTransform buttonRect = rect ?? button.GetComponent<RectTransform>();
            SharedState.ItemButtonPool.Add(new PooledItemButton(button, buttonRect, iconImage, binder, SharedState.ItemButtonPool.Count));
        }

        return SharedState.ItemButtonPool[index];
    }

    internal static void HideAllPooledItemButtons()
    {
        for (int i = 0; i < SharedState.ItemButtonPool.Count; i++)
        {
            SharedState.ItemButtonPool[i].Button.gameObject.SetActive(false);
            SharedState.ItemButtonPool[i].BoundDataIndex = -1;
        }
    }

    internal static RectTransform? GetListContent()
    {
        if (SharedState.ScrollContent == null)
        {
            return null;
        }

        if (SharedState.ScrollContent.Content != null)
        {
            return SharedState.ScrollContent.Content;
        }

        return SharedState.ScrollContent.transform.Find("Content") as RectTransform;
    }

    internal static void EnsureListLayoutReady()
    {
        RectTransform? content = GetListContent();
        if (content == null)
        {
            return;
        }

        var contentLayout = content.GetComponent<VerticalLayoutGroup>();
        if (contentLayout != null)
        {
            UnityEngine.Object.DestroyImmediate(contentLayout);
        }

        var contentFitter = content.GetComponent<ContentSizeFitter>();
        if (contentFitter != null)
        {
            UnityEngine.Object.DestroyImmediate(contentFitter);
        }

        content.anchorMin = new Vector2(0f, 1f);
        content.anchorMax = new Vector2(1f, 1f);
        content.pivot = new Vector2(0.5f, 1f);
        Vector2 contentPos = content.anchoredPosition;
        contentPos.x = 0f;
        if (!_suppressScrollUpdate)
        {
            contentPos.y = Mathf.Max(0f, contentPos.y);
        }
        content.anchoredPosition = contentPos;

        SharedState.ItemGridLayout = content.GetComponent<GridLayoutGroup>();
        if (SharedState.ItemGridLayout == null)
        {
            try
            {
                SharedState.ItemGridLayout = content.gameObject.AddComponent<GridLayoutGroup>();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[ItemBrowser] Failed to add GridLayoutGroup: {e.GetType().Name} {e.Message}");
                return;
            }
        }

        if (SharedState.ItemGridLayout == null)
        {
            return;
        }

        SharedState.ItemGridLayout.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
        SharedState.ItemGridLayout.constraintCount = 2;
        SharedState.ItemGridLayout.spacing = new Vector2(12f, 8f);
        SharedState.ItemGridLayout.startAxis = GridLayoutGroup.Axis.Horizontal;
        SharedState.ItemGridLayout.startCorner = GridLayoutGroup.Corner.UpperLeft;
        SharedState.ItemGridLayout.childAlignment = TextAnchor.UpperLeft;
    }

    internal static void UpdateItemGridCellSize()
    {
        EnsureListLayoutReady();

        RectTransform? contentRect = GetListContent();
        if (SharedState.ItemGridLayout == null || contentRect == null)
        {
            return;
        }

        float contentWidth = contentRect.rect.width;
        if (contentWidth <= 0f)
        {
            contentWidth = 680f;
        }

        int columns = Math.Max(1, SharedState.ItemGridLayout.constraintCount);
        float horizontalPadding = SharedState.ItemGridLayout.padding.left + SharedState.ItemGridLayout.padding.right;
        float spacing = SharedState.ItemGridLayout.spacing.x * (columns - 1);
        float width = (contentWidth - horizontalPadding - spacing) / columns;
        width = Mathf.Clamp(width, 320f, 420f);

        SharedState.ItemGridLayout.cellSize = new Vector2(width, 72f);
        SharedState.ItemGridLayout.padding = new RectOffset(4, 4, 4, 4);
    }

    internal static void TickBackgroundButtonPoolWarmup()
    {
        if (!SharedState.ItemListInitialized || SharedState.ItemPreloadRunning || _buttonPoolWarmupRunning)
        {
            return;
        }

        if (SharedState.PageOpen)
        {
            return;
        }

        int targetCount = CalculateButtonPoolWarmupTarget(SharedState.ItemEntries.Count);
        if (targetCount <= 0 || SharedState.ItemButtonPool.Count >= targetCount)
        {
            return;
        }

        if (Time.unscaledTime < _nextButtonPoolWarmupCheckTime)
        {
            return;
        }

        _nextButtonPoolWarmupCheckTime = Time.unscaledTime + 0.2f;
        TryStartBackgroundButtonPoolWarmup("AutoWarmup", targetCount);
    }

    internal static bool TryStartBackgroundButtonPoolWarmup(string reason, int targetCount)
    {
        if (_buttonPoolWarmupRunning)
        {
            return false;
        }

        if (SharedState.Instance == null || SharedState.ScrollContent == null)
        {
            return false;
        }

        RectTransform? listContent = GetListContent();
        if (listContent == null)
        {
            return false;
        }

        if (targetCount <= 0 || SharedState.ItemButtonPool.Count >= targetCount)
        {
            return false;
        }

        _buttonPoolWarmupTargetCount = targetCount;
        _buttonPoolWarmupRunning = true;
        _buttonPoolWarmupCoroutine = SharedState.Instance.StartCoroutine(WarmupButtonPoolGradually(listContent, reason));
        Plugin.VerboseLog($"Button pool warmup started ({reason}). Target={targetCount}, Existing={SharedState.ItemButtonPool.Count}");
        return true;
    }

    internal static int CalculateButtonPoolWarmupTarget(int sourceCount)
    {
        if (sourceCount <= 0)
        {
            return 0;
        }

        int visibleTarget = CalculateVirtualizedVisiblePoolTarget();
        int minimumTarget = VirtualizedColumnCount * 4;
        int warmupTarget = Mathf.Max(minimumTarget, visibleTarget);
        return Mathf.Min(sourceCount, warmupTarget);
    }

    private static IEnumerator WarmupButtonPoolGradually(RectTransform listContent, string reason)
    {
        const int buttonsPerFrame = 2;
        int budget = buttonsPerFrame;

        yield return null;

        while (SharedState.ItemButtonPool.Count < _buttonPoolWarmupTargetCount)
        {
            EnsurePooledItemButton(SharedState.ItemButtonPool.Count, listContent);
            budget--;

            if (budget <= 0)
            {
                budget = buttonsPerFrame;
                yield return null;
            }
        }

        _buttonPoolWarmupRunning = false;
        _buttonPoolWarmupCoroutine = null;
        Plugin.VerboseLog($"Button pool warmup completed ({reason}). Count={SharedState.ItemButtonPool.Count}");
    }

    private static void StopListRenderCore(bool incrementGeneration)
    {
        if (incrementGeneration)
        {
            SharedState.ListRenderGeneration++;
        }

        StopTrackedCoroutine(ref _listRenderCoroutine);
        SharedState.ListRenderRunning = false;
    }

    internal static void ResetButtonPoolWarmupState(bool stopCoroutine)
    {
        if (stopCoroutine)
        {
            StopTrackedCoroutine(ref _buttonPoolWarmupCoroutine);
        }
        else
        {
            _buttonPoolWarmupCoroutine = null;
        }

        _buttonPoolWarmupRunning = false;
        _buttonPoolWarmupTargetCount = 0;
        _nextButtonPoolWarmupCheckTime = 0f;
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

    internal static void ResetState()
    {
        _pendingDataIndices.Clear();
        _freePoolButtons.Clear();
        _firstDataIndex = -1;
        _cellWidth = 320f;
        _suppressScrollUpdate = false;
        _scrollDirty = false;
        _nextScrollApplyTime = 0f;
        ResetButtonPoolWarmupState(stopCoroutine: true);
        StopListRenderCore(incrementGeneration: false);
    }
}
