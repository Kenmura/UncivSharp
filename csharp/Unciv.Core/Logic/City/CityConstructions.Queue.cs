using Unciv.Models.Ruleset;
using Unciv.Models.Ruleset.Tile;
using Unciv.Models.Ruleset.Unique;
using Unciv.Presentation.Civilopedia;
using Unciv.Presentation.Components;
using TileClass = Unciv.Logic.Map.Tiles.Tile;

namespace Unciv.Logic.City;

public partial class CityConstructions
{
    public FormattedLine GetProductionMarkup(Ruleset ruleset)
    {
        var currentConstructionSnapshot = CurrentConstructionName();
        if (currentConstructionSnapshot.Length == 0) return new FormattedLine();
        string category;
        if (ruleset.Buildings.TryGetValue(currentConstructionSnapshot, out var building) && building.IsAnyWonder())
            category = CivilopediaCategories.Wonder.ToString();
        else if (ruleset.Buildings.ContainsKey(currentConstructionSnapshot))
            category = CivilopediaCategories.Building.ToString();
        else if (ruleset.Units.ContainsKey(currentConstructionSnapshot))
            category = CivilopediaCategories.Unit.ToString();
        else
            category = "";
        var label = "{" + currentConstructionSnapshot + "}";
        if (!PerpetualConstruction.IsNamePerpetual(currentConstructionSnapshot))
        {
            var turnsLeft = TurnsToConstruction(currentConstructionSnapshot);
            label += $" - {turnsLeft}{Fonts.Turn}";
        }
        return category.Length == 0 ? new FormattedLine(label)
            : new FormattedLine(label, link: $"{category}/{currentConstructionSnapshot}");
    }


    public virtual void ChooseNextConstruction()
    {
        if (!IsQueueEmptyOrIdle())
        {
            // If the USER set a perpetual construction, then keep it!
            if (GetConstruction(CurrentConstructionName()) is not PerpetualConstruction || CurrentConstructionIsUserSet) return;
        }

        var gameInfo = City.Civ.GameInfo;
        // T183: the civ's own setting applies whenever it acts (Kotlin: only if the local user is the current player, UserId gating)
        var autoAssignProduction = City.Civ.IsHuman() && gameInfo.GetPlayerSettings(City.Civ.CivId).AutoAssignCityProduction;
        if ((autoAssignProduction // only automate if the human player has the setting to automate production
                    || gameInfo.IsFullAutoPlayAI)
            || City.Civ.IsAI() || City.IsPuppet)
        {
            CityConstructionsAutomation.ChooseNextConstruction(this);
        }

        // Support for UniqueType.CreatesOneImprovement - if an Improvement-creating Building was auto-queued, auto-choose a tile:
        if (GetCurrentConstruction() is not Building building) return;
        var improvement = building.GetImprovementToCreate(City.GetRuleset(), City.Civ);
        if (improvement == null) return;

        if (GetTileForImprovement(improvement.Name) == null)
        {
            var newTile = GetTileForConstructionImprovementAutomation(improvement);
            if (newTile == null) return;
            TryPlaceCreateOneImprovementMarker(improvement, newTile);
        }
    }

    /// <summary><c>Automation.getTileForConstructionImprovement(city, improvement)</c></summary>
    private TileClass? GetTileForConstructionImprovementAutomation(TileImprovement improvement) =>
        City.Population.Automation.GetTileForConstructionImprovement(City, improvement);

    /// <summary>Whether this city may mark its own <paramref name="tile"/> to create <paramref name="improvement"/> when construction completes.</summary>
    public bool CanPlaceCreateOneImprovementOn(TileImprovement improvement, TileClass tile) =>
        ReferenceEquals(tile.GetCity(), City)
        && City.TilesInRange.Contains(tile)
        && !tile.IsCityCenter()
        && !tile.IsMarkedForCreatesOneImprovement()
        && tile.ImprovementFunctions.CanBuildImprovement(improvement, City.State);

