using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace Orion.Services.Game;

/// <summary>
/// The two enemy searches farm mode needs, copied out of Daedalus's <c>TargetingService</c> and
/// <c>EnemyAttackability</c>. Left behind on purpose, because they are the rotation's concerns and
/// not a farmer's: the per-frame enemy cache, the line-of-sight and invulnerability filters, the
/// Eureka elemental-level guard and the per-fight forbidden-target hints.
/// </summary>
public sealed class EnemyScanner
{
    // BattleNpcKind values (Daedalus's BattleNpcKinds).
    private const byte Part = 1;
    private const byte Pet = 2;
    private const byte Chocobo = 3;
    private const byte Combatant = 5;
    private const byte NpcPartyMember = 9;

    /// <summary>Heavy Swing and Blizzard: one melee, one ranged damage action to ask the game about.</summary>
    private static readonly uint[] DamageProbeActionIds = [31, 142];

    private readonly IObjectTable _objectTable;

    public EnemyScanner(IObjectTable objectTable) => _objectTable = objectTable;

    /// <summary>
    /// The nearest enemy with aggro on the local player specifically: on the game's enmity
    /// ("hater") list, or targeting the player. Never matches a mob fighting someone else.
    /// </summary>
    public unsafe IBattleNpc? FindNearestAggroedEnemy(float maxRange, IPlayerCharacter player)
    {
        Span<uint> haterIds = stackalloc uint[32];
        var haterCount = 0;
        try
        {
            var uiState = FFXIVClientStructs.FFXIV.Client.Game.UI.UIState.Instance();
            if (uiState != null)
            {
                var count = Math.Min(uiState->Hater.HaterCount, 32);
                for (var i = 0; i < count; i++)
                    haterIds[haterCount++] = uiState->Hater.Haters[i].EntityId;
            }
        }
        catch
        {
            haterCount = 0;
        }

        IBattleNpc? nearest = null;
        var nearestDist = float.MaxValue;
        foreach (var enemy in ValidEnemies(maxRange, player))
        {
            var aggroedOnUs = enemy.TargetObjectId == player.GameObjectId;
            for (var i = 0; !aggroedOnUs && i < haterCount; i++)
                aggroedOnUs = haterIds[i] == enemy.EntityId;

            if (!aggroedOnUs)
                continue;

            var dist = Vector3.DistanceSquared(player.Position, enemy.Position);
            if (dist < nearestDist)
            {
                nearest = enemy;
                nearestDist = dist;
            }
        }
        return nearest;
    }

    /// <summary>
    /// The nearest enemy whose BNpcName row id is in <paramref name="nameIds"/>, optionally only
    /// those standing where <paramref name="inReach"/> says yes, and only those at or below
    /// <paramref name="maxLevel"/> when it is above 0.
    /// </summary>
    public IBattleNpc? FindNearestEnemyByNameIds(
        IReadOnlyCollection<uint> nameIds,
        float maxRange,
        IPlayerCharacter player,
        Func<Vector3, bool>? inReach = null,
        int maxLevel = 0)
    {
        if (nameIds.Count == 0)
            return null;

        IBattleNpc? nearest = null;
        var nearestDist = float.MaxValue;
        foreach (var enemy in ValidEnemies(maxRange, player))
        {
            var matches = false;
            foreach (var id in nameIds)
            {
                if (id == enemy.NameId)
                {
                    matches = true;
                    break;
                }
            }
            if (!matches)
                continue;

            if (maxLevel > 0 && enemy.Level > maxLevel)
                continue;

            if (inReach != null && !inReach(enemy.Position))
                continue;

            var dist = Vector3.DistanceSquared(player.Position, enemy.Position);
            if (dist < nearestDist)
            {
                nearest = enemy;
                nearestDist = dist;
            }
        }
        return nearest;
    }

    private IEnumerable<IBattleNpc> ValidEnemies(float maxRange, IPlayerCharacter player)
    {
        var playerPos = player.Position;
        foreach (var obj in _objectTable)
        {
            if (obj.ObjectKind != ObjectKind.BattleNpc || obj is not IBattleNpc npc)
                continue;

            if (!npc.IsTargetable || npc.IsDead)
                continue;

            var kind = (byte)npc.BattleNpcKind;
            if (kind is Pet or Chocobo or NpcPartyMember)
                continue;

            // An ordinary combatant, a part of a boss, or the unclassified kind overworld mobs report.
            if (kind is not (Combatant or Part) && npc.SubKind != 0)
                continue;

            var effectiveRange = maxRange + npc.HitboxRadius + player.HitboxRadius;
            if (Vector3.DistanceSquared(playerPos, npc.Position) > effectiveRange * effectiveRange)
                continue;

            if (!CanBeDamaged(npc))
                continue;

            yield return npc;
        }
    }

    /// <summary>
    /// The game's own action-target check: turns away battle NPCs that look hostile but cannot be
    /// hit (escort and protect objectives, story allies).
    /// </summary>
    private static unsafe bool CanBeDamaged(IBattleNpc npc)
    {
        try
        {
            var target = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)npc.Address;
            if (target == null)
                return false;

            foreach (var actionId in DamageProbeActionIds)
            {
                if (ActionManager.CanUseActionOnTarget(actionId, target))
                    return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }
}
