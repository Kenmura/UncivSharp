using System.Text.Json.Serialization;
using Unciv.Json;
using Unciv.Logic.Map;
using Unciv.Mathematics;
using Unciv.Utils;

namespace Unciv.Logic.Civilization;

// core/src/com/unciv/logic/civilization/ExploredRegion.kt
// Stripped: the purity annotations. TileGroupMap.groupSize (a UI constant) is inlined as GroupSize.
// Kotlin Rectangle is mutable; the engine Rectangle is an immutable record struct, so getRectangle() returns the current value
// and calculateStageCoords replaces it.

public class ExploredRegion : IIsPartOfGameInfoSerialization
{
    /// <summary><c>TileGroupMap.groupSize</c>.</summary>
    private const float GroupSize = 50f;

    private bool worldWrap;
    private bool evenMapWidth;
    private bool rectangularMap;
    private float mapRadius;
    private readonly float tileRadius = GroupSize * 0.8f;
    private bool shouldRecalculateCoords = true;
    private bool shouldUpdateMinimap = true;

    // Rectangle for positioning the camera viewport on the minimap
    private Rectangle exploredRectangle;
    private bool shouldRestrictX;

    // Top left point of the explored region in stage (x;y) starting from the top left corner
    private Vector2 topLeftStage = new();
    // Bottom right point of the explored region in stage (x;y) starting from the top left corner
    private Vector2 bottomRightStage = new();

    // Top left point of the explored region in hex (long;lat) from the center of the map
    [JsonPropertyName("topLeft")]
    [JsonInclude]
    private Vector2 topLeft = new();
    // Bottom right point of the explored region in hex (long;lat) from the center of the map
    [JsonPropertyName("bottomRight")]
    [JsonInclude]
    private Vector2 bottomRight = new();

    // Getters
    public bool ShouldRecalculateCoords() => shouldRecalculateCoords;
    public bool ShouldUpdateMinimap() => shouldUpdateMinimap;
    public Rectangle GetRectangle() => exploredRectangle;
    public bool ShouldRestrictX() => shouldRestrictX;
    public float GetLeftX() => topLeftStage.X;
    public float GetRightX() => bottomRightStage.X;
    public float GetTopY() => topLeftStage.Y;
    public float GetBottomY() => bottomRightStage.Y;

    public ExploredRegion Clone()
    {
        // T219: Kotlin copies the references, so both regions share the same vectors (and checkTilePosition mutates them in place);
        // the clone gets its own.
        var toReturn = new ExploredRegion
        {
            topLeft = topLeft.Cpy(),
            bottomRight = bottomRight.Cpy(),
        };
        return toReturn;
    }

    public void SetMapParameters(MapParameters mapParameters)
    {
        worldWrap = mapParameters.WorldWrap;
        evenMapWidth = worldWrap;

        if (mapParameters.Shape == MapShape.Rectangular)
        {
            mapRadius = mapParameters.MapSize.Width / 2; // Int division, then toFloat
            evenMapWidth = mapParameters.MapSize.Width % 2 == 0 || evenMapWidth;
            rectangularMap = true;
        }
        else
            mapRadius = mapParameters.MapSize.Radius;
    }

    public bool IsEmpty() => topLeft.Equals(Vector2.Zero) && bottomRight.Equals(Vector2.Zero);

