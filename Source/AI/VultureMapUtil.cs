using System.Collections.Generic;
using EFT;
using EFT.Interactive;
using UnityEngine;
using UnityEngine.AI;

namespace Luc1dShadow.Vulture.AI
{
    public static class VultureMapUtil
    {
        private static bool _initialized = false;
        public static List<Door> AllDoors { get; private set; } = new List<Door>();
        
        // Registry for claimed ambush points to prevent clustering
        // Key: Position, Value: (SquadID, TargetPosition)
        private struct AmbushClaim
        {
            public string SquadId;
            public Vector3 TargetPos;
        }
        private static readonly Dictionary<Vector3, AmbushClaim> _claimedAmbushPoints = new Dictionary<Vector3, AmbushClaim>();
        private static readonly float CLAIM_RADIUS_SQR = 8f * 8f; // 8m overlap prevention
        private static readonly float ANGLE_REJECTION_THRESHOLD = 60f; // Reject if firing angle is too similar

        public static void Initialize()
        {
            if (_initialized) return;

            CacheAllDoors();
            BlockOffMapZones();
            _initialized = true;
        }

        private static void CacheAllDoors()
        {
            try
            {
                var doors = Object.FindObjectsOfType<Door>();
                if (doors != null)
                {
                    AllDoors.AddRange(doors);
                }
                if (Plugin.DebugLogging.Value) Plugin.Log.LogInfo($"[Vulture] VultureMapUtil: Cached {AllDoors.Count} doors for collision handling.");
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError($"[Vulture] Failed to cache doors: {ex}");
            }
        }

        private static void BlockOffMapZones()
        {
            int count = 0;

            // Block Minefields
            var minefields = Object.FindObjectsOfType<Minefield>();
            if (minefields != null)
            {
                foreach (var minefield in minefields)
                {
                    AddNavMeshObstacle(minefield.gameObject);
                    count++;
                }
            }

            // Block Sniper Zones
            var sniperZones = Object.FindObjectsOfType<SniperFiringZone>();
            if (sniperZones != null)
            {
                foreach (var zone in sniperZones)
                {
                    AddNavMeshObstacle(zone.gameObject);
                    count++;
                }
            }

            if (count > 0)
            {
                if (Plugin.DebugLogging.Value) Plugin.Log.LogInfo($"VultureMapUtil: Blocked {count} off-map zones (Minefields/Snipers).");
            }
        }

        private static void AddNavMeshObstacle(GameObject obj)
        {
            // Only add if it has a collider to base the size on
            var collider = obj.GetComponent<BoxCollider>();
            if (collider == null) return;

            // Check if already has one
            if (obj.GetComponent<NavMeshObstacle>() != null) return;

            var obstacle = obj.AddComponent<NavMeshObstacle>();
            obstacle.carving = true; // Use simple carving
            obstacle.carvingMoveThreshold = 0.5f; // Don't update frequently (static)
            obstacle.shape = NavMeshObstacleShape.Box;
            
            // Align with collider
            obstacle.center = collider.center;
            
            // Adjust size to match world scale of collider
            // NavMeshObstacle size is relative to transform scale, just like BoxCollider size
            // So we can often just copy the size directly if scales are uniform
            // But let's be safe and ensure it matches the physical bounds
            
        // Simple approach: Copy the size directly as both rely on local scale
        obstacle.size = collider.size;
    }

    private static Vector3 RoundVector(Vector3 v)
    {
        return new Vector3(Mathf.Round(v.x * 10f) / 10f, Mathf.Round(v.y * 10f) / 10f, Mathf.Round(v.z * 10f) / 10f);
    }

    public static bool IsPointClaimed(Vector3 position, Vector3 targetPos, string requesterSquadId)
    {
        Vector3 roundedPos = RoundVector(position);
        foreach (var claim in _claimedAmbushPoints)
        {
            // DECONFLICTION: All bots (including squadmates) must respect claimed points 
            // to prevent huddling behind the same cover.

            float distSqr = (claim.Key - roundedPos).sqrMagnitude;
                if (distSqr < CLAIM_RADIUS_SQR)
                {
                    // If very close (<3m), unconditional rejection to prevent clipping
                    if (distSqr < 9f) return true;

                    // If within medium radius (8m), check Firing Angle
                    // If both squads are looking at the target from the same side, reject
                    Vector3 dirA = (claim.Value.TargetPos - claim.Key).normalized;
                    Vector3 dirB = (targetPos - position).normalized;

                    if (Vector3.Angle(dirA, dirB) < ANGLE_REJECTION_THRESHOLD)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        public static void ClaimPoint(Vector3 position, Vector3 targetPos, string squadId)
        {
            _claimedAmbushPoints[RoundVector(position)] = new AmbushClaim { SquadId = squadId, TargetPos = targetPos };
        }

        public static void ReleasePoint(Vector3 position)
        {
            _claimedAmbushPoints.Remove(RoundVector(position));
        }

        public static void ClearAllClaims()
        {
            _claimedAmbushPoints.Clear();
        }
    }
}