    /// <summary>
    /// Try to mark <paramref name="tile"/> so completing this construction will create <paramref name="improvement"/> there.
    ///
    /// The tile must already belong to this city; reaching marker placement with another
    /// city's tile is invalid internal state and fails loudly.
    /// </summary>
    /// <returns><c>true</c> if <paramref name="tile"/> is now marked for <paramref name="improvement"/>, or <c>false</c> if it is not eligible.</returns>
    public bool TryPlaceCreateOneImprovementMarker(TileImprovement improvement, TileClass tile)
    {
        if (ReferenceEquals(tile.GetCity(), City) && tile.IsMarkedForCreatesOneImprovement(improvement.Name))
            return true;
        if (!ReferenceEquals(tile.GetCity(), City))
            throw new InvalidOperationException(
                $"Cannot mark {tile.Position} for {nameof(UniqueType.CreatesOneImprovement)} in {City.Name}: tile is owned by {(tile.GetCity() as City)?.Name ?? "no city"}");
        if (!CanPlaceCreateOneImprovementOn(improvement, tile))
            return false;

        tile.ImprovementFunctions.MarkForCreatesOneImprovement(improvement.Name);
        return true;
    }

    public bool CanAddToQueue(IConstruction construction) =>
        !IsQueueFull() &&
        construction.IsBuildable(this) &&
        !(construction is Building && IsBeingConstructedOrEnqueued(construction.Name));

    private bool IsLastConstructionPerpetual() => ConstructionQueue.Count != 0 &&
        PerpetualConstruction.IsNamePerpetual(ConstructionQueue[^1]);
    // `getConstruction(constructionQueue.last()) is PerpetualConstruction` is clear but more expensive

    public bool IsQueueEmptyOrIdle() => CurrentConstructionName().Length == 0
        || CurrentConstructionName() == PerpetualConstruction.Idle.Name;

    /// <summary>
    /// Add <paramref name="construction"/> to the end or top (controlled by <paramref name="addToTop"/>) of the queue with all checks (does nothing if not possible)
    ///
    /// Note: Overload with string parameter <c>constructionName</c> exists as well.
    /// </summary>
    /// <param name="construction">What to add</param>
    /// <param name="addToTop">Add to the top instead of the end</param>
    /// <param name="tile">Supports <see cref="UniqueType.CreatesOneImprovement"/> the tile to place the improvement from that unique on.
    /// Required when adding such a building without an existing marker.</param>
    public void AddToQueue(IConstruction construction, bool addToTop = false, TileClass? tile = null)
    {
        if (!CanAddToQueue(construction)) return;
        MarkTileForCreatesOneImprovement(construction, tile);
        var constructionName = construction.Name;
        if (IsQueueEmptyOrIdle())
            SetCurrentConstruction(constructionName);
        else if (addToTop && construction is PerpetualConstruction && PerpetualConstruction.IsNamePerpetual(CurrentConstructionName()))
            SetCurrentConstruction(constructionName); // perpetual constructions will replace each other
        else if (addToTop)
            EditQueue(queue => queue.Insert(0, constructionName));
        else if (IsLastConstructionPerpetual())
        {
            // Note this also works if  currentConstructionName() is perpetual and the only entry - that var is delegated to the first queue position
            if (construction is PerpetualConstruction)
            {
                // perpetual constructions will replace each other
                EditQueue(queue => queue[^1] = constructionName);
            }
            else
                EditQueue(queue => queue.Insert(queue.Count - 1, constructionName)); // insert new construction before perpetual one
        }
        else
            EditQueue(queue => queue.Add(constructionName));
        CurrentConstructionIsUserSet = true;
    }