    /// <summary>Check if tilePosition is beyond explored region</summary>
    public void CheckTilePosition(HexCoord tilePosition, HexCoord? explorerPosition)
    {
        bool mapExplored = false;
        float longitude = HexMath.GetLongitude(tilePosition);
        float latitude = HexMath.GetLatitude(tilePosition);

        // First time call
        if (IsEmpty())
        {
            topLeft = new Vector2(longitude, latitude);
            bottomRight = new Vector2(longitude, latitude);
            return;
        }

        // Check X coord
        if (topLeft.X >= bottomRight.X)
        {
            if (longitude > topLeft.X)
            {
                // For world wrap maps when the maximumX is reached, we move to a minimumX - 1f
                if (worldWrap && longitude == mapRadius) longitude = mapRadius * -1f;
                topLeft.X = longitude;
                mapExplored = true;
            }
            else if (longitude < bottomRight.X)
            {
                // For world wrap maps when the minimumX is reached, we move to a maximumX + 1f
                if (worldWrap && longitude == mapRadius * -1f + 1f) longitude = mapRadius + 1f;
                bottomRight.X = longitude;
                mapExplored = true;
            }
        }
        else
        {
            // When we cross the map edge with world wrap, the vectors are swapped along the x-axis
            if (longitude < bottomRight.X && longitude > topLeft.X)
            {
                float rightSideDistance;
                float leftSideDistance;

                // If we have explorerPosition, get distance to explorer
                // This solves situations when a newly explored cell is in the middle of an unexplored area
                if (explorerPosition != null)
                {
                    // Kotlin Int, promoted to Float by the arithmetic below
                    int explorerLongitude = HexMath.GetLongitude(explorerPosition.Value);

                    rightSideDistance = explorerLongitude < 0 && bottomRight.X > 0
                        // The explorer is still on the right edge of the map, but has explored over the edge
                        ? mapRadius * 2f + explorerLongitude - bottomRight.X
                        : Math.Abs(explorerLongitude - bottomRight.X);

                    leftSideDistance = explorerLongitude > 0 && topLeft.X < 0
                        // The explorer is still on the left edge of the map, but has explored over the edge
                        ? mapRadius * 2f - explorerLongitude + topLeft.X
                        : Math.Abs(topLeft.X - explorerLongitude);
                }
                else
                {
                    // If we don't have explorerPosition, we calculate the distance to the edges of the explored region
                    // e.g. when capitals are revealed
                    rightSideDistance = bottomRight.X - longitude;
                    leftSideDistance = longitude - topLeft.X;
                }

                // Expand region from the nearest edge
                if (rightSideDistance > leftSideDistance)
                    topLeft.X = longitude;
                else
                    bottomRight.X = longitude;

                mapExplored = true;
            }
        }

        // Check Y coord
        if (latitude > topLeft.Y)
        {
            topLeft.Y = latitude;
            mapExplored = true;
        }
        else if (latitude < bottomRight.Y)
        {
            bottomRight.Y = latitude;
            mapExplored = true;
        }

        if (mapExplored)
        {
            shouldRecalculateCoords = true;
            shouldUpdateMinimap = true;
        }
    }

    public void CalculateStageCoords(float mapMaxX, float mapMaxY)
    {
        shouldRecalculateCoords = false;

        // Check if we explored the whole world wrap map horizontally
        shouldRestrictX = bottomRight.X - topLeft.X != 1f;

        // Get world (x;y)
        var topLeftWorld = HexMath.WorldFromLatLong(topLeft, tileRadius);
        var bottomRightWorld = HexMath.WorldFromLatLong(bottomRight, tileRadius);

        // Convert X to the stage coords
        float mapCenterX = evenMapWidth ? (mapMaxX + GroupSize + 4f) * 0.5f : mapMaxX * 0.5f;
        float left = mapCenterX + topLeftWorld.X;
        float right = mapCenterX + bottomRightWorld.X;

        // World wrap over edge check
        if (left > mapMaxX) left = 10f;
        if (right < 0f) right = mapMaxX - 10f;

        // Convert Y to the stage coords
        float mapCenterY = rectangularMap ? mapMaxY * 0.5f + GroupSize * 0.25f : mapMaxY * 0.5f;
        float top = mapCenterY - topLeftWorld.Y;
        float bottom = mapCenterY - bottomRightWorld.Y;

        topLeftStage = new Vector2(left, top);
        bottomRightStage = new Vector2(right, bottom);

        // Calculate rectangle for positioning the camera viewport on the minimap
        float yOffset = tileRadius * (float)Math.Sqrt(3f) * 0.5f;
        exploredRectangle = new Rectangle(
            left - tileRadius,
            mapMaxY - bottom - yOffset * 0.5f,
            GetWidth() * tileRadius * 1.5f,
            GetHeight() * yOffset);
    }

    public bool IsPositionInRegion(HexCoord position)
    {
        int lon = HexMath.GetLongitude(position);
        int lat = HexMath.GetLatitude(position);
        return topLeft.X > bottomRight.X
            ? lon <= topLeft.X && lon >= bottomRight.X && lat <= topLeft.Y && lat >= bottomRight.Y
            : ((lon >= topLeft.X && lon >= bottomRight.X) || (lon <= topLeft.X && lon <= bottomRight.X)) && lat <= topLeft.Y && lat >= bottomRight.Y;
    }

    public int GetWidth()
    {
        float result;
        if (topLeft.X > bottomRight.X) result = topLeft.X - bottomRight.X;
        else result = mapRadius * 2f - (bottomRight.X - topLeft.X);
        return KotlinMath.FloatToInt(result) + 1;
    }

    public int GetHeight() => KotlinMath.FloatToInt(topLeft.Y - bottomRight.Y) + 1;

    public float GetMinimapLeft(float tileSize)
    {
        shouldUpdateMinimap = false;
        return (topLeft.X + 1f) * tileSize * -0.75f;
    }
}
