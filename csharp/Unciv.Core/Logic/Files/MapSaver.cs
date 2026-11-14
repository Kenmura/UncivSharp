using Unciv.Json;
using Unciv.Logic.Map;
using Unciv.Utils;

namespace Unciv.Logic.Files;

// core/src/com/unciv/logic/files/MapSaver.kt
//
// Deviations:
// - UncivGame.Current.files.getLocalFile becomes the explicit `localFile` resolver argument (a function from a path relative to the
//   local data folder to its handle) of SaveMap and GetMaps.
// - TileMap and TileMap.Preview JSON is wired by their own converters (T107).
//   The file/zip/trim/fallback logic here is complete.

public static class MapSaver
{
    public const string MapsFolder = "maps";

    public static readonly SaveOptions DefaultSaveOptions = new(Zipped: true);

    public static TileMap MapFromSavedString(string mapString)
    {
        string unzippedJson;
        try
        {
            unzippedJson = FileConversions.Unzip(mapString.Trim());
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException)
        {
            unzippedJson = mapString;
        }
        return MapFromJson(unzippedJson);
    }

    public static string MapToSavedString(TileMap tileMap, SaveOptions? options = null)
    {
        tileMap.AssignContinents(TileMap.AssignContinentsMode.Reassign);
        var mapJson = UncivJson.Serialize(tileMap);
        return (options ?? DefaultSaveOptions).Zipped ? FileConversions.Zip(mapJson) : mapJson;
    }

    public static void SaveMap(string mapName, TileMap tileMap, Func<string, IUncivFileHandle> localFile, SaveOptions? options = null)
    {
        tileMap.AssignContinents(TileMap.AssignContinentsMode.Reassign);
        FileConversions.WriteJson(localFile($"{MapsFolder}/{mapName}"), tileMap, (options ?? DefaultSaveOptions).Zipped);
    }

    public static TileMap LoadMap(IUncivFileHandle mapFile) => FileConversions.ReadJson<TileMap>(mapFile).NotNull();

    public static IReadOnlyList<IUncivFileHandle> GetMaps(Func<string, IUncivFileHandle> localFile) => localFile(MapsFolder).List();

    private static TileMap MapFromJson(string json) => UncivJson.Deserialize<TileMap>(LenientJson.QuoteBareKeys(json)).NotNull();

    public static MapParameters LoadMapParameters(IUncivFileHandle mapFile) => LoadMapPreview(mapFile).MapParameters;

    public static TileMap.Preview LoadMapPreview(IUncivFileHandle mapFile) => FileConversions.ReadJson<TileMap.Preview>(mapFile).NotNull();
}