    private void MarkTileForCreatesOneImprovement(IConstruction construction, TileClass? tile)
    {
        var improvementToCreate = (construction as Building)?.GetImprovementToCreate(City.GetRuleset(), City.Civ);
        if (improvementToCreate == null) return;
        if (GetTileForImprovement(improvementToCreate.Name) != null) return;

        var tileForImprovement = tile
            ?? throw new ArgumentException($"Cannot queue {construction.Name} without a target tile for {nameof(UniqueType.CreatesOneImprovement)}");
        if (!TryPlaceCreateOneImprovementMarker(improvementToCreate, tileForImprovement))
            throw new ArgumentException($"Cannot queue {construction.Name}: {improvementToCreate.Name} cannot be created on {tileForImprovement.Position}");
    }

    /// <summary>
    /// Add a construction named <paramref name="constructionName"/> to the end of the queue with all checks
    ///
    /// Note: Delegates to overload with <c>construction</c> parameter.
    /// </summary>
    public void AddToQueue(string constructionName, TileClass? tile = null) => AddToQueue(GetConstruction(constructionName), tile: tile);

    /// <summary>Remove one entry from the queue by index.</summary>
    /// <param name="constructionQueueIndex">The index to remove</param>
    /// <param name="automatic">If this was done automatically, we should automatically try to choose a new construction and treat it as such</param>
    public void RemoveFromQueue(int constructionQueueIndex, bool automatic)
    {
        var newQueue = new List<string>(ConstructionQueue);
        var constructionName = newQueue[constructionQueueIndex];
        newQueue.RemoveAt(constructionQueueIndex);
        var queueBecameEmpty = newQueue.Count == 0;
        // To prevent Construction Automation
        if (queueBecameEmpty && !automatic) newQueue.Add(PerpetualConstruction.Idle.Name);
        ConstructionQueue = newQueue;

        // UniqueType.CreatesOneImprovement support
        var construction = GetConstruction(constructionName);
        if (construction is Building building)
        {
            var improvement = building.GetImprovementToCreate(City.GetRuleset(), City.Civ);
            if (improvement != null)
            {
                GetTileForImprovement(improvement.Name)
                    ?.ImprovementFunctions
                    .RemoveCreatesOneImprovementMarker(removeConstruction: false);
            }
        }

        if (queueBecameEmpty)
        {
            if (automatic) ChooseNextConstruction();
            CurrentConstructionIsUserSet = false;
        }
        else CurrentConstructionIsUserSet = true; // we're just continuing the regular queue
    }

    /// <summary>
    /// Remove all queue entries for <paramref name="constructionName"/>.
    ///
    /// Does nothing if there's no entry of that name in the queue.
    /// If the queue is emptied, no automatic: getSettings().autoAssignCityProduction is ignored! (parameter to be added when needed)
    /// </summary>
    public void RemoveAllByName(string constructionName)
    {
        while (!IsQueueEmptyOrIdle())
        {
            var index = IndexOfName(ConstructionQueue, constructionName);
            if (index < 0) return;
            RemoveFromQueue(index, false);
        }
    }

    public void RemoveAll()
    {
        while (!IsQueueEmptyOrIdle())
            // automatic=false so once the queue is empties the "idle" construction is set, ending the loop
            RemoveFromQueue(0, false);
        ChooseNextConstruction();
        CurrentConstructionIsUserSet = false;
    }

    /// <summary>
    /// Moves an entry to the queue top by index.
    /// No-op when index invalid. Must not be called for PerpetualConstruction entries - unchecked!
    /// </summary>
    public void MoveEntryToTop(int constructionQueueIndex)
    {
        if (constructionQueueIndex == 0 || constructionQueueIndex >= ConstructionQueue.Count) return;
        EditQueue(queue =>
        {
            var entry = queue[constructionQueueIndex];
            queue.RemoveAt(constructionQueueIndex);
            queue.Insert(0, entry);
        });
    }

