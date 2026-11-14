using Unciv.Logic.Map.MapUnit.Actions;
using Unciv.Models;
using Unciv.Models.Ruleset.Unique;

namespace Unciv.Logic.Battle;

public static partial class Battle
{
    /// <summary>
    /// Moves <paramref name="attacker"/> to <paramref name="attackableTile"/>, handles siege setup then attacks if still possible
    /// (by calling <see cref="Attack"/> or <see cref="Nuke.NUKE"/>). Does _not_ play the attack sound!
    ///
    /// Currently not used by UI, only by automation via BattleHelper.tryAttackNearbyEnemy
    /// </summary>
    public static void MoveAndAttack(MapUnitCombatant attacker, AttackableTile attackableTile)
    {
        if (!MovePreparingAttack(attacker, attackableTile, true)) return;
        AttackOrNuke(attacker, attackableTile);
    }

    /// <summary>
    /// Moves <paramref name="attacker"/> to <paramref name="attackableTile"/>, handles siege setup and returns <c>true</c> if an attack is still possible.
    ///
    /// This is a logic function, not UI, so e.g. sound needs to be handled after calling this.
    /// Only relevant for <see cref="MapUnitCombatant"/> - cities can't move, so callers with a <see cref="CityCombatant"/> attacker
    /// should skip calling this and treat the attack as always still possible.
    /// </summary>
    public static bool MovePreparingAttack(MapUnitCombatant attacker, AttackableTile attackableTile, bool tryHealPillage = false)
    {
        var tilesMovedThrough = attacker.Unit.Movement.GetDistanceToTiles().GetPathToTile(attackableTile.TileToAttackFrom);
        attacker.Unit.Movement.MoveToTile(attackableTile.TileToAttackFrom);
        // When calculating movement distance, we assume that a hidden tile is 1 movement point,
        // which can lead to EXCEEDINGLY RARE edge cases where you think
        // that you can attack a tile by passing through a HIDDEN TILE,
        // but the hidden tile is actually IMPASSIBLE, so you stop halfway!
        if (attacker.GetTile() != attackableTile.TileToAttackFrom) return false;
        // Rarely, a melee unit will target a civilian then move through the civilian to get
        // to attackableTile.tileToAttackFrom, meaning that they take the civilian.
        // This can lead to:
        // A. the melee unit from trying to capture their own unit (see #7282)
        // B. The civilian unit disappearing entirely (e.g. Great Person) and trying to capture a non-existent unit (see #8563)
        var combatant = GetMapCombatantOfTile(attackableTile.TileToAttack);
        if (combatant == null || combatant.GetCivInfo() == attacker.GetCivInfo()) return false;
        // Alternatively, maybe we DID reach that tile, but it turned out to be a hill or something,
        // so we expended all of our movement points!
        if (attacker.HasUnique(UniqueType.MustSetUp)
            && !attacker.Unit.IsSetUpForSiege()
            && attacker.Unit.HasMovement())
        {
            attacker.Unit.Action = UnitActionType.SetUp.Value();
            attacker.Unit.UseMovementPoints(1f);
        }

        if (tryHealPillage)
        {
            // Now lets retroactively see if we can pillage any improvement on the path improvement to heal
            // while still being able to attack
            foreach (var tileToPillage in tilesMovedThrough)
            {
                if (attacker.Unit.CurrentMovement <= 1f || attacker.Unit.Health > 90) break; // We are done pillaging

                if (UnitActionsPillage.CanPillage(attacker.Unit, tileToPillage)
                    && tileToPillage.CanPillageTileImprovement())
                {
                    UnitActionsPillage.GetPillageAction(attacker.Unit, tileToPillage)?.Action?.Invoke();
                }
            }
        }
        return attacker.Unit.HasMovement();
    }
}
