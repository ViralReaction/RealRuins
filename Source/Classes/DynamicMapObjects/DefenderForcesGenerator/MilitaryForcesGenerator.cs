﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using RimWorld.BaseGen;
using Verse;
using Verse.AI.Group;

namespace RealRuins {
    class MilitaryForcesGenerator : AbstractDefenderForcesGenerator {

        float militaryPower = 1;
        int minTriggerTimeout = 0;

        public MilitaryForcesGenerator(float militaryPower, int minimalTriggerFiringTimeout = 0) {
            if (militaryPower > 1) {
                this.militaryPower = militaryPower;
                minTriggerTimeout = minimalTriggerFiringTimeout;
            }
        }

        public override void GenerateForces(Map map, ResolveParams rp, ScatterOptions options) {
            if (options == null) return;

            int addedTriggers = 0;
            float ratio = 10;
            float remainingCost = options.uncoveredCost * (Rand.Value + 0.5f); //cost estimation as seen by other factions
            Debug.Log(Debug.ForceGen, "Running military force generation with remaining cost of {0} (while uncovered is {1})", remainingCost, options.uncoveredCost);

            float initialCost = remainingCost;

            int triggersAbsoluteMaximum = 100;

            while (remainingCost > 0) {

                IntVec3 mapLocation = rp.rect.RandomCell;
                if (!mapLocation.InBounds(map)) continue;

                ThingDef raidTriggerDef = ThingDef.Named("RaidTrigger");
                RaidTrigger trigger = ThingMaker.MakeThing(raidTriggerDef) as RaidTrigger;

                trigger.faction = rp.faction;

                int raidMaxPoints = (int)(remainingCost / ratio);
                float raidValue = Math.Abs(Rand.Gaussian()) * raidMaxPoints + Rand.Value * raidMaxPoints + 250.0f;
                if (raidValue > 10000) raidValue = Rand.Range(8000, 11000); //sanity cap. against some beta-poly bases.
                remainingCost -= raidValue * ratio;

                int timeout = (int)Math.Abs(Rand.Gaussian(0, 75));
                trigger.value = ScalePointsToDifficulty(raidValue);
                trigger.SetTimeouts(timeout, 200);

                GenSpawn.Spawn(trigger, mapLocation, map);
                Debug.Log(Debug.ForceGen, "Spawned trigger at {0}, {1} for {2} points, autofiring after {3} rare ticks", mapLocation.x, mapLocation.z, trigger.value, timeout);
                addedTriggers++;

                options.uncoveredCost = Math.Abs(remainingCost);

                if (addedTriggers > triggersAbsoluteMaximum) {
                    if (remainingCost < initialCost * 0.2f) {
                        if (Rand.Chance(0.1f)) {
                            if (remainingCost > 100000) {
                                remainingCost = Rand.Range(80000, 110000);
                            }
                            return;
                        }
                    }
                }
            }
        }

        public override void GenerateStartingParty(Map map, ResolveParams rp, ScatterOptions currentOptions) {
            float uncoveredCost = currentOptions.uncoveredCost;

            int points = (int)(uncoveredCost / (10 * militaryPower));
            int initialGroup = 0;
            if (points > 10000) {
                initialGroup = Rand.Range(5000, 10000);
            } else {
                initialGroup = points;
            }
            Debug.Log(Debug.ForceGen, "Military gen: uncoveredCost {0}, military power: {1}, total points allowed: {2}", uncoveredCost, militaryPower, points);

            points -= initialGroup;
            SpawnGroup((int)ScalePointsToDifficulty(initialGroup), rp.rect, rp.faction, map);
            Debug.Log(Debug.ForceGen, "Initial group of {0} spawned, {1} points left for triggers", initialGroup, points);

            while (points > 0) {
                IntVec3 mapLocation = rp.rect.RandomCell;
                if (!mapLocation.InBounds(map)) continue;

                ThingDef raidTriggerDef = ThingDef.Named("RaidTrigger");
                RaidTrigger trigger = ThingMaker.MakeThing(raidTriggerDef) as RaidTrigger;

                trigger.faction = rp.faction;
                trigger.SetTimeouts(0, 300);

                int raidMaxPoints = (int)(10000 / Math.Max(Math.Sqrt(d: militaryPower), 1.0));
                float raidValue = Math.Abs(Rand.Gaussian()) * raidMaxPoints + Rand.Value * raidMaxPoints + 250.0f;
                if (raidValue > 10000) raidValue = Rand.Range(8000, 11000); //sanity cap. against some beta-poly bases.
                points -= (int)raidValue;

                trigger.value = ScalePointsToDifficulty(points);

                GenSpawn.Spawn(trigger, mapLocation, map);
                Debug.Log(Debug.ForceGen, "Spawned trigger at {0}, {1} for {2} points, autofiring after {3} rare ticks", mapLocation.x, mapLocation.z, trigger.value, 0);
            }
        }

        private void SpawnGroup(int points, CellRect locationRect, Faction faction, Map map) {
            PawnGroupMakerParms pawnGroupMakerParms = new PawnGroupMakerParms();
            pawnGroupMakerParms.groupKind = PawnGroupKindDefOf.Combat;
            pawnGroupMakerParms.tile = map.Tile;
            pawnGroupMakerParms.points = points;
            pawnGroupMakerParms.faction = faction;
            pawnGroupMakerParms.generateFightersOnly = false;
            pawnGroupMakerParms.seed = Rand.Int;

            List<Pawn> pawns = PawnGroupMakerUtility.GeneratePawns(pawnGroupMakerParms).ToList();
            CellRect rect = locationRect;

            if (pawns == null) {
                Debug.Warning("Pawns list is null");
            } else {
                Debug.Log("Pawns list contains {0} records", pawns.Count);
            }

            foreach (Pawn p in pawns) {

                bool result = CellFinder.TryFindRandomCellInsideWith(locationRect, (IntVec3 x) => x.Standable(map), out IntVec3 location);


                if (result) {
                    GenSpawn.Spawn(p, location, map, Rot4.Random);
                } else {
                    Debug.Warning("Can't find location!");
                }
            }

            LordJob lordJob = null;
            lordJob = new LordJob_DefendBase(faction, rect.CenterCell);

            if (lordJob != null) {
                LordMaker.MakeNewLord(faction, lordJob, map, pawns);
            }
        }
    }
}
