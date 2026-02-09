using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using UnityEngine;

namespace Luc1dShadow.Vulture
{
    /// <summary>
    /// Listens for combat sounds (gunshots, explosions) that trigger Vulture behavior.
    /// </summary>
    public class CombatSoundListener
    {
        public static List<CombatEvent> RecentEvents = new List<CombatEvent>();
        private const float MAX_AGE = 300f; // Keep events for 5 mins to support long ambushes/greed
        private static ManualLogSource _log;

        public void Enable()
        {
            _log = Plugin.Log;
            Harmony.CreateAndPatchAll(typeof(GunshotListenerPatch));
            _log.LogInfo("CombatSoundListener: Patches enabled.");
            
            // Subscribe to grenade explosions via game event
            SubscribeToExplosions();
        }

        private void SubscribeToExplosions()
        {
            try
            {
                // The BotEventHandler is a Singleton that emits grenade events
                // We'll try to subscribe when GameWorld is ready
                if (Singleton<BotEventHandler>.Instantiated)
                {
                    var handler = Singleton<BotEventHandler>.Instance;
                    handler.OnGrenadeExplosive += OnGrenadeExplosion;
                    _log.LogInfo("CombatSoundListener: Subscribed to grenade explosions.");
                }
                else
                {
                    // Will need to subscribe later when GameWorld loads
                    _log.LogInfo("CombatSoundListener: BotEventHandler not ready, will subscribe on first raid.");
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning($"CombatSoundListener: Could not subscribe to explosions: {ex.Message}");
            }
        }

        public static void TrySubscribeToExplosions()
        {
            try
            {
                if (Singleton<BotEventHandler>.Instantiated)
                {
                    var handler = Singleton<BotEventHandler>.Instance;
                    handler.OnGrenadeExplosive -= OnGrenadeExplosion; // Prevent double subscribe
                    handler.OnGrenadeExplosive += OnGrenadeExplosion;
                    if (Plugin.DebugLogging.Value)
                        Plugin.Log.LogInfo("CombatSoundListener: Subscribed to grenade explosions (raid start).");
                }
            }
            catch { }
        }

        private static void OnGrenadeExplosion(
            Vector3 explosionPosition,
            string playerProfileID,
            bool isSmoke,
            float smokeRadius,
            float smokeLifeTime,
            int throwableId)
        {
            // Ignore smoke grenades for vulture (they're not deadly combat sounds)
            if (isSmoke) return;

            float now = Time.time;
            CleanupOldEvents();

            RecentEvents.Add(new CombatEvent
            {
                Position = explosionPosition,
                Time = now,
                Power = 150f, // Explosions are louder than gunshots
                IsExplosion = true
            });

            if (Plugin.DebugLogging.Value)
                Plugin.Log.LogInfo($"[CombatSoundListener] Explosion detected at {explosionPosition}");
        }

        public struct CombatEvent
        {
            public Vector3 Position;
            public float Time;
            public float Power;
            public bool IsExplosion;
            public bool IsBoss;
            public string ShooterProfileId;
        }

        /// <summary>
        /// Checks if a WildSpawnType is a boss or boss-tier enemy.
        /// </summary>
        private static bool IsBossType(WildSpawnType role)
        {
            return role == WildSpawnType.bossKilla
                || role == WildSpawnType.bossBully
                || role == WildSpawnType.bossGluhar
                || role == WildSpawnType.bossKojaniy
                || role == WildSpawnType.bossTagilla
                || role == WildSpawnType.bossSanitar
                || role == WildSpawnType.bossKnight
                || role == WildSpawnType.followerBigPipe
                || role == WildSpawnType.followerBirdEye
                || role == WildSpawnType.sectantPriest
                || role == WildSpawnType.sectantWarrior
                || role == WildSpawnType.bossZryachiy
                || role == WildSpawnType.followerZryachiy
                || role == WildSpawnType.bossKolontay
                || role == WildSpawnType.followerKolontayAssault
                || role == WildSpawnType.followerKolontaySecurity
                || role == WildSpawnType.bossBoar
                || role == WildSpawnType.followerBoar
                || role == WildSpawnType.bossBoarSniper
                || role == WildSpawnType.bossPartisan;
        }

        /// <summary>
        /// Checks if a role is a marksman/sniper scav that should be ignored.
        /// These are static snipers that don't indicate meaningful combat.
        /// </summary>
        public static bool IsMarksmanType(WildSpawnType role)
        {
            return role == WildSpawnType.marksman
                || role == WildSpawnType.shooterBTR;
        }

        /// <summary>
        /// Checks if a position is within a recent boss activity zone.
        /// </summary>
        public static bool IsInBossZone(Vector3 position, float radius)
        {
            float now = Time.time;
            float radiusSq = radius * radius;
            float decayTime = Plugin.BossZoneDecay?.Value ?? 120f;

            for (int i = 0; i < RecentEvents.Count; i++)
            {
                var evt = RecentEvents[i];
                if (!evt.IsBoss) continue;
                if (now - evt.Time > decayTime) continue;

                float distSq = (evt.Position - position).sqrMagnitude;
                if (distSq < radiusSq)
                {
                    return true;
                }
            }

            return false;
        }

        [HarmonyPatch(typeof(Player), "OnMakingShot")]
        public static class GunshotListenerPatch
        {
            [HarmonyPostfix]
            public static void Postfix(Player __instance, IWeapon weapon)
            {
                bool isSilenced = false;
                
                if (__instance.HandsController is Player.FirearmController controller)
                {
                     isSilenced = controller.IsSilenced;
                }

                if (Plugin.DebugLogging.Value)
                {
                    Plugin.Log.LogInfo($"[CombatSoundListener] Shot fired by {__instance.Profile.Nickname} at {__instance.Position}. Silenced: {isSilenced}");
                }

                if (isSilenced)
                {
                    if (Plugin.DebugLogging.Value) Plugin.Log.LogInfo($"[CombatSoundListener] Shot ignored (Silenced)");
                    return; 
                } 

                float now = Time.time;
                CleanupOldEvents();

                // Check if shooter is a boss or marksman
                var role = __instance.Profile.Info.Settings.Role;
                bool isBoss = IsBossType(role);
                bool isMarksman = IsMarksmanType(role);

                // Ignore marksman/sniper scav shots - they're static snipers, not meaningful combat
                if (isMarksman)
                {
                    if (Plugin.DebugLogging.Value)
                        Plugin.Log.LogInfo($"[CombatSoundListener] Shot ignored (Marksman/Sniper Scav): {__instance.Profile.Nickname}");
                    return;
                }

                RecentEvents.Add(new CombatEvent
                {
                    Position = __instance.Position,
                    Time = now,
                    Power = 100f,
                    IsExplosion = false,
                    IsBoss = isBoss,
                    ShooterProfileId = __instance.ProfileId
                });

                if (isBoss && Plugin.DebugLogging.Value)
                    Plugin.Log.LogInfo($"[CombatSoundListener] BOSS detected: {__instance.Profile.Nickname} ({role}) at {__instance.Position}");

                // Also try subscribing here in case we missed the raid start
                TrySubscribeToExplosions();
            }
        }

        private static void CleanupOldEvents()
        {
            float now = Time.time;
            RecentEvents.RemoveAll(x => now - x.Time > MAX_AGE);
        }

        /// <summary>
        /// Gets the nearest combat event (shot or explosion) within range.
        /// </summary>
        public static CombatEvent? GetNearestEvent(Vector3 botPos, float maxDist)
        {
            float now = Time.time;
            CombatEvent? nearest = null;
            float bestDistSq = maxDist * maxDist;

            for (int i = 0; i < RecentEvents.Count; i++)
            {
                var evt = RecentEvents[i];
                if (now - evt.Time > MAX_AGE) continue;

                float distSq = (evt.Position - botPos).sqrMagnitude;
                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    nearest = evt;
                }
            }

            return nearest;
        }

        /// <summary>
        /// Counts the number of combat events in an area within a time window.
        /// Used for Multi-Shot Intensity calculation.
        /// </summary>
        public static int GetEventIntensity(Vector3 position, float radius, float timeWindow)
        {
            float now = Time.time;
            float radiusSq = radius * radius;
            int count = 0;

            for (int i = 0; i < RecentEvents.Count; i++)
            {
                var evt = RecentEvents[i];
                float age = now - evt.Time;
                if (age > timeWindow) continue;

                float distSq = (evt.Position - position).sqrMagnitude;
                if (distSq < radiusSq)
                {
                    count++;
                    // Explosions count as multiple "shots" worth of intensity
                    if (evt.IsExplosion) count += 2;
                }
            }

            return count;
        }
    }
}
