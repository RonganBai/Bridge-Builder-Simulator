using System;
using System.Linq;
using System.Text.Json;
using BridgeArchitect3D.Data;

/// <summary>The shared level editor (designer + in-game settings): checks, terrain edits, fixed nodes, file formats.</summary>
internal static class LevelEditTests
{
    public static void Run(GameDatabase db, Action<string, bool, string> check)
    {
        // Every built-in level can be opened in the editor and saved again unchanged
        foreach (var lv in db.Levels)
        {
            var errs = LevelEditing.Validate(LevelEditing.Copy(lv), db);
            check($"Level editor: built-in {lv.Id} passes the shared checks", errs.Count == 0, string.Join("; ", errs));
        }

        // A designer default level passes both the old spec checks and the shared ones
        var spec = new CustomLevelSpec { Name = "edit test", RiverWidth = 20, LeftBankHeight = 0, RightBankHeight = 2, Ship = true };
        spec.Anchors.Add(new CustomAnchorSpec { X = 10, Y = -2 });
        var l = CustomLevelBuilder.Build(spec, db);
        check("Level editor: designer level passes the shared checks", LevelEditing.Validate(l, db).Count == 0, string.Join("; ", LevelEditing.Validate(l, db)));

        // River width: right bank anchors shift, river nodes / pillars / ship lane scale
        var w = LevelEditing.Copy(l);
        var r1 = w.Anchors.First(a => a.IsRoadEnd); var c1 = w.Anchors.First(a => a.Id == "C1");
        LevelEditing.SetRiverWidth(w, 30);
        check("Level editor: wider river moves the right road anchor with the bank", Math.Abs(r1.Position.X - 30) < 1e-9 && w.Terrain.RiverWidth == 30, $"R1 x={r1.Position.X}");
        check("Level editor: river nodes and their pillars stay in the same place relative to the river",
            Math.Abs(c1.Position.X - 15) < 1e-9 && w.Terrain.Pillars.Any(p => Math.Abs(p.X - 15) < 1e-9), $"C1 x={c1.Position.X}");
        check("Level editor: ship lane follows the river", w.TrafficEvents.Where(e => e.Type == TrafficEventType.Ship).All(e => Math.Abs(e.LaneX - 15) < 1e-9), "");

        // Bank height: the road start moves with the left bank
        var h = LevelEditing.Copy(l);
        LevelEditing.SetBankHeight(h, true, 3);
        var l1 = h.Anchors.First(a => a.IsRoadStart);
        check("Level editor: raising a bank raises its road anchor", Math.Abs(l1.Position.Y - 3) < 1e-9 && h.Terrain.LeftBankHeight == 3, $"L1 y={l1.Position.Y}");

        // Fixed nodes: river nodes get a pillar, wall nodes do not, removing deletes the pillar, road anchors stay
        var f = LevelEditing.Copy(l); int pillars = f.Terrain.Pillars.Count;
        var mid = LevelEditing.AddAnchor(f, new Vec3d(5, -3, 0));
        check("Level editor: a node in the river stands on a new rock pillar", f.Terrain.Pillars.Count == pillars + 1 && mid.Id.StartsWith("C"), $"id={mid.Id}");
        var wall = LevelEditing.BankPoint(f, true, true, 2);
        LevelEditing.MoveAnchor(f, mid, wall);
        check("Level editor: moving a node onto the bank wall removes its pillar", f.Terrain.Pillars.Count == pillars, "");
        LevelEditing.MoveAnchor(f, mid, new Vec3d(6, -2, 0));
        LevelEditing.RemoveAnchor(f, mid);
        check("Level editor: removing a river node removes its pillar", f.Terrain.Pillars.Count == pillars && f.Anchors.All(a => a != mid), "");
        check("Level editor: road anchors cannot be removed", !LevelEditing.RemoveAnchor(f, f.Anchors.First(a => a.IsRoadStart)), "");
        var buried = LevelEditing.Copy(l); buried.Terrain.BankSlopeDeg = 45;
        LevelEditing.AddAnchor(buried, new Vec3d(0.5, -4, 0));
        check("Level editor: a node buried in a sloped bank is reported", LevelEditing.Validate(buried, db).Any(e => e.Contains("土体")), string.Join("; ", LevelEditing.Validate(buried, db)));

        // No-pier zone, soil, water
        var z = LevelEditing.Copy(l);
        LevelEditing.SetNoPiers(z, true);
        check("Level editor: no-pier zone covers the river", LevelEditing.NoPiers(z) && LevelEditing.Validate(z, db).Any(e => e.Contains("禁止建墩")), "");
        LevelEditing.SetNoPiers(z, false);
        check("Level editor: no-pier zone can be removed again", !LevelEditing.NoPiers(z), "");
        LevelEditing.SetSoil(z, "soft");
        check("Level editor: river-bed soil applies to the whole river", z.Terrain.DefaultSoil == "soft" && z.Terrain.FoundationZones.All(q => q.Soil == "soft"), "");
        LevelEditing.SetHasWater(z, false);
        check("Level editor: a dry gully drops its ships", z.TrafficEvents.All(e => e.Type != TrafficEventType.Ship) && z.Terrain.WaterLevel == z.Terrain.RiverBedDepth, "");
        var noCar = LevelEditing.Copy(l); noCar.TrafficEvents.RemoveAll(e => e.Type == TrafficEventType.Vehicle);
        check("Level editor: a level without vehicles is reported", LevelEditing.Validate(noCar, db).Any(e => e.Contains("车辆")), "");

        // Traffic helpers
        var t = LevelEditing.Copy(l);
        var train = LevelEditing.AddVehicle(t, db.Vehicles["train"]);
        check("Level editor: adding a train makes a train event after the last event", train.Type == TrafficEventType.Train && train.Time > 0, $"t={train.Time}");
        var ship = t.TrafficEvents.First(e => e.Type == TrafficEventType.Ship);
        LevelEditing.SetShipKind(ship, "sailboat", t.Terrain.RiverWidth);
        check("Level editor: changing a ship's type takes that type's height", ship.ShipKind == "sailboat" && Math.Abs(ship.Height - ShipCatalog.Types.First(s => s.Id == "sailboat").Height) < 1e-9, "");

        // Depth controls: river depth keeps the water depth, water depth keeps freeboard
        var d = LevelEditing.Copy(l);
        LevelEditing.SetWaterDepth(d, 2);
        LevelEditing.SetRiverDepth(d, 12);
        check("Level editor: river depth moves the bed and keeps the water depth",
            Math.Abs(LevelEditing.RiverDepth(d) - 12) < 1e-9 && Math.Abs(LevelEditing.WaterDepth(d) - 2) < 1e-9, $"rd={LevelEditing.RiverDepth(d)} wd={LevelEditing.WaterDepth(d)}");
        LevelEditing.SetWaterDepth(d, 50);
        check("Level editor: water stays below the bank top", LevelEditing.WaterDepth(d) <= 11.5 + 1e-9, $"wd={LevelEditing.WaterDepth(d)}");
        LevelEditing.SetBuildDepth(d, 6);
        check("Level editor: build depth is measured below the river bed", Math.Abs(d.Terrain.BuildMinY - (d.Terrain.RiverBedDepth - 6)) < 1e-9 && LevelEditing.Validate(d, db).Count == 0, string.Join("; ", LevelEditing.Validate(d, db)));

        // Ships: any spot on the water, draft 10%~50% of the ship height
        var sh = LevelEditing.Copy(l); var boat = sh.TrafficEvents.First(e => e.Type == TrafficEventType.Ship);
        LevelEditing.SetShipDraft(boat, 100);
        check("Level editor: ship draft is capped at 50% of its height", Math.Abs(boat.Draft.Value - boat.Height * 0.5) < 1e-9, $"d={boat.Draft}");
        LevelEditing.SetShipDraft(boat, 0);
        check("Level editor: ship draft is at least 10% of its height", Math.Abs(boat.Draft.Value - boat.Height * 0.1) < 1e-9, $"d={boat.Draft}");
        LevelEditing.SetShipLane(sh, boat, boat.Width / 2 + 1);
        check("Level editor: a ship can sail off the river centre", Math.Abs(boat.LaneX - (boat.Width / 2 + 1)) < 1e-9 && LevelEditing.Validate(sh, db).Count == 0, string.Join("; ", LevelEditing.Validate(sh, db)));
        LevelEditing.SetShipLane(sh, boat, -10);
        check("Level editor: a ship stays clear of the bank wall", Math.Abs(boat.LaneX - boat.Width / 2 - LevelEditing.ShipWallGap) < 1e-9, $"x={boat.LaneX}");
        sh.Terrain.BankSlopeDeg = 45; LevelEditing.SetShipLane(sh, boat, 0);
        check("Level editor: on a sloped bank the hull keeps off the slope", boat.LaneX > boat.Width / 2 + 0.5 && LevelEditing.Validate(sh, db).Count == 0, $"x={boat.LaneX}");
        boat.LaneX = 0.1;
        check("Level editor: a ship grounded on the bank is reported", LevelEditing.Validate(sh, db).Any(e => e.Contains("岸壁")), "");
        boat.Draft = boat.Height * 0.9;
        check("Level editor: a draft above 50% is reported", LevelEditing.Validate(sh, db).Any(e => e.Contains("吃水")), "");

        // Files: old designer specs and full definitions both load; save-as gives a custom id
        string oldJson = JsonSerializer.Serialize(spec, GameDatabase.JsonOptions);
        var fromOld = LevelEditing.ReadCustomLevel(oldJson, db);
        check("Level editor: old designer files load as the same level", fromOld != null && fromOld.Anchors.Count == l.Anchors.Count && fromOld.Terrain.RiverWidth == 20, "");
        var round = LevelEditing.ReadCustomLevel(GameDatabase.SerializeLevel(w), db);
        check("Level editor: edited levels round-trip through the custom level file", round.Terrain.RiverWidth == 30 && round.Anchors.Count == w.Anchors.Count, "");
        var asCustom = LevelEditing.AsCustom(db.Levels[0]);
        check("Level editor: a built-in level saved as custom gets its own custom id", CustomLevelBuilder.IsCustom(asCustom.Id) && db.Levels[0].Id == "level1", asCustom.Id);
    }
}
