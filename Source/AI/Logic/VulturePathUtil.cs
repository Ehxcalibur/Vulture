using System.Collections.Generic;
using EFT;
using EFT.Interactive;
using UnityEngine;
using UnityEngine.AI;

namespace Luc1dShadow.Vulture.AI
{
    /// <summary>
    /// Shared path validation and point-finding helpers used by multiple Logic classes.
    /// </summary>
    public static class VulturePathUtil
    {
        private static Dictionary<Vector2, bool> _indoorCache = new Dictionary<Vector2, bool>();
        private static float _lastCacheClearTime = 0f;

        public static bool IsPositionIndoors(Vector3 pos)
        {
            if (Time.time - _lastCacheClearTime > 5f)
            {
                _indoorCache.Clear();
                _lastCacheClearTime = Time.time;
            }

            // Round coordinates to 1m grid for caching
            Vector2 key = new Vector2(Mathf.Round(pos.x), Mathf.Round(pos.z));
            if (_indoorCache.TryGetValue(key, out bool cachedResult)) return cachedResult;

            // Vertical raycast to detect ceiling (Indoor heuristic)
            bool result = Physics.Raycast(pos + Vector3.up * 0.5f, Vector3.up, 15f, LayerMaskClass.HighPolyWithTerrainMask);
            _indoorCache[key] = result;
            return result;
        }

        public static bool TryFindBuildingEntrance(BotOwner bot, Vector3 targetPos, out Vector3 entrancePos)
        {
            entrancePos = Vector3.zero;
            float nearestDist = float.MaxValue;
            Door nearestDoor = null;

            foreach (var door in VultureMapUtil.AllDoors)
            {
                if (door == null || !door.Operatable) continue;

                float distSqr = (door.transform.position - targetPos).sqrMagnitude;
                if (distSqr < 2500f && distSqr < nearestDist) // 50m limit
                {
                    nearestDist = distSqr;
                    nearestDoor = door;
                }
            }

            if (nearestDoor != null)
            {
                // Randomize offset and jitter to distribute bots.
                Vector3 doorPos = nearestDoor.transform.position;
                Vector3 toDoor = (doorPos - bot.Position).normalized;
                
                float offsetDist = UnityEngine.Random.Range(2.0f, 7.0f);
                float jitter = UnityEngine.Random.Range(-15f, 15f);
                Vector3 offsetDir = Quaternion.Euler(0, jitter, 0) * toDoor;

                // Sample outside point
                Vector3 samplePos = doorPos - (offsetDir * offsetDist); 
                if (NavMesh.SamplePosition(samplePos, out NavMeshHit hit, 4.0f, NavMesh.AllAreas))
                {
                    entrancePos = hit.position;
                    return true;
                }
            }

            return false;
        }

