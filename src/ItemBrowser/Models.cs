using PEAKLib.UI.Elements;

using UnityEngine;
using UnityEngine.UI;

namespace ItemBrowser;

internal enum MajorCategory
{
    All,
    Food,
    Weapon
}

internal enum ItemCategory
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

internal sealed class ItemEntry
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

internal sealed class PooledItemButton
{
    public PeakMenuButton Button { get; }
    public RectTransform ButtonRect { get; }
    public Image IconImage { get; }
    public ItemButtonBinder Binder { get; }
    public int PoolIndex { get; }
    public int BoundDataIndex { get; set; }

    public PooledItemButton(PeakMenuButton button, RectTransform buttonRect, Image iconImage, ItemButtonBinder binder, int poolIndex)
    {
        Button = button;
        ButtonRect = buttonRect;
        IconImage = iconImage;
        Binder = binder;
        PoolIndex = poolIndex;
        BoundDataIndex = -1;
    }
}

internal sealed class ItemButtonBinder : MonoBehaviour
{
    public Item? Prefab;

    public void HandleClick()
    {
        if (Prefab != null)
        {
            ItemSpawner.SpawnItem(Prefab);
        }
    }
}

internal sealed class CategoryTab
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

internal sealed class MajorCategoryTab
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
