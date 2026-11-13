using static Unciv.Assets.Pictograms;

namespace Unciv.Assets;

/// <summary>
/// T400: per-object pictograms (original polygons and discs, project MPL-2.0, zero Firaxis art) for every Brave New World base-ruleset object
/// and T300 excluded object that lacks pack art in <c>Packs/unciv</c>. Unknown/mod objects continue to fall back to the category pictogram in <see cref="Pictograms"/>.
/// </summary>
public static class ObjectPictograms
{
    private static readonly Dictionary<(string Category, string Name), Piece[]> ByKey = new()
    {
        // Techs (3)
        [(RulesetAssetKeys.TechIcon, "Penicillin")] = [L(24, 34, 76, 34, 84, 50, 76, 66, 24, 66, 16, 50), D(48, 34, 52, 34, 52, 66, 48, 66), D(30, 46, 42, 46, 42, 54, 30, 54)],
        [(RulesetAssetKeys.TechIcon, "Railroad")] = [L(28, 8, 38, 8, 38, 92, 28, 92), L(62, 8, 72, 8, 72, 92, 62, 92), L(16, 22, 84, 22, 84, 30, 16, 30), L(16, 46, 84, 46, 84, 54, 16, 54), L(16, 70, 84, 70, 84, 78, 16, 78)],
        [(RulesetAssetKeys.TechIcon, "The Internet")] = [Disc(false, 50, 50, 42), Disc(true, 50, 50, 32), L(46, 8, 54, 8, 54, 92, 46, 92), L(8, 46, 92, 46, 92, 54, 8, 54), Disc(false, 50, 50, 12)],

        // Buildings (21 BNW + 3 T300)
        [(RulesetAssetKeys.BuildingIcon, "Airport")] = [L(44, 8, 56, 8, 56, 84, 44, 84), L(8, 44, 92, 30, 92, 44, 56, 56, 44, 56, 8, 44), L(30, 78, 70, 70, 70, 82, 30, 82)],
        [(RulesetAssetKeys.BuildingIcon, "Artists' Guild")] = [Disc(false, 48, 52, 38), Disc(true, 62, 62, 10), Disc(true, 34, 36, 7), Disc(true, 54, 30, 7), Disc(true, 30, 58, 7), L(64, 14, 74, 22, 44, 74, 36, 68)],
        [(RulesetAssetKeys.BuildingIcon, "Borobudur")] = [L(8, 80, 92, 80, 92, 92, 8, 92), L(16, 64, 84, 64, 84, 78, 16, 78), L(24, 48, 76, 48, 76, 62, 24, 62), L(34, 32, 66, 32, 66, 46, 34, 46), L(45, 8, 55, 8, 58, 32, 42, 32)],
        [(RulesetAssetKeys.BuildingIcon, "Broadway")] = [L(12, 20, 88, 20, 88, 50, 12, 50), D(20, 28, 80, 28, 80, 42, 20, 42), L(20, 50, 80, 50, 80, 90, 20, 90), D(34, 58, 66, 58, 66, 90, 34, 90), Disc(false, 22, 14, 5), Disc(false, 50, 14, 5), Disc(false, 78, 14, 5)],
        [(RulesetAssetKeys.BuildingIcon, "Candi")] = [L(14, 76, 86, 76, 86, 92, 14, 92), L(22, 56, 78, 56, 78, 76, 22, 76), L(30, 36, 70, 36, 70, 56, 30, 56), L(38, 18, 62, 18, 62, 36, 38, 36), L(46, 6, 54, 6, 54, 18, 46, 18), D(42, 62, 58, 62, 58, 92, 42, 92)],
        [(RulesetAssetKeys.BuildingIcon, "Caravansary")] = [L(10, 36, 90, 36, 90, 92, 10, 92), Disc(false, 50, 36, 24), D(34, 54, 66, 54, 66, 92, 34, 92), L(10, 22, 24, 22, 24, 36, 10, 36), L(76, 22, 90, 22, 90, 36, 76, 36)],
        [(RulesetAssetKeys.BuildingIcon, "Ducal Stable")] = [L(8, 38, 50, 10, 92, 38, 92, 48, 8, 48), L(14, 48, 86, 48, 86, 92, 14, 92), D(38, 58, 62, 58, 62, 92, 38, 92), Disc(false, 50, 28, 7)],
        [(RulesetAssetKeys.BuildingIcon, "East India Company")] = [L(12, 66, 88, 66, 76, 88, 24, 88), L(47, 10, 53, 10, 53, 66, 47, 66), L(53, 14, 82, 38, 53, 58), L(47, 20, 20, 42, 47, 58)],
        [(RulesetAssetKeys.BuildingIcon, "Globe Theatre")] = [L(28, 14, 72, 14, 90, 36, 90, 72, 72, 90, 28, 90, 10, 72, 10, 36), Disc(true, 50, 52, 22), L(36, 44, 64, 44, 64, 64, 36, 64)],
        [(RulesetAssetKeys.BuildingIcon, "Hotel")] = [L(24, 10, 76, 10, 76, 92, 24, 92), D(32, 20, 44, 20, 44, 32, 32, 32), D(56, 20, 68, 20, 68, 32, 56, 32), D(32, 40, 44, 40, 44, 52, 32, 52), D(56, 40, 68, 40, 68, 52, 56, 52), D(42, 68, 58, 68, 58, 92, 42, 92)],
        [(RulesetAssetKeys.BuildingIcon, "Ikanda")] = [Disc(false, 50, 50, 42), Disc(true, 50, 50, 28), Disc(false, 50, 50, 16), L(44, 82, 56, 82, 56, 94, 44, 94)],
        [(RulesetAssetKeys.BuildingIcon, "International Space Station")] = [L(10, 44, 90, 44, 90, 56, 10, 56), L(14, 14, 30, 14, 30, 86, 14, 86), L(70, 14, 86, 14, 86, 86, 70, 86), L(42, 28, 58, 28, 58, 72, 42, 72)],
        [(RulesetAssetKeys.BuildingIcon, "Musicians' Guild")] = [Disc(false, 30, 74, 14), Disc(false, 70, 66, 14), L(38, 20, 46, 20, 46, 74, 38, 74), L(78, 12, 86, 12, 86, 66, 78, 66), L(38, 20, 86, 12, 86, 24, 38, 32)],
        [(RulesetAssetKeys.BuildingIcon, "National Visitor Center")] = [L(10, 40, 90, 40, 90, 92, 10, 92), L(50, 10, 94, 40, 6, 40), D(38, 54, 62, 54, 62, 92, 38, 92), Disc(false, 50, 28, 6)],
        [(RulesetAssetKeys.BuildingIcon, "Parthenon")] = [L(50, 8, 94, 32, 6, 32), L(12, 36, 24, 36, 24, 82, 12, 82), L(34, 36, 46, 36, 46, 82, 34, 82), L(54, 36, 66, 36, 66, 82, 54, 82), L(76, 36, 88, 36, 88, 82, 76, 82), L(6, 82, 94, 82, 94, 92, 6, 92)],
        [(RulesetAssetKeys.BuildingIcon, "Prora")] = [L(6, 42, 94, 42, 94, 86, 6, 86), D(14, 52, 26, 52, 26, 70, 14, 70), D(34, 52, 46, 52, 46, 70, 34, 70), D(54, 52, 66, 52, 66, 70, 54, 70), D(74, 52, 86, 52, 86, 70, 74, 70)],
        [(RulesetAssetKeys.BuildingIcon, "Red Fort")] = [L(10, 34, 30, 34, 30, 92, 10, 92), L(70, 34, 90, 34, 90, 92, 70, 92), L(28, 46, 72, 46, 72, 92, 28, 92), L(12, 20, 28, 20, 28, 34, 12, 34), L(72, 20, 88, 20, 88, 34, 72, 34), D(42, 64, 58, 64, 58, 92, 42, 92)],
        [(RulesetAssetKeys.BuildingIcon, "Royal Library")] = [L(10, 36, 48, 44, 48, 90, 10, 82), L(90, 36, 52, 44, 52, 90, 90, 82), L(28, 26, 36, 10, 50, 20, 64, 10, 72, 26)],
        [(RulesetAssetKeys.BuildingIcon, "Uffizi")] = [L(10, 24, 30, 24, 30, 88, 10, 88), L(70, 24, 90, 24, 90, 88, 70, 88), L(30, 68, 70, 68, 70, 88, 30, 88), D(42, 74, 58, 74, 58, 88, 42, 88)],
        [(RulesetAssetKeys.BuildingIcon, "Writers' Guild")] = [L(24, 86, 36, 62, 78, 10, 86, 18, 46, 68), L(22, 66, 46, 66, 46, 90, 22, 90)],
        [(RulesetAssetKeys.BuildingIcon, "Zoo")] = [L(12, 40, 24, 40, 24, 90, 12, 90), L(76, 40, 88, 40, 88, 90, 76, 90), L(12, 24, 88, 24, 88, 40, 12, 40), Disc(false, 50, 68, 14), Disc(false, 36, 50, 6), Disc(false, 50, 46, 6), Disc(false, 64, 50, 6)],
        [(RulesetAssetKeys.BuildingIcon, "Bomb Shelter")] = [Disc(false, 50, 62, 30), D(10, 66, 90, 66, 90, 94, 10, 94), L(10, 66, 90, 66, 90, 78, 10, 78), Disc(true, 50, 50, 8), L(44, 34, 56, 34, 50, 46)],
        [(RulesetAssetKeys.BuildingIcon, "Heroic Epic")] = [L(46, 10, 54, 10, 54, 80, 46, 80), L(34, 70, 66, 70, 66, 76, 34, 76), Disc(false, 22, 54, 10), Disc(false, 78, 54, 10), Disc(true, 22, 54, 5), Disc(true, 78, 54, 5)],
        [(RulesetAssetKeys.BuildingIcon, "Satrap's Court")] = [Disc(false, 50, 28, 12), L(14, 24, 38, 28, 14, 34), L(86, 24, 62, 28, 86, 34), L(18, 76, 82, 76, 82, 90, 18, 90), L(28, 60, 72, 60, 72, 76, 28, 76), L(38, 46, 62, 46, 62, 60, 38, 60)],

        // Units (18 BNW + 3 T300)
        [(RulesetAssetKeys.UnitIcon, "Archaeologist")] = [L(18, 36, 82, 36, 82, 44, 18, 44), Disc(false, 50, 36, 22), L(24, 56, 76, 56, 68, 74, 32, 74), L(46, 44, 54, 44, 54, 92, 46, 92)],
        [(RulesetAssetKeys.UnitIcon, "Berber Cavalry")] = [Disc(false, 48, 36, 24), Disc(true, 56, 36, 18), L(18, 88, 82, 12, 88, 18, 24, 92)],
        [(RulesetAssetKeys.UnitIcon, "Caravan")] = [L(16, 46, 32, 24, 52, 42, 68, 24, 84, 46, 84, 70, 16, 70), L(20, 70, 28, 70, 28, 92, 20, 92), L(72, 70, 80, 70, 80, 92, 72, 92)],
        [(RulesetAssetKeys.UnitIcon, "Cargo Ship")] = [L(10, 64, 90, 64, 78, 88, 22, 88), L(22, 42, 46, 42, 46, 64, 22, 64), L(50, 42, 74, 42, 74, 64, 50, 64), L(64, 22, 74, 22, 74, 42, 64, 42)],
        [(RulesetAssetKeys.UnitIcon, "Comanche Riders")] = [L(16, 82, 84, 18, 90, 24, 22, 88), L(68, 12, 88, 12, 78, 32), L(20, 40, 38, 24, 34, 48), L(12, 52, 30, 36, 26, 60)],
        [(RulesetAssetKeys.UnitIcon, "Great Galleass")] = [L(8, 62, 92, 62, 80, 82, 20, 82), L(28, 82, 34, 82, 30, 94, 24, 94), L(48, 82, 54, 82, 50, 94, 44, 94), L(68, 82, 74, 82, 70, 94, 64, 94), L(46, 12, 52, 12, 52, 62, 46, 62), L(52, 16, 86, 42, 52, 56)],
        [(RulesetAssetKeys.UnitIcon, "Great Musician")] = [L(24, 22, 34, 22, 44, 76, 56, 76, 66, 22, 76, 22, 62, 88, 38, 88), L(42, 24, 46, 24, 46, 76, 42, 76), L(54, 24, 58, 24, 58, 76, 54, 76)],
        [(RulesetAssetKeys.UnitIcon, "Great Writer")] = [L(16, 18, 56, 18, 56, 88, 16, 88), D(24, 34, 48, 34, 48, 40, 24, 40), D(24, 52, 48, 52, 48, 58, 24, 58), L(54, 84, 84, 14, 90, 20, 62, 88)],
        [(RulesetAssetKeys.UnitIcon, "Immortal")] = [Disc(false, 42, 52, 30), Disc(true, 42, 52, 14), L(74, 8, 82, 8, 82, 92, 74, 92), L(78, 6, 88, 18, 68, 18)],
        [(RulesetAssetKeys.UnitIcon, "Persian Immortal")] = [Disc(false, 42, 52, 30), Disc(true, 42, 52, 14), L(74, 8, 82, 8, 82, 92, 74, 92), L(78, 6, 88, 18, 68, 18)],
        [(RulesetAssetKeys.UnitIcon, "Impi")] = [L(38, 14, 56, 36, 56, 64, 38, 86, 20, 64, 20, 36), D(36, 34, 40, 34, 40, 66, 36, 66), L(70, 12, 78, 12, 78, 90, 70, 90)],
        [(RulesetAssetKeys.UnitIcon, "Kris Swordsman")] = [L(50, 6, 58, 22, 46, 38, 58, 54, 46, 68, 42, 54, 50, 38, 42, 22), L(30, 68, 70, 68, 70, 76, 30, 76), L(45, 76, 55, 76, 55, 92, 45, 92)],
        [(RulesetAssetKeys.UnitIcon, "Merchant of Venice")] = [L(16, 30, 84, 30, 84, 38, 16, 38), L(46, 16, 54, 16, 54, 88, 46, 88), Disc(false, 24, 62, 12), Disc(false, 76, 62, 12)],
        [(RulesetAssetKeys.UnitIcon, "Nau")] = [L(12, 66, 88, 66, 74, 88, 26, 88), L(46, 12, 54, 12, 54, 66, 46, 66), L(24, 22, 76, 22, 76, 56, 24, 56), D(46, 26, 54, 26, 54, 52, 46, 52), D(30, 35, 70, 35, 70, 43, 30, 43)],
        [(RulesetAssetKeys.UnitIcon, "Pathfinder")] = [L(50, 6, 62, 38, 94, 50, 62, 62, 50, 94, 38, 62, 6, 50, 38, 38), Disc(true, 50, 50, 10)],
        [(RulesetAssetKeys.UnitIcon, "Pracinha")] = [Disc(false, 50, 54, 32), D(14, 58, 86, 58, 86, 92, 14, 92), L(14, 52, 86, 52, 86, 60, 14, 60), L(46, 28, 54, 28, 54, 48, 46, 48), L(40, 35, 60, 35, 60, 42, 40, 42)],
        [(RulesetAssetKeys.UnitIcon, "Siege Tower")] = [L(32, 14, 68, 14, 76, 80, 24, 80), L(12, 24, 34, 14, 34, 28, 16, 36), Disc(false, 32, 84, 10), Disc(false, 68, 84, 10), D(42, 34, 58, 34, 58, 54, 42, 54)],
        [(RulesetAssetKeys.UnitIcon, "Winged Hussar")] = [L(22, 10, 48, 22, 42, 72, 24, 72), L(16, 24, 44, 32, 42, 42, 14, 34), L(12, 44, 42, 50, 40, 60, 10, 54), L(64, 10, 72, 10, 72, 92, 64, 92)],
        [(RulesetAssetKeys.UnitIcon, "Work Boat")] = [L(14, 60, 86, 60, 74, 86, 26, 86), L(46, 18, 54, 18, 54, 60, 46, 60), L(24, 24, 72, 42, 24, 48)],
        [(RulesetAssetKeys.UnitIcon, "Anti-Aircraft Gun")] = [L(24, 76, 76, 76, 86, 90, 14, 90), L(34, 66, 76, 18, 84, 24, 42, 72), L(22, 56, 64, 10, 72, 16, 30, 62)],
        [(RulesetAssetKeys.UnitIcon, "Nuclear Submarine")] = [L(10, 56, 90, 56, 84, 76, 16, 76), L(38, 34, 60, 34, 60, 56, 38, 56), L(46, 16, 52, 16, 52, 34, 46, 34), L(52, 16, 62, 16, 62, 22, 52, 22)],

        // Resources (5 BNW + 2 T300)
        [(RulesetAssetKeys.ResourceIcon, "Antiquity Site")] = [L(24, 78, 76, 78, 76, 92, 24, 92), L(34, 34, 66, 22, 66, 78, 34, 78), D(44, 36, 50, 36, 50, 74, 44, 74)],
        [(RulesetAssetKeys.ResourceIcon, "Hidden Antiquity Site")] = [L(20, 78, 62, 78, 62, 90, 20, 90), L(28, 38, 54, 28, 54, 78, 28, 78), Disc(false, 68, 42, 20), Disc(true, 68, 42, 12), L(78, 56, 92, 74, 84, 80, 70, 62)],
        [(RulesetAssetKeys.ResourceIcon, "Cloves")] = [Disc(false, 36, 30, 12), Disc(false, 64, 30, 12), Disc(false, 50, 20, 12), L(32, 38, 54, 88, 46, 88, 26, 40), L(68, 38, 54, 88, 46, 88, 62, 40)],
        [(RulesetAssetKeys.ResourceIcon, "Nutmeg")] = [Disc(false, 50, 52, 30), D(30, 34, 70, 48, 66, 56, 26, 42), D(28, 62, 72, 50, 74, 58, 30, 70), L(46, 14, 54, 14, 54, 28, 46, 28)],
        [(RulesetAssetKeys.ResourceIcon, "Pepper")] = [Disc(false, 34, 56, 14), Disc(false, 62, 52, 14), Disc(false, 48, 74, 14), L(44, 16, 66, 28, 50, 42, 34, 28)],
        [(RulesetAssetKeys.ResourceIcon, "Bison")] = [L(16, 36, 46, 22, 82, 42, 82, 74, 68, 74, 68, 88, 58, 88, 58, 70, 34, 70, 34, 88, 24, 88, 24, 66, 16, 58), L(10, 24, 24, 32, 16, 42)],
        [(RulesetAssetKeys.ResourceIcon, "Cocoa")] = [L(50, 12, 72, 36, 72, 66, 50, 90, 28, 66, 28, 36), D(47, 18, 53, 18, 53, 84, 47, 84), D(34, 36, 40, 36, 40, 68, 34, 68), D(60, 36, 66, 36, 66, 68, 60, 68)],

        // Improvements (5 BNW + 4 T300)
        [(RulesetAssetKeys.ImprovementIcon, "Archaeological Dig")] = [L(16, 76, 84, 76, 92, 90, 8, 90), L(36, 14, 44, 14, 44, 76, 36, 76), L(28, 52, 52, 52, 52, 76, 28, 76), L(60, 18, 70, 18, 64, 76, 58, 76)],
        [(RulesetAssetKeys.ImprovementIcon, "Brazilwood Camp")] = [Disc(false, 28, 72, 14), Disc(false, 58, 72, 14), Disc(false, 43, 48, 14), L(74, 14, 84, 14, 84, 86, 74, 86), L(54, 14, 84, 14, 84, 32, 54, 28)],
        [(RulesetAssetKeys.ImprovementIcon, "Chateau")] = [L(14, 44, 86, 44, 86, 92, 14, 92), L(12, 44, 24, 14, 36, 44), L(64, 44, 76, 14, 88, 44), L(36, 28, 64, 28, 64, 44, 36, 44), D(44, 64, 56, 64, 56, 92, 44, 92)],
        [(RulesetAssetKeys.ImprovementIcon, "Feitoria")] = [L(14, 44, 86, 44, 86, 90, 14, 90), L(46, 12, 54, 12, 54, 44, 46, 44), L(54, 14, 86, 26, 54, 36), D(42, 62, 58, 62, 58, 90, 42, 90)],
        [(RulesetAssetKeys.ImprovementIcon, "Kasbah")] = [L(16, 46, 84, 46, 84, 92, 16, 92), L(16, 30, 30, 30, 30, 46, 16, 46), L(43, 30, 57, 30, 57, 46, 43, 46), L(70, 30, 84, 30, 84, 46, 70, 46), D(40, 60, 60, 60, 60, 92, 40, 92)],
        [(RulesetAssetKeys.ImprovementIcon, "Remove Forest")] = [L(50, 12, 80, 66, 20, 66), L(44, 66, 56, 66, 56, 88, 44, 88), D(12, 16, 22, 10, 88, 84, 78, 90), L(14, 14, 20, 10, 86, 86, 80, 90)],
        [(RulesetAssetKeys.ImprovementIcon, "Remove Jungle")] = [L(46, 40, 54, 40, 54, 90, 46, 90), L(50, 40, 14, 26, 36, 14), L(50, 40, 86, 26, 64, 14), L(14, 14, 20, 10, 86, 86, 80, 90)],
        [(RulesetAssetKeys.ImprovementIcon, "Remove Railroad")] = [L(32, 14, 40, 14, 40, 86, 32, 86), L(60, 14, 68, 14, 68, 86, 60, 86), L(22, 34, 78, 34, 78, 42, 22, 42), L(22, 58, 78, 58, 78, 66, 22, 66), L(14, 14, 20, 10, 86, 86, 80, 90)],
        [(RulesetAssetKeys.ImprovementIcon, "Remove Road")] = [L(24, 88, 40, 12, 60, 12, 76, 88), D(48, 24, 52, 24, 52, 42, 48, 42), D(48, 56, 52, 56, 52, 74, 48, 74), L(14, 14, 20, 10, 86, 86, 80, 90)],

        // Policies (5 BNW)
        [(RulesetAssetKeys.PolicyIcon, "Exploration")] = [Disc(false, 50, 50, 40), Disc(true, 50, 50, 28), L(50, 16, 60, 50, 50, 84, 40, 50)],
        [(RulesetAssetKeys.PolicyIcon, "Exploration Complete")] = [Disc(false, 50, 50, 42), Disc(true, 50, 50, 30), L(50, 14, 62, 50, 50, 86, 38, 50), L(14, 50, 50, 38, 86, 50, 50, 62)],
        [(RulesetAssetKeys.PolicyIcon, "Maritime Infrastructure")] = [Disc(false, 50, 20, 10), L(45, 28, 55, 28, 55, 84, 45, 84), L(28, 40, 72, 40, 72, 48, 28, 48), L(16, 64, 34, 86, 66, 86, 84, 64, 74, 60, 60, 76, 40, 76, 26, 60)],
        [(RulesetAssetKeys.PolicyIcon, "Navigation School")] = [L(50, 16, 88, 78, 12, 78), Disc(true, 50, 56, 18), Disc(false, 50, 22, 8)],
        [(RulesetAssetKeys.PolicyIcon, "Treasure Fleets")] = [L(14, 44, 86, 44, 86, 86, 14, 86), L(18, 20, 82, 20, 88, 40, 12, 40), D(44, 38, 56, 38, 56, 58, 44, 58), Disc(false, 34, 36, 8), Disc(false, 66, 36, 8)],

        // Promotions (13 BNW + 9 T300)
        [(RulesetAssetKeys.PromotionIcon, "Ambition")] = [L(50, 8, 76, 46, 60, 46, 60, 92, 40, 92, 40, 46, 24, 46)],
        [(RulesetAssetKeys.PromotionIcon, "Enemy Blade")] = [L(50, 6, 62, 26, 50, 44, 62, 62, 42, 74, 42, 26), L(30, 72, 70, 72, 70, 80, 30, 80), L(45, 80, 55, 80, 55, 94, 45, 94)],
        [(RulesetAssetKeys.PromotionIcon, "Evil Spirits")] = [Disc(false, 50, 52, 32), L(22, 12, 38, 32, 24, 38), L(78, 12, 62, 32, 76, 38), Disc(true, 38, 48, 6), Disc(true, 62, 48, 6), D(36, 66, 64, 66, 60, 74, 40, 74)],
        [(RulesetAssetKeys.PromotionIcon, "Heavy Charge")] = [L(10, 36, 54, 36, 54, 16, 92, 50, 54, 84, 54, 64, 10, 64), D(24, 45, 58, 45, 58, 55, 24, 55)],
        [(RulesetAssetKeys.PromotionIcon, "Heroism")] = [L(50, 8, 62, 38, 92, 38, 68, 58, 76, 90, 50, 70, 24, 90, 32, 58, 8, 38, 38, 38)],
        [(RulesetAssetKeys.PromotionIcon, "Invulnerability")] = [L(18, 12, 82, 12, 82, 52, 50, 92, 18, 52), Disc(true, 50, 44, 18), Disc(false, 50, 44, 10)],
        [(RulesetAssetKeys.PromotionIcon, "Lightning Warfare")] = [L(58, 6, 26, 52, 48, 52, 38, 94, 76, 44, 54, 44)],
        [(RulesetAssetKeys.PromotionIcon, "Mystic Blade")] = [L(50, 6, 60, 22, 60, 66, 40, 66, 40, 22), L(26, 66, 74, 66, 74, 74, 26, 74), L(50, 76, 60, 85, 50, 94, 40, 85)],
        [(RulesetAssetKeys.PromotionIcon, "Recruitment")] = [Disc(false, 30, 34, 10), L(16, 48, 44, 48, 44, 86, 16, 86), Disc(false, 68, 34, 10), L(54, 48, 82, 48, 82, 86, 54, 86), L(45, 16, 55, 16, 55, 36, 45, 36), L(40, 22, 60, 22, 60, 30, 40, 30)],
        [(RulesetAssetKeys.PromotionIcon, "Restlessness")] = [L(10, 22, 46, 22, 46, 10, 84, 34, 46, 58, 46, 46, 10, 46), L(18, 58, 54, 58, 54, 46, 92, 70, 54, 94, 54, 82, 18, 82)],
        [(RulesetAssetKeys.PromotionIcon, "Sapper")] = [L(20, 24, 80, 24, 68, 38, 32, 38), L(46, 38, 54, 38, 54, 92, 46, 92), L(14, 60, 28, 70, 14, 82), L(86, 60, 72, 70, 86, 82)],
        [(RulesetAssetKeys.PromotionIcon, "Sneak Attack")] = [L(62, 10, 84, 16, 44, 72, 34, 62), L(22, 66, 46, 88, 38, 94, 16, 74), L(12, 82, 24, 92, 14, 94)],
        [(RulesetAssetKeys.PromotionIcon, "[Immortal] ability")] = [L(22, 14, 78, 14, 78, 54, 50, 90, 22, 54), D(45, 28, 55, 28, 55, 68, 45, 68), D(32, 43, 68, 43, 68, 53, 32, 53)],
        [(RulesetAssetKeys.PromotionIcon, "[Persian Immortal] ability")] = [L(22, 14, 78, 14, 78, 54, 50, 90, 22, 54), D(45, 28, 55, 28, 55, 68, 45, 68), D(32, 43, 68, 43, 68, 53, 32, 53)],
        [(RulesetAssetKeys.PromotionIcon, "Armor Plating I")] = [L(16, 18, 84, 18, 84, 82, 16, 82), D(24, 26, 32, 26, 32, 34, 24, 34), D(68, 26, 76, 26, 76, 34, 68, 34), D(45, 36, 55, 36, 55, 68, 45, 68)],
        [(RulesetAssetKeys.PromotionIcon, "Armor Plating II")] = [L(16, 18, 84, 18, 84, 82, 16, 82), D(24, 26, 32, 26, 32, 34, 24, 34), D(68, 26, 76, 26, 76, 34, 68, 34), D(36, 36, 46, 36, 46, 68, 36, 68), D(54, 36, 64, 36, 64, 68, 54, 68)],
        [(RulesetAssetKeys.PromotionIcon, "Armor Plating III")] = [L(16, 18, 84, 18, 84, 82, 16, 82), D(28, 36, 38, 36, 38, 68, 28, 68), D(45, 36, 55, 36, 55, 68, 45, 68), D(62, 36, 72, 36, 72, 68, 62, 68)],
        [(RulesetAssetKeys.PromotionIcon, "Flight Deck I")] = [L(14, 14, 86, 14, 74, 88, 26, 88), D(46, 24, 54, 24, 54, 76, 46, 76), L(36, 46, 64, 46, 50, 26)],
        [(RulesetAssetKeys.PromotionIcon, "Flight Deck II")] = [L(14, 14, 86, 14, 74, 88, 26, 88), D(38, 24, 46, 24, 46, 76, 38, 76), D(54, 24, 62, 24, 62, 76, 54, 76)],
        [(RulesetAssetKeys.PromotionIcon, "Flight Deck III")] = [L(14, 14, 86, 14, 74, 88, 26, 88), D(30, 24, 38, 24, 38, 76, 30, 76), D(46, 24, 54, 24, 54, 76, 46, 76), D(62, 24, 70, 24, 70, 76, 62, 76)],
        [(RulesetAssetKeys.PromotionIcon, "Devout")] = [L(44, 10, 56, 10, 56, 90, 44, 90), L(20, 32, 80, 32, 80, 44, 20, 44)],
        [(RulesetAssetKeys.PromotionIcon, "Unable to pillage tiles")] = [L(42, 42, 58, 42, 54, 90, 46, 90), L(50, 10, 68, 32, 50, 44, 32, 32), L(14, 14, 22, 10, 86, 86, 78, 90)],
    };

    public static bool Has(AssetKey key) => ByKey.ContainsKey((key.Category, key.Name));

    public static IEnumerable<AssetKey> AllKeys => ByKey.Keys.Select(k => AssetKey.Of(k.Category, k.Name));

    public static void Draw(byte[] rgba, int size, AssetKey key, int box, int centreX, int centreY)
    {
        if (PiecesFor(key) is { } pieces)
            DrawPieces(rgba, size, pieces, box, centreX, centreY);
    }

    internal static Piece[]? PiecesFor(AssetKey key) => ByKey.GetValueOrDefault((key.Category, key.Name));
}