        public static bool TryFindAmbushPoint(BotOwner bot, NavMeshPath path, Vector3 targetPos, out Vector3 result, out VultureCoverValidator.CoverheightType heightType)
        {
            result = Vector3.zero;
            heightType = VultureCoverValidator.CoverheightType.None;
            
            // 1. INDOOR HANDLING: Priority Building Entrance POIs
            Vector3 effectiveTarget = targetPos;
            bool isIndoors = IsPositionIndoors(targetPos);
            if (isIndoors)
            {
                if (TryFindBuildingEntrance(bot, targetPos, out Vector3 entrance))
                {
                    effectiveTarget = entrance;
                    if (Plugin.DebugLogging.Value) Plugin.Log.LogInfo($"[Vulture] Target is INDOORS. Focus shift to entrance POI at {entrance}");
                }
            }

            float distToTarget = Vector3.Distance(bot.Position, targetPos);
            string squadId = bot.BotsGroup?.Id.ToString() ?? bot.Id.ToString();
            float[] ranges = { Plugin.AmbushTier1.Value, Plugin.AmbushTier2.Value, Plugin.AmbushTier3.Value };

            // 1.5 CLOSE COMBAT FALLBACK: If very close, prioritize any viable cover even if not "perfect"
            if (distToTarget < 25f)
            {
                if (TrySearchStages(bot, path, effectiveTarget, new float[] { 10f, 15f }, false, out result, out heightType))
                {
                    if (!VultureMapUtil.IsPointClaimed(result, effectiveTarget, squadId))
                    {
                        if (Plugin.DebugLogging.Value) Plugin.Log.LogInfo($"[Vulture] Ambush found (Close Combat Fallback) at {result}");
                        VultureMapUtil.ClaimPoint(result, effectiveTarget, squadId);
                        return true;
                    }
                }
            }

            // 2. NATIVE COVER SEARCH (BSG Points)
            if (TrySearchStages(bot, path, effectiveTarget, ranges, true, out result, out heightType)) 
            {
                if (!VultureMapUtil.IsPointClaimed(result, effectiveTarget, squadId))
                {
                    if (Plugin.DebugLogging.Value) Plugin.Log.LogInfo($"[Vulture] Ambush found (Native Strict) at {result}");
                    VultureMapUtil.ClaimPoint(result, effectiveTarget, squadId);
                    return true;
                }
            }

            if (TrySearchStages(bot, path, effectiveTarget, ranges, false, out result, out heightType))
            {
                if (!VultureMapUtil.IsPointClaimed(result, effectiveTarget, squadId))
                {
                    if (Plugin.DebugLogging.Value) Plugin.Log.LogInfo($"[Vulture] Ambush found (Native Relaxed) at {result}");
                    VultureMapUtil.ClaimPoint(result, effectiveTarget, squadId);
                    return true;
                }
            }

            // 3. SYNTHETIC COVER SEARCH (Sunflower Pattern FALLBACK)
            if (TrySearchSyntheticSunflower(bot, path, effectiveTarget, ranges, out result, out heightType))
            {
                if (!VultureMapUtil.IsPointClaimed(result, effectiveTarget, squadId))
                {
                    if (Plugin.DebugLogging.Value) Plugin.Log.LogInfo($"[Vulture] Ambush found (Synthetic Sunflower) at {result}");
                    VultureMapUtil.ClaimPoint(result, effectiveTarget, squadId);
                    return true;
                }
            }

            // 4. DYNAMIC ENVIRONMENTAL CONCEALMENT (Last Resort)
            if (TryFindDynamicConcealment(bot, path, effectiveTarget, out result, out heightType))
            {
                if (Plugin.DebugLogging.Value) Plugin.Log.LogInfo($"[Vulture] Ambush found (Dynamic Concealment) at {result}");
                return true;
            }

            return false;
        }

        public static bool IsPositionCrowded(Vector3 pos, BotsGroup group, BotOwner self)
        {
            if (group == null || group.MembersCount < 2) return false;

            for (int i = 0; i < group.MembersCount; i++)
            {
                var member = group.Member(i);
                if (member == null || member.ProfileId == self.ProfileId) continue;
                if (member.HealthController == null || !member.HealthController.IsAlive) continue;

                // Check distance to current position (15m spacing)
                if (Vector3.SqrMagnitude(member.Position - pos) < 225f) 
                {
                    return true;
                }
            }
            return false;
        }

