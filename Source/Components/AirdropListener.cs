using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using Comfort.Common;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace Luc1dShadow.Vulture
{
    /// <summary>
    /// Monitors for airdrop crates and tracks their positions for Vulture behavior.
    /// Uses Harmony patch on AirdropLogicClass.method_3 (called when airdrop lands).
    /// </summary>
    public class AirdropListener : MonoBehaviour
    {
        public static List<AirdropEvent> ActiveAirdrops = new List<AirdropEvent>();
        
        private static ManualLogSource _log;
        private static HashSet<int> _trackedInstanceIds = new HashSet<int>();
        private static bool _patchApplied = false;

        public struct AirdropEvent
        {
            public Vector3 Position;
            public float LandedTime;
            public int InstanceId;
            public bool IsActive;
        }

        private void Awake()
        {
            _log = Plugin.Log;
            if (Plugin.DebugLogging.Value) _log.LogInfo("[AirdropListener] Component initialized.");
            
            // Apply the Harmony patch for airdrop landing detection
            TryApplyAirdropPatch();
        }

        private void Update()
        {
            // Only process if feature is enabled
            if (!Plugin.EnableAirdropVulturing.Value) return;
            if (!Singleton<GameWorld>.Instantiated) return;

            CleanupOldAirdrops();
        }

        private static void TryApplyAirdropPatch()
        {
            if (_patchApplied) return;

            try
            {
                // Find AirdropLogicClass type using reflection
                var airdropLogicType = AccessTools.TypeByName("AirdropLogicClass");
                if (airdropLogicType == null)
                {
                    if (Plugin.DebugLogging.Value) Plugin.Log.LogWarning("[AirdropListener] AirdropLogicClass not found, airdrop detection disabled.");
                    return;
                }

                // Find method_3 (called when airdrop lands)
                var targetMethod = AccessTools.Method(airdropLogicType, "method_3");
                if (targetMethod == null)
                {
                    if (Plugin.DebugLogging.Value) Plugin.Log.LogWarning("[AirdropListener] AirdropLogicClass.method_3 not found, airdrop detection disabled.");
                    return;
                }

                // Create Harmony instance and patch
                var harmony = new Harmony("com.luc1dshadow.vulture.airdrop");
                var postfix = typeof(AirdropListener).GetMethod(nameof(OnAirdropLandPostfix), 
                    BindingFlags.Static | BindingFlags.NonPublic);
                
                harmony.Patch(targetMethod, postfix: new HarmonyMethod(postfix));
                
                _patchApplied = true;
                if (Plugin.DebugLogging.Value) Plugin.Log.LogInfo("[AirdropListener] Airdrop landing patch applied successfully.");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[AirdropListener] Failed to apply airdrop patch: {ex.Message}");
            }
        }

        /// <summary>
        /// Harmony postfix for AirdropLogicClass.method_3
        /// </summary>
        private static void OnAirdropLandPostfix(object __instance)
        {
            try
            {
                if (!Plugin.EnableAirdropVulturing.Value) return;

                // Get AirdropSynchronizableObject_0 property/field from the instance
                var instanceType = __instance.GetType();
                object airdropObj = null;
                
                // Try property first
                var airdropProp = instanceType.GetProperty("AirdropSynchronizableObject_0", 
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (airdropProp != null)
                {
                    airdropObj = airdropProp.GetValue(__instance);
                }
                
                // Try field if property not found
                if (airdropObj == null)
                {
                    var airdropField = instanceType.GetField("AirdropSynchronizableObject_0", 
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (airdropField != null)
                    {
                        airdropObj = airdropField.GetValue(__instance);
                    }
                }

                if (airdropObj == null)
                {
                    if (Plugin.DebugLogging.Value)
                        Plugin.Log.LogWarning("[AirdropListener] Could not get AirdropSynchronizableObject from instance.");
                    return;
                }

                // Access transform from the airdrop Component/MonoBehaviour
                if (airdropObj is Component airdropComponent)
                {
                    Vector3 position = airdropComponent.transform.position;
                    int instanceId = airdropComponent.GetInstanceID();
                    
                    OnAirdropLanded(position, instanceId);
                }
                else
                {
                    // Try reflection as fallback
                    var transformProp = airdropObj.GetType().GetProperty("transform");
                    if (transformProp != null)
                    {
                        var transform = transformProp.GetValue(airdropObj) as Transform;
                        if (transform != null)
                        {
                            Vector3 position = transform.position;
                            int instanceId = transform.gameObject.GetInstanceID();
                            OnAirdropLanded(position, instanceId);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (Plugin.DebugLogging.Value)
                    Plugin.Log.LogWarning($"[AirdropListener] Postfix error: {ex.Message}");
            }
        }

        /// <summary>
        /// Called when an airdrop lands to register it.
        /// </summary>
        private static void OnAirdropLanded(Vector3 position, int instanceId)
        {
            if (_trackedInstanceIds.Contains(instanceId)) return;

            _trackedInstanceIds.Add(instanceId);

            var airdropEvent = new AirdropEvent
            {
                Position = position,
                LandedTime = Time.time,
                InstanceId = instanceId,
                IsActive = true
            };

            ActiveAirdrops.Add(airdropEvent);
            
            if (Plugin.DebugLogging.Value) _log?.LogInfo($"[AirdropListener] Airdrop LANDED at {position}");
        }

        private void CleanupOldAirdrops()
        {
            float now = Time.time;
            float maxAge = Plugin.AirdropAmbushDuration.Value + 300f;

            for (int i = ActiveAirdrops.Count - 1; i >= 0; i--)
            {
                var drop = ActiveAirdrops[i];
                
                if (now - drop.LandedTime > maxAge)
                {
                    _trackedInstanceIds.Remove(drop.InstanceId);
                    ActiveAirdrops.RemoveAt(i);
                    
                    if (Plugin.DebugLogging.Value)
                        _log.LogInfo($"[AirdropListener] Airdrop expired and removed.");
                }
            }
        }

        /// <summary>
        /// Gets the nearest active airdrop within range.
        /// </summary>
        public static AirdropEvent? GetNearestAirdrop(Vector3 botPos, float maxDist)
        {
            AirdropEvent? nearest = null;
            float bestDistSq = maxDist * maxDist;

            for (int i = 0; i < ActiveAirdrops.Count; i++)
            {
                var drop = ActiveAirdrops[i];
                if (!drop.IsActive) continue;

                float distSq = (drop.Position - botPos).sqrMagnitude;
                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    nearest = drop;
                }
            }

            return nearest;
        }

        /// <summary>
        /// Check if any airdrop exists within range.
        /// </summary>
        public static bool HasAirdropInRange(Vector3 position, float range)
        {
            float rangeSq = range * range;
            
            for (int i = 0; i < ActiveAirdrops.Count; i++)
            {
                var drop = ActiveAirdrops[i];
                if (!drop.IsActive) continue;

                float distSq = (drop.Position - position).sqrMagnitude;
                if (distSq < rangeSq)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Called when raid ends to clear tracked airdrops.
        /// </summary>
        public static void OnRaidEnd()
        {
            ActiveAirdrops.Clear();
            _trackedInstanceIds.Clear();
        }

        private void OnDestroy()
        {
            OnRaidEnd();
        }
    }
}