    /// <summary>
    /// Moves an entry by index to the end of the queue, or just before a PerpetualConstruction
    /// (or replacing a PerpetualConstruction if it itself is one and the queue is by happenstance invalid having more than one of those)
    /// </summary>
    public void MoveEntryToEnd(int constructionQueueIndex)
    {
        if (constructionQueueIndex >= ConstructionQueue.Count) return;
        var withoutEntry = new List<string>(ConstructionQueue);
        var constructionName = withoutEntry[constructionQueueIndex];
        withoutEntry.RemoveAt(constructionQueueIndex);
        // Some of the overhead of addToQueue is redundant here, but if the complex "needs to replace or go before a perpetual" logic is needed, then use it anyway
        if (withoutEntry.Count != 0 && PerpetualConstruction.IsNamePerpetual(withoutEntry[^1]))
        {
            ConstructionQueue = withoutEntry;
            AddToQueue(constructionName);
            return;
        }
        withoutEntry.Add(constructionName);
        ConstructionQueue = withoutEntry;
    }

    public int RaisePriority(int constructionQueueIndex)
    {
        if (constructionQueueIndex == 0) return 0; // Already first
        EditQueue(queue => Swap(queue, constructionQueueIndex - 1, constructionQueueIndex));
        return constructionQueueIndex - 1;
    }

    // Lowering == Highering next element in queue
    public int LowerPriority(int constructionQueueIndex)
    {
        if (constructionQueueIndex >= ConstructionQueue.Count - 1) return constructionQueueIndex; // Already last
        RaisePriority(constructionQueueIndex + 1);
        return constructionQueueIndex + 1;
    }

    private static void Swap(List<string> list, int idx1, int idx2) => (list[idx1], list[idx2]) = (list[idx2], list[idx1]);

    /// <summary>
    /// Support for <see cref="UniqueType.CreatesOneImprovement"/>:
    ///
    /// If <paramref name="building"/> is an improvement-creating one, find a marked tile matching the improvement to be created
    /// (skip if none found), then un-mark the tile and place the improvement unless <paramref name="removeOnly"/> is set.
    /// </summary>
    private void ApplyCreateOneImprovement(Building building, bool removeOnly = false)
    {
        var improvement = building.GetImprovementToCreate(City.GetRuleset(), City.Civ);
        if (improvement == null) return;
        var tileForImprovement = GetTileForImprovement(improvement.Name);
        if (tileForImprovement == null) return;
        tileForImprovement.ImprovementFunctions.RemoveCreatesOneImprovementMarker(removeConstruction: false);
        if (removeOnly) return;
        tileForImprovement.SetImprovement(improvement, City.Civ);
        // GUI.setUpdateWorldOnNextRender(): the client redraw request is not part of the engine.
    }

    /// <summary>
    /// Support for <see cref="UniqueType.CreatesOneImprovement"/>:
    ///
    /// To be called after circumstances forced clearing a marker from a tile (pillaging, nuking).
    /// Should remove one matching building from the queue without looking for a marked tile.
    /// </summary>
    public virtual void RemoveCreateOneImprovementConstruction(string improvement)
    {
        var ruleset = City.GetRuleset();
        var indexToRemove = -1;
        for (var index = 0; index < ConstructionQueue.Count; index++)
        {
            var construction = GetConstruction(ConstructionQueue[index]);
            var buildingImprovement = (construction as Building)?.GetImprovementToCreate(ruleset, City.Civ)?.Name;
            if (buildingImprovement == improvement)
            {
                indexToRemove = index;
                break;
            }
        }
        if (indexToRemove < 0) return;

        var newQueue = new List<string>(ConstructionQueue);
        newQueue.RemoveAt(indexToRemove);
        var queueBecameEmpty = newQueue.Count == 0;
        if (queueBecameEmpty) newQueue.Add(PerpetualConstruction.Idle.Name);
        ConstructionQueue = newQueue;

        CurrentConstructionIsUserSet = !queueBecameEmpty;
    }

    /// <summary>
    /// Support for <see cref="UniqueType.CreatesOneImprovement"/>:
    ///
    /// Find the selected tile for a specific improvement being constructed via a building, if any.
    /// </summary>
    public TileClass? GetTileForImprovement(string improvementName) => City.GetTiles()
        .FirstOrDefault(it => it.IsMarkedForCreatesOneImprovement(improvementName));
}