        private static bool TrySearchStages(BotOwner bot, NavMeshPath path, Vector3 target, float[] ranges, bool requireCluster, out Vector3 results, out VultureCoverValidator.CoverheightType heightType)
        {
            results = Vector3.zero;
            heightType = VultureCoverValidator.CoverheightType.None;
            for (int i = 0; i < ranges.Length; i++)
            {
                float range = ranges[i];
                float distToTarget = Vector3.Distance(bot.Position, target);

                if (distToTarget > 150f)
                {
                    float minPushRange = distToTarget / 2.5f; 
                    range = Mathf.Min(range, minPushRange);
                }

                Vector3 dir = (bot.Position - target).normalized;
                float jitter = UnityEngine.Random.Range(-5f, 5f);
                Vector3 jitterDir = Quaternion.Euler(0, jitter * 10f, 0) * dir;
                Vector3 idealPos = target + (jitterDir * (range + jitter));
                
                float searchRadius = (i == 0) ? 15f : 30f;
                var coverPoint = bot.Covers.GetClosestPoint(idealPos, null, false, (int)searchRadius);
                
                if (coverPoint != null && ValidatePointWithReachability(bot, path, coverPoint.Position, target))
                {
                    float maxDist = Mathf.Max(90f, range * 1.5f);
                    if (Vector3.Distance(coverPoint.Position, target) > maxDist) continue;

                    if (IsPositionCrowded(coverPoint.Position, bot.BotsGroup, bot)) continue;

                    var validation = VultureCoverValidator.Validate(coverPoint.Position, target);
                    if (!validation.IsValid) continue;

                    if (requireCluster && !IsGoodCoverPosition(bot, coverPoint.Position, target)) continue;

                    results = coverPoint.Position;
                    heightType = validation.HeightType;
                    return true;
                }
            }
            return false;
        }

        private static bool TrySearchSyntheticSunflower(BotOwner bot, NavMeshPath path, Vector3 target, float[] ranges, out Vector3 result, out VultureCoverValidator.CoverheightType heightType)
        {
            result = Vector3.zero;
            heightType = VultureCoverValidator.CoverheightType.None;

            // Spiral pattern parameters
            const int SAMPLE_COUNT = 16;
            float goldenAngle = Mathf.PI * (3f - Mathf.Sqrt(5f));

            foreach (float range in ranges)
            {
                Vector3 dir = (bot.Position - target).normalized;
                float jitter = UnityEngine.Random.Range(-5f, 5f);
                Vector3 jitterDir = Quaternion.Euler(0, jitter * 10f, 0) * dir;
                Vector3 searchOrigin = target + jitterDir * (range + jitter);

                for (int i = 0; i < SAMPLE_COUNT; i++)
                {
                    float theta = i * goldenAngle;
                    float r = 15f * Mathf.Sqrt((float)i / SAMPLE_COUNT); // 15m radius spread
                    
                    float x = searchOrigin.x + r * Mathf.Cos(theta);
                    float z = searchOrigin.z + r * Mathf.Sin(theta);
                    Vector3 candidate = new Vector3(x, searchOrigin.y + 1f, z);

                    if (NavMesh.SamplePosition(candidate, out NavMeshHit hit, 2.0f, NavMesh.AllAreas))
                    {
                        if (ValidatePointWithReachability(bot, path, hit.position, target))
                        {
                            var validation = VultureCoverValidator.Validate(hit.position, target);
                            if (validation.IsValid)
                            {
                                result = hit.position;
                                heightType = validation.HeightType;
                                return true;
                            }
                        }
                    }
                }
            }
            return false;
        }

