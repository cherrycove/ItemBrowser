using System;

using Photon.Pun;

using UnityEngine;

using Zorro.Core.CLI;

namespace ItemBrowser;

[ConsoleClassCustomizer("ItemBrowser")]
internal static class ItemSpawner
{
    [ConsoleCommand(false)]
    public static void Spawn(Item item)
    {
        if (item == null)
        {
            ReportConsoleWarning("[ItemBrowser] Spawn command failed: resolved item was null.");
            return;
        }

        if (!TrySpawnItem(item, out string spawnMessage))
        {
            ReportConsoleWarning(spawnMessage);
            return;
        }

        ReportConsoleInfo(spawnMessage);
    }

    public static void SpawnItem(Item prefab)
    {
        TrySpawnItem(prefab, out _);
    }

    /// <summary>
    /// 判断角色是否处于死亡/灵魂/完全晕倒状态（无法持有物品）。
    /// </summary>
    private static bool IsCharacterDeadOrGhost(Character character)
    {
        if (character == null) return true;
        if (character.data == null) return true;
        return character.data.dead || character.IsGhost || character.data.fullyPassedOut;
    }

    /// <summary>
    /// 当本地玩家死亡/灵魂时，返回应接收物品的角色（观战目标）。
    /// 功能禁用或玩家存活时返回本地玩家自身。
    /// </summary>
    private static Character? ResolveSpawnTarget(Character localPlayer, out string? warningMessage)
    {
        warningMessage = null;

        // 功能未启用 或 本地玩家存活 → 照旧发给自己
        if (!SharedState.ConfigGhostSendToObserved!.Value
            || !IsCharacterDeadOrGhost(localPlayer))
        {
            return localPlayer;
        }

        // 本地玩家已死亡/灵魂，尝试获取观战目标
        Character? observed = Character.observedCharacter;

        if (observed == null)
        {
            warningMessage = "[ItemBrowser] You are dead/ghost but not spectating anyone. Cannot send item.";
            return null;
        }

        if (IsCharacterDeadOrGhost(observed))
        {
            string observedName = observed.name ?? "unknown";
            warningMessage = $"[ItemBrowser] Spectated player '{observedName}' is also dead or incapacitated. Cannot send item.";
            return null;
        }

        return observed;
    }

    private static bool TrySpawnItem(Item prefab, out string statusMessage)
    {
        statusMessage = string.Empty;

        if (prefab == null)
        {
            statusMessage = "[ItemBrowser] Item prefab is null.";
            return false;
        }

        Character player = Character.localCharacter;
        if (player == null)
        {
            statusMessage = "[ItemBrowser] No local character available. Enter a match to spawn items.";
            Plugin.Log.LogWarning(statusMessage);
            return false;
        }

        if (!SharedState.ConfigAllowOnline!.Value)
        {
            if (!PhotonNetwork.OfflineMode && (!PhotonNetwork.IsConnected || !PhotonNetwork.InRoom))
            {
                statusMessage = "[ItemBrowser] Online spawn disabled or not in room.";
                Plugin.Log.LogWarning(statusMessage);
                return false;
            }
        }

        // ── 灵魂观战时重定向目标 ──
        Character? spawnTarget = ResolveSpawnTarget(player, out string? targetWarning);
        if (spawnTarget == null)
        {
            statusMessage = targetWarning ?? "[ItemBrowser] No valid spawn target found.";
            Plugin.Log.LogWarning(statusMessage);
            return false;
        }

        bool isRedirected = spawnTarget != player;
        string targetDesc = isRedirected
            ? $"observed teammate '{spawnTarget.name ?? "unknown"}'"
            : "local player";

        if (isRedirected)
        {
            Plugin.VerboseLog($"Ghost redirect -> {targetDesc}");
        }

        string viewId = spawnTarget.photonView?.ViewID.ToString() ?? "null";
        Plugin.VerboseLog($"Spawn: '{prefab.name}', target={targetDesc}, viewId={viewId}, online={PhotonNetwork.IsConnected}, inRoom={PhotonNetwork.InRoom}");

        try
        {
            if (GameUtils.instance != null)
            {
                GameUtils.instance.InstantiateAndGrab(prefab, spawnTarget, 0);
                Plugin.VerboseLog($"Spawn completed: {prefab.name}");
                statusMessage = isRedirected
                    ? $"[ItemBrowser] Spawned {FormatItemCommandLabel(prefab)} -> sent to {targetDesc}."
                    : $"[ItemBrowser] Spawned {FormatItemCommandLabel(prefab)}.";
                return true;
            }

            statusMessage = $"[ItemBrowser] GameUtils.instance is null, cannot spawn {prefab.name}.";
            Plugin.Log.LogError(statusMessage);
            return false;
        }
        catch (Exception e)
        {
            statusMessage = $"[ItemBrowser] Spawn failed for {prefab.name}: {e.Message}";
            Plugin.Log.LogError(statusMessage);
            return false;
        }
    }

    private static string FormatItemCommandLabel(Item item)
    {
        if (item == null)
        {
            return "<null>";
        }

        string prefabName = Localization.NormalizeItemNameForMap(item.name ?? string.Empty);
        string displayName = ItemLoader.GetResolvedItemDisplayName(item).Trim();
        if (string.IsNullOrWhiteSpace(displayName) || string.Equals(displayName, prefabName, StringComparison.OrdinalIgnoreCase))
        {
            return $"'{prefabName}'";
        }

        return $"'{displayName}' ({prefabName})";
    }

    private static void ReportConsoleInfo(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        Plugin.Log.LogInfo(message);
        Debug.Log(message);
    }

    private static void ReportConsoleWarning(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        Plugin.Log.LogWarning(message);
        Debug.LogWarning(message);
    }

    public static void RefreshConsoleCommands()
    {
        try
        {
            ConsoleHandler.Initialize(ConsoleHandler.ScanForConsoleCommands(), ConsoleHandler.ScanForTypeParsers());
            Plugin.VerboseLog("Console commands refreshed.");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[ItemBrowser] Failed to refresh console commands: {e.GetType().Name} {e.Message}");
        }
    }
}
