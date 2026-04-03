using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

using UnityEngine;
using UnityEngine.UI;

namespace ItemBrowser;

internal static class IconResolver
{
    private static Coroutine? _iconPrewarmCoroutine;
    private static bool _iconPrewarmRunning;
    private static bool _iconPrewarmCompleted;
    private static int _iconPrewarmProcessedCount;
    private static int _iconPrewarmResolvedCount;
    private static float _nextIconPrewarmCheckTime;

    internal static Sprite? ResolveEntryIcon(ItemEntry entry)
    {
        if (entry.Icon != null)
        {
            return entry.Icon;
        }

        // Defer expensive icon fallback to background prewarm to keep first-open frame smooth.
        TryStartBackgroundIconPrewarm("VisibleEntryFallback");
        return null;
    }

    internal static void TickBackgroundIconPrewarm()
    {
        if (!SharedState.ItemListInitialized || SharedState.ItemPreloadRunning || _iconPrewarmRunning || _iconPrewarmCompleted)
        {
            return;
        }

        if (SharedState.PageOpen)
        {
            // Keep UI interaction smooth while browser is visible.
            return;
        }

        if (SharedState.ItemEntries.Count == 0)
        {
            return;
        }

        if (Time.unscaledTime < _nextIconPrewarmCheckTime)
        {
            return;
        }

        _nextIconPrewarmCheckTime = Time.unscaledTime + 0.15f;
        TryStartBackgroundIconPrewarm("AutoWarmup");
    }

    internal static bool TryStartBackgroundIconPrewarm(string reason)
    {
        if (!SharedState.ItemListInitialized || SharedState.ItemPreloadRunning || _iconPrewarmRunning || _iconPrewarmCompleted)
        {
            return false;
        }

        if (SharedState.Instance == null || SharedState.ItemEntries.Count == 0)
        {
            return false;
        }

        _iconPrewarmRunning = true;
        _iconPrewarmProcessedCount = 0;
        _iconPrewarmResolvedCount = 0;
        _iconPrewarmCoroutine = SharedState.Instance.StartCoroutine(WarmupMissingIconsGradually(reason));
        Plugin.VerboseLog($"Icon prewarm started ({reason}). Entries={SharedState.ItemEntries.Count}");
        return true;
    }

    private static IEnumerator WarmupMissingIconsGradually(string reason)
    {
        const int itemsPerFrame = 1;
        int budget = itemsPerFrame;

        // Yield once to avoid doing heavy icon fallback in the same frame that started this warmup.
        yield return null;

        for (int i = 0; i < SharedState.ItemEntries.Count; i++)
        {
            ItemEntry entry = SharedState.ItemEntries[i];
            _iconPrewarmProcessedCount++;

            if (entry.Icon == null)
            {
                Sprite? icon = GetItemIcon(entry.Prefab, allowHeavyFallback: true);
                if (icon != null)
                {
                    entry.UpdateIcon(icon);
                    _iconPrewarmResolvedCount++;
                }
            }

            budget--;
            if (budget <= 0)
            {
                budget = itemsPerFrame;
                yield return null;
            }
        }

        _iconPrewarmRunning = false;
        _iconPrewarmCoroutine = null;
        _iconPrewarmCompleted = true;
        Plugin.VerboseLog($"Icon prewarm completed ({reason}). Processed={_iconPrewarmProcessedCount}, Resolved={_iconPrewarmResolvedCount}");
    }

    internal static void ResetIconPrewarmState(bool stopCoroutine)
    {
        if (stopCoroutine)
        {
            StopTrackedCoroutine(ref _iconPrewarmCoroutine);
        }
        else
        {
            _iconPrewarmCoroutine = null;
        }

        _iconPrewarmRunning = false;
        _iconPrewarmCompleted = false;
        _iconPrewarmProcessedCount = 0;
        _iconPrewarmResolvedCount = 0;
        _nextIconPrewarmCheckTime = 0f;
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

    internal static Sprite? GetItemIcon(Item item, bool allowHeavyFallback = true)
    {
        if (item == null)
        {
            return null;
        }

        string key = item.name ?? string.Empty;
        if (SharedState.ItemIconCache.TryGetValue(key, out Sprite? cached))
        {
            return cached;
        }

        Sprite? icon = null;
        List<string>? probe = SharedState.ConfigVerboseLogs != null && SharedState.ConfigVerboseLogs.Value ? new List<string>() : null;
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
                icon = TryExtractMaterialTextureSprite(item, probe);
                if (icon == null && probe != null)
                {
                    probe.Add("HeavyFallback(material)=miss");
                }
            }
        }
        catch (Exception e)
        {
            Plugin.VerboseLog($"GetItemIcon failed for {item.name}: {e.GetType().Name} {e.Message}");
        }

        if (probe != null)
        {
            if (icon != null)
            {
                Plugin.VerboseLog($"Icon resolved for '{item.name}': {DescribeSprite(icon)} | {FormatProbe(probe)}");
            }
            else
            {
                Plugin.Log.LogWarning($"[ItemBrowser] Icon missing for '{item.name}'. Probe: {FormatProbe(probe)}");
            }
        }

        SharedState.ItemIconCache[key] = icon;
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
        if (SharedState.GeneratedTextureSpriteCache.TryGetValue(id, out Sprite cached) && cached != null)
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
            SharedState.GeneratedTextureSpriteCache[id] = created;
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
}