        private static bool TryFindDynamicConcealment(BotOwner bot, NavMeshPath path, Vector3 target, out Vector3 result, out VultureCoverValidator.CoverheightType heightType)
        {
            result = Vector3.zero;
            heightType = VultureCoverValidator.CoverheightType.None;

            Vector3 searchOrigin = Vector3.Lerp(bot.Position, target, 0.5f);
            Collider[] colliders = Physics.OverlapSphere(searchOrigin, 30f, LayerMaskClass.HighPolyWithTerrainMask);
            
            foreach (var col in colliders)
            {
                if (col == null) continue;
                if (col.bounds.size.y < 0.6f || col.bounds.size.magnitude < 1.0f) continue;

                Vector3 dirToTarget = (target - col.bounds.center).normalized;
                Vector3 hidePos = col.bounds.center - (dirToTarget * (col.bounds.extents.magnitude + 1.0f));

                if (NavMesh.SamplePosition(hidePos, out NavMeshHit hit, 2.0f, NavMesh.AllAreas))
                {
                    if (ValidatePointWithReachability(bot, path, hit.position, target))
                    {
                        var validation = VultureCoverValidator.Validate(hit.position, target);
                        if (validation.IsValid)
                        {
                            result = hit.position;
                            heightType = validation.HeightType;
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        public static bool HasLOStoTarget(Vector3 point, Vector3 targetPos)
        {
            // Check from eye level (1.2m) to roughly target's center (0.8m)
            Vector3 origin = point + Vector3.up * 1.2f;
            Vector3 target = targetPos + Vector3.up * 0.8f;
            
            Vector3 dir = target - origin;
            float dist = dir.magnitude;
            
            // If target is very close, assume LOS or we'll want to push anyway
            if (dist < 5f) return true;

            // Use high poly mask to include walls/buildings
            if (Physics.Raycast(origin, dir, out RaycastHit hit, dist, LayerMaskClass.HighPolyWithTerrainMask))
            {
                // If we hit something significantly before the target, LOS is blocked
                if (hit.distance < dist - 0.5f) return false;
            }
            return true;
        }

        private static bool IsGoodCoverPosition(BotOwner bot, Vector3 point, Vector3 targetPos)
        {
            // If it's very close to the target, it's probably good regardless of surroundings
            if (Vector3.Distance(point, targetPos) < 20f) return true;

            // Check if there are other cover points nearby (not just the one we picked)
            // This indicates a "cluster" of cover (room, forest, urban) rather than a lone rock in a field
            int nearbyCovers = 0;
            const float CLUSTER_RADIUS = 15f;
            
            // We sample a few points around to see if they are also cover or have cover
            // Actually, BotOwner.Covers doesn't have an easy "GetCountInRange" but we can check a few offsets
            Vector3[] offsets = { 
                Vector3.forward * 5f, Vector3.back * 5f, 
                Vector3.left * 5f, Vector3.right * 5f 
            };

            foreach (var offset in offsets)
            {
                var nearby = bot.Covers.GetClosestPoint(point + offset, null, false, 10);
                if (nearby != null && Vector3.Distance(nearby.Position, point) < CLUSTER_RADIUS)
                {
                    nearbyCovers++;
                }
            }

            // If we found at least one other distinct cover point nearby, it's a cluster
            return nearbyCovers >= 1;
        }

        /// <summary>
        /// Validates NavMesh path and reachability.
        /// </summary>
        public static bool ValidatePointWithReachability(BotOwner bot, NavMeshPath path, Vector3 point, Vector3 targetPos)
        {
            if (bot == null || path == null) return false;

            // SANITIZATION: Vertical Clearance Check
            // Ensure there is at least 1.8m of headroom above the point to prevent "inside wall" or "under table" spawns.
            // Using a sphere cast or capsule would be better, but a simple raycast is 95% effective and cheaper.
            if (Physics.Raycast(point + Vector3.up * 0.1f, Vector3.up, out RaycastHit headHit, 1.8f, LayerMaskClass.HighPolyWithTerrainMask))
            {
                // If we hit something solid immediately above us, reject the point.
                return false;
            }
            
            path.ClearCorners();
            
            // SNAPPING: Ensure both bot and target are on NavMesh for validation
            Vector3 snappedBotPos = bot.Position;
            Vector3 snappedPoint = point;

            if (NavMesh.SamplePosition(bot.Position, out NavMeshHit botHit, 2.0f, NavMesh.AllAreas))
                snappedBotPos = botHit.position;

            if (NavMesh.SamplePosition(point, out NavMeshHit pointHit, 2.0f, NavMesh.AllAreas))
                snappedPoint = pointHit.position;

            if (NavMesh.CalculatePath(snappedBotPos, snappedPoint, NavMesh.AllAreas, path))
            {
                if (path.status == NavMeshPathStatus.PathComplete)
                {
                    float pathLen = CalculatePathLength(path);
                    float straightLen = Vector3.Distance(bot.Position, point);
                    
                    // REACHABILITY: Path must not be wildly indirect (1.8x limit, 3x for indoors)
                    float reachabilityThreshold = 1.8f;
                    if (IsPositionIndoors(point) || IsPositionIndoors(targetPos)) reachabilityThreshold = 3.0f;

                    if (pathLen > straightLen * reachabilityThreshold) return false;
                    
                    // SEGMENT SAFETY: Path must not pass too close to the danger zone (target)
                    // RELAXED: If we are already very close (<15m), allow tighter proximity to target during pathing.
                    float safetyRadius = straightLen < 15f ? 2.5f : 5.0f;
                    for (int i = 0; i < path.corners.Length - 1; i++)
                    {
                        float distToTarget = Vector3.Distance(targetPos, path.corners[i]);
                        if (distToTarget < safetyRadius) return false; // Don't run directly through target
                    }

                    return true;
                }
            }
            return false;
        }

        public static float CalculatePathLength(NavMeshPath path)
        {
            if (path.corners.Length < 2) return 0f;
            float length = 0f;
            for (int i = 0; i < path.corners.Length - 1; i++)
                length += Vector3.Distance(path.corners[i], path.corners[i + 1]);
            return length;
        }

        public static bool TryFindGreedTarget(BotOwner bot, NavMeshPath path, Vector3 eventPos, out Vector3 result)
        {
            result = Vector3.zero;
            if (eventPos == Vector3.zero) return false;
            
            // Step 1: Check the exact event point
            if (ValidatePointWithReachability(bot, path, eventPos, eventPos)) 
            { 
                result = eventPos; 
                return true; 
            }

            // Step 2: Sample nearby navmesh (3m radius)
            if (NavMesh.SamplePosition(eventPos, out NavMeshHit hit, 3.0f, NavMesh.AllAreas))
            {
                if (ValidatePointWithReachability(bot, path, hit.position, eventPos)) 
                { 
                    result = hit.position; 
                    return true; 
                }
            }

            // Step 3: Expanded search (5m radius) for missed snaps
            if (NavMesh.SamplePosition(eventPos, out NavMeshHit hitWide, 5.0f, NavMesh.AllAreas))
            {
                if (ValidatePointWithReachability(bot, path, hitWide.position, eventPos)) 
                { 
                    result = hitWide.position; 
                    return true; 
                }
            }

            // Validation: Ensure result isn't wildly far properly
            if (result != Vector3.zero && Vector3.Distance(result, eventPos) > 10f)
            {
                result = Vector3.zero;
                return false;
            }

            return false;
        }

        public static bool TryFindNearestDeadBody(BotOwner bot, out Vector3 bodyPos)
        {
            bodyPos = Vector3.zero;
            float nearestDistSqr = float.MaxValue;
            const float MAX_BODY_RANGE = 50f;

            try
            {
                var gameWorld = Comfort.Common.Singleton<GameWorld>.Instance;
                if (gameWorld == null || gameWorld.AllAlivePlayersList == null) return false;

                Vector3 botPos = bot.Position;
                foreach (var player in gameWorld.RegisteredPlayers)
                {
                    if (player == null) continue;
                    // Allow AI targets (e.g., scavs, bosses, bots).
                    // Ignore self
                    if (player.ProfileId == bot.ProfileId) continue;
                    
                    // Ignore squad members
                    if (bot.BotsGroup != null)
                    {
                        bool isSquad = false;
                        for (int m = 0; m < bot.BotsGroup.MembersCount; m++)
                        {
                            var member = bot.BotsGroup.Member(m);
                            if (member != null && member.ProfileId == player.ProfileId)
                            {
                                isSquad = true;
                                break;
                            }
                        }
                        if (isSquad) continue;
                    }

                    if (player.HealthController == null || player.HealthController.IsAlive) continue;

                    Vector3 toBody = player.Position - botPos;
                    float distSqr = toBody.sqrMagnitude;

                    if (distSqr < MAX_BODY_RANGE * MAX_BODY_RANGE && distSqr < nearestDistSqr)
                    {
                        if (!Physics.Linecast(botPos + Vector3.up * 1.2f, player.Position + Vector3.up * 0.5f, out _, LayerMaskClass.HighPolyWithTerrainMask))
                        {
                            bodyPos = player.Position;
                            nearestDistSqr = distSqr;
                        }
                    }
                }
                if (nearestDistSqr < float.MaxValue) return true;
            }
            catch { }
            return false;
        }

        public static bool TryFindCoverWithLOSToBody(BotOwner bot, NavMeshPath path, Vector3 bodyPos, out Vector3 coverPos, out VultureCoverValidator.CoverheightType heightType)
        {
            coverPos = Vector3.zero;
            heightType = VultureCoverValidator.CoverheightType.None;
            
            for (float radius = 8f; radius <= 12f; radius += 2f)
            {
                var cover = bot.Covers.GetClosestPoint(bodyPos + (UnityEngine.Random.onUnitSphere * radius), null, false, 25);
                if (cover != null && ValidatePointWithReachability(bot, path, cover.Position, bodyPos))
                {
                    var validation = VultureCoverValidator.Validate(cover.Position, bodyPos);
                    if (!validation.IsValid) continue;

                    if (HasLOStoTarget(cover.Position, bodyPos))
                    {
                        coverPos = cover.Position;
                        heightType = validation.HeightType;
                        return true;
                    }
                }
            }
            
            var closestCover = bot.Covers.GetClosestPoint(bot.Position, null, false, 25);
            if (closestCover != null && ValidatePointWithReachability(bot, path, closestCover.Position, bodyPos))
            {
                var validation = VultureCoverValidator.Validate(closestCover.Position, bodyPos);
                if (validation.IsValid)
                {
                    coverPos = closestCover.Position;
                    heightType = validation.HeightType;
                    return true;
                }
            }

            // SUNFLOWER FALLBACK FOR BODIES
            if (TrySearchSyntheticSunflower(bot, path, bodyPos, new float[] { 10f }, out coverPos, out heightType))
            {
                 return true;
            }

            return false;
        }

        public static bool IsSurvivorAliveInArea(Vector3 center, float radius, BotOwner bot)
        {
            try
            {
                var gameWorld = Comfort.Common.Singleton<GameWorld>.Instance;
                if (gameWorld == null) return false;

                float radiusSq = radius * radius;
                var players = gameWorld.AllAlivePlayersList;
                if (players == null) return false;

                foreach (var player in players)
            {
                if (player == null) continue;
                if (player.ProfileId == bot.ProfileId) continue;

                // FIX: Ignore other Vulture bots to prevent recursive vulturing across sides.
                if (player.AIData?.BotOwner != null && VultureLayer.IsVulture(player.AIData.BotOwner)) continue;

                // Relationship checks to avoid dependency issues.
                    if (bot.BotsGroup != null)
                    {
                        // 1. Check if same group ID (Immediate squadmates)
                        if (player.BotsGroup != null && player.BotsGroup.Id == bot.BotsGroup.Id) continue;

                        // 2. Check if same side (PMC side or Scav side)
                        if (player.Profile.Side == bot.Profile.Side) continue;
                    }

                    // Fallback for non-grouped bots
                    if (player.Profile.Side == bot.Profile.Side) continue;

                    // Skip marksmen/sniper scavs - they aren't worth vulturing
                    if (CombatSoundListener.IsMarksmanType(player.Profile.Info.Settings.Role)) continue;

                    // Check if within radius (AllAlivePlayersList should already handle the IsAlive check)
                    if ((player.Position - center).sqrMagnitude <= radiusSq)
                    {
                        return true;
                    }
                }
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError($"[VulturePathUtil] Error in IsSurvivorAliveInArea: {ex}");
            }
            return false;
        }
    }
}
