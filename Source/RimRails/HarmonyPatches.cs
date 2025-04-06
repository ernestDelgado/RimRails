using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimRails
{
    [StaticConstructorOnStartup]
    public static class HarmonyPatches
    {
        static HarmonyPatches()
        {
            var harmony = new Harmony("rimrails.mod");
            harmony.PatchAll();
        }
    }

    // ✅ Scale Up Path Cost
    public static class RimRailsSettings
    {
        public static int scaleUp = 30; // 🔹 Default path cost multiplier (adjustable)
    }

    // ✅ Modify Path Cost for Terrain & Floors
    [HarmonyPatch(typeof(PathGrid), "CalculatedCostAt")]
    public static class Patch_CalculatedCostAt
    {
        private static readonly FieldInfo MapField = typeof(PathGrid).GetField("map", BindingFlags.Instance | BindingFlags.NonPublic);

        public static void Postfix(ref int __result, PathGrid __instance, IntVec3 c, bool perceivedStatic, IntVec3 prevCell)
        {
            if (MapField == null)
            {
                Log.Warning("RimRails: Could not access PathGrid map field.");
                return;
            }

            Map map = MapField.GetValue(__instance) as Map;
            if (map == null)
            {
                Log.Warning("RimRails: Map is null in PathGrid. Skipping path cost modification.");
                return;
            }

            bool hasTrainTrack = map.thingGrid.ThingsListAt(c).Any(thing => thing.def.defName == "TrainTracks");
            bool hasBlockingWall = map.thingGrid.ThingsListAt(c).Any(thing => thing.def.passability == Traversability.Impassable);

            // ✅ If there's a train track and it's **not blocked**, set path cost to 0
            if (hasTrainTrack && !hasBlockingWall)
            {
                __result = 0;
                return;
            }

            // ✅ If there's a wall **on the tracks**, increase path cost so pawns avoid it
            if (hasTrainTrack && hasBlockingWall)
            {
                __result = PathGrid.ImpassableCost; // ✅ Make walls on tracks completely impassable
                return;
            }

            // ✅ Get the terrain & floor at the current cell
            TerrainDef terrain = map.terrainGrid.TerrainAt(c);
            bool hasFloor = terrain?.layerable == true;

            // ✅ Scale path cost for all **natural terrain**
            if (terrain != null && !hasFloor)
            {
                __result = Math.Min(__result * RimRailsSettings.scaleUp, 10000);
            }

            // ✅ Apply a **temporary path cost increase to floors**
            if (hasFloor)
            {
                __result += RimRailsSettings.scaleUp; // Adds temporary cost so floors aren't as good as tracks
            }
        }
    }


    // ✅ Adjust Pawn Movement Speed Based on Path Cost Scaling
    [HarmonyPatch(typeof(Pawn_PathFollower), "CostToMoveIntoCell", new Type[] { typeof(IntVec3) })]
    public static class Patch_PawnMovement
    {
        private static readonly FieldInfo PawnField = AccessTools.Field(typeof(Pawn_PathFollower), "pawn");

        public static void Postfix(Pawn_PathFollower __instance, IntVec3 c, ref float __result)
        {
            Pawn pawn = PawnField.GetValue(__instance) as Pawn;
            if (pawn == null || pawn.Map == null) return;

            Map map = pawn.Map;
            bool hasTrainTrack = map.thingGrid.ThingsListAt(c).Any(thing => thing.def.defName == "TrainTracks");

            // ✅ Check if the pawn is a player-controlled colonist OR a tamed animal
            bool isColonist = pawn.Faction != null && pawn.Faction.IsPlayer;
            bool isTamedAnimal = pawn.RaceProps.Animal && pawn.Faction != null && pawn.Faction.IsPlayer;

            // ✅ If the pawn is a colonist or tamed animal, apply train track speed boost
            if (hasTrainTrack && (isColonist || isTamedAnimal))
            {
                __result *= 0.2f; // ✅ 5x Speed Boost for Colonists & Their Tamed Animals

                // ✅ Remove grass and small bushes when the pawn moves over train tracks
                List<Thing> thingsInCell = map.thingGrid.ThingsListAt(c);
                for (int i = thingsInCell.Count - 1; i >= 0; i--) // Iterate in reverse to avoid modification issues
                {
                    Thing thing = thingsInCell[i];
                    if (thing is Plant plant && !plant.def.plant.IsTree) // ✅ Exclude trees
                    {
                        thing.Destroy(DestroyMode.Vanish); // ✅ Grass/Bushes disappear when walked over
                    }
                }
                return;
            }

            // ✅ If it's not a colonist/tamed animal, keep normal speed (NO BOOST for Raiders)
            if (hasTrainTrack && !isColonist && !isTamedAnimal)
            {
                return;
            }

            // ✅ Apply **terrain & floor** movement speed scaling
            TerrainDef terrain = map.terrainGrid.TerrainAt(c);
            bool hasFloor = terrain != null && terrain.layerable; // Floors are layerable

            int pathCost = 1;
            try
            {
                if (map?.pathing?.Normal?.pathGrid != null)
                {
                    int calculated = map.pathing.Normal.pathGrid.CalculatedCostAt(c, false, c); // Use `c` instead of `IntVec3.Invalid`
                    if (calculated > 0) pathCost = calculated;
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"RimRails: Failed to get path cost at {c} - {ex.Message}");
            }

            float movementBoost = 2f / Mathf.Sqrt(pathCost); // ✅ Keeps movement balanced

            if (terrain != null)
            {
                __result *= movementBoost;
            }
        }
    }


    [HarmonyPatch(typeof(Pawn_PathFollower), "TrySetNewPath")]
    public static class Patch_AbandonIfStuck
    {
        public static void Postfix(Pawn_PathFollower __instance)
        {
            Pawn pawn = Traverse.Create(__instance).Field("pawn").GetValue<Pawn>();
            if (pawn == null || pawn.Dead || pawn.Map == null || pawn.jobs == null) return;

            // ✅ If a pawn fails to find a valid path, abandon the job
            if (__instance.curPath == null || !__instance.curPath.Found)
            {
                pawn.jobs.EndCurrentJob(JobCondition.Incompletable);
                Log.Message($"RimRails: {pawn.Name} was stuck and abandoned the task.");
            }
        }
    }




    // ✅ Render Minecart on Pawns using train tracks
    [HarmonyPatch(typeof(PawnRenderer), "RenderPawnAt")]
    public static class Patch_RenderMinecart
    {
        public static void Postfix(PawnRenderer __instance, Vector3 drawLoc, Pawn ___pawn)
        {
            if (___pawn == null || ___pawn.Map == null) return;

            // Check if the pawn is standing on train tracks
            bool isOnTrack = ___pawn.Position.GetThingList(___pawn.Map).Any(thing => thing.def.defName == "TrainTracks");

            if (isOnTrack)
            {
                // Load the minecart material
                Material minecartMaterial = MaterialPool.MatFrom("Minecart", ShaderDatabase.Transparent);

                // Adjust position and scale
                Vector3 cartPosition = drawLoc + new Vector3(0, -1f, 0);
                float scale = 1.25f;

                // Create a scaled matrix for rendering
                Matrix4x4 matrix = Matrix4x4.TRS(cartPosition, Quaternion.identity, new Vector3(scale, 1, scale));

                // Render the minecart
                Graphics.DrawMesh(MeshPool.plane10, matrix, minecartMaterial, 2);
            }
        }
    }



    // ✅ Adjust train track path cost when placed
    [HarmonyPatch(typeof(Thing), "SpawnSetup")]
    public static class Patch_TrainTrackPlacement
    {
        private static readonly MethodInfo NotifyThingSpawnedMethod =
            typeof(RegionDirtyer).GetMethod("Notify_ThingAffectingRegionsSpawned", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly MethodInfo NotifyWalkabilityChangedMethod =
            typeof(RegionDirtyer).GetMethod("Notify_WalkabilityChanged", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly HashSet<IntVec3> pendingTrackCells = new HashSet<IntVec3>();
        private static int lastUpdateTick = 0;

        public static void Postfix(Thing __instance, Map map, bool respawningAfterLoad)
        {
            IntVec3 cell = __instance.Position;

            // Case 1: Track placed
            if (__instance.def.defName == "TrainTracks")
            {
                // Invalidate region
                NotifyThingSpawnedMethod?.Invoke(map.regionDirtyer, new object[] { __instance });

                // Check if there's a wall on this cell – if so, let wall win
                var edifice = cell.GetEdifice(map);
                bool walkable = edifice == null || edifice.def.passability != Traversability.Impassable;

                NotifyWalkabilityChangedMethod?.Invoke(map.regionDirtyer, new object[] { cell, walkable });

                // Optional: Reset path cost manually
                map.pathing.Normal.pathGrid.pathGrid[map.cellIndices.CellToIndex(cell)] = 0;

                pendingTrackCells.Add(cell);
            }

            // Case 2: Wall (or any impassable structure) placed
            else if (__instance.def.passability == Traversability.Impassable)
            {
                // Invalidate region
                NotifyThingSpawnedMethod?.Invoke(map.regionDirtyer, new object[] { __instance });

                // Check if there's a track already under this wall
                Thing track = map.thingGrid.ThingAt(cell, ThingDef.Named("TrainTracks"));
                if (track != null)
                {
                    // Force the wall’s impassable nature to win
                    NotifyWalkabilityChangedMethod?.Invoke(map.regionDirtyer, new object[] { cell, false });

                    // Optional: Reset path cost
                    map.pathing.Normal.pathGrid.pathGrid[map.cellIndices.CellToIndex(cell)] = 0;

                    pendingTrackCells.Add(cell);
                }
            }

            // Recalculate pathfinding once every few ticks
            int currentTick = Find.TickManager.TicksGame;
            if (currentTick - lastUpdateTick > 15)
            {
                RecalculatePathfinding(map);
                lastUpdateTick = currentTick;
            }
        }

        private static void RecalculatePathfinding(Map map)
        {
            if (pendingTrackCells.Count == 0) return;

            map.pathing.RecalculateAllPerceivedPathCosts();
            pendingTrackCells.Clear();
        }
    }







    [HarmonyPatch(typeof(Pawn), "TickRare")]
    public static class Patch_PawnTickRare
    {
        public static void Postfix(Pawn __instance)
        {
            if (__instance == null || __instance.Dead || __instance.Map == null)
                return;

            // Only apply boost if the pawn is moving
            if (__instance.pather == null || !__instance.pather.Moving)
                return;

            // Check if the pawn is on train tracks
            bool isOnTrack = __instance.Position.GetThingList(__instance.Map)
                .Any(thing => thing.def.defName == "TrainTracks");

            if (isOnTrack)
            {
                __instance.pather.nextCellCostTotal *= 0.2f; // 5x Speed Boost
            }
        }
    }



    public class PlaceWorker_AllowOnTrack : PlaceWorker
    {
        public AcceptanceReport AllowsPlacing(BuildableDef checkingDef, Map map, IntVec3 loc, Rot4 rot, Thing thingToIgnore = null)
        {
            // ✅ If there is a train track in the cell, allow placement of walls, fences, or doors.
            foreach (Thing thing in loc.GetThingList(map))
            {
                if (thing.def.defName == "TrainTracks")
                {
                    return true; // ✅ Allow placement over tracks.
                }
            }

            // ✅ If the structure is a wall, fence, or door, allow placement over tracks.
            if (checkingDef is ThingDef def)
            {
                if (def.building != null && (def.building.isPlaceOverableWall || def.building.isFence || def.building.isEdifice))
                {
                    return true;
                }
            }

            return false; // ❌ Otherwise, deny placement.
        }
    }
    public class Building_TrainTrack : Building
    {
        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);

            // ✅ No need to set path cost here since Harmony patch handles it
        }

        public override void Destroy(DestroyMode mode = DestroyMode.Vanish)
        {
            base.Destroy(mode);

            // ✅ Keep this, so pathfinding recalculates when tracks are destroyed
            if (Map != null)
            {
                Map.pathing.RecalculateAllPerceivedPathCosts();
            }
        }
    }




}
