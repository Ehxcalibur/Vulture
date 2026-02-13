using EFT;
using EFT.Interactive;
using UnityEngine;
using UnityEngine.AI;

namespace Luc1dShadow.Vulture.AI.Movement
{
    /// <summary>
    /// Custom movement controller for Vulture bots.
    /// Replaces default BotOwner movement with controlled path following.
    /// </summary>
    public class VultureMover
    {
        // References
        private readonly BotOwner _bot;
        private readonly VulturePath _path;
        private readonly VultureStuckDetector _stuckDetector;
        
        // Configuration
        private const float DOOR_APPROACH_SPEED = 0.25f;    // Slow down for doors
        private const float SPRINT_STAMINA_STOP = 0.3f;     // Stop sprinting at 30%
        private const float SPRINT_STAMINA_RESUME = 0.95f;  // Resume at 95%
        private const float CORNER_ARRIVAL_DISTANCE = 0.35f; // Tight threshold for precise cornering
        private const float FINAL_ARRIVAL_DISTANCE = 0.6f;   // Tightened for precision
        private const float STICKY_ARRIVAL_DISTANCE = 1.2f;  // Tightened for precision
        private const float SPRINT_CORNER_DISTANCE = 0.6f;   // Wider threshold when sprinting (momentum)
        private const float DOOR_CHECK_RADIUS = 3.0f;
        private const float CORNER_SLOWDOWN_DIST = 0.85f;    // Start slowing within this distance of corner
        private const float SPRINT_LOOK_ANGLE_MAX = 15f;     // Tightened for precision
        
        // State
        private Vector3 _destination;
        private float _targetSpeed;
        private bool _sprintRequested;
        private bool _isActive;
        private float _cornerStartTime;  // Time when we started moving toward the current corner
        private float _remediationPauseUntil; // Timer to pause movement input for animations
        
        // Stuck & Spin state
        private float _lastYaw;
        private float _cumulativeRotation;
        private float _spinCheckTimer;
        private Vector3 _lastSteerDir;
        
        public bool IsMoving => _isActive && (IsWaitingForPath || (_path.HasPath && !_path.IsComplete)) && Time.time > _remediationPauseUntil;
        public bool IsRemediating => Time.time < _remediationPauseUntil;
        public bool IsStuck => _stuckDetector.IsStuck;
        public bool IsFatigued => _bot.GetPlayer.Physical.Stamina.NormalValue < SPRINT_STAMINA_STOP;
        public bool HasArrived { get; private set; }

        public bool IsSprinting => _sprintRequested;
        public Vector3 Destination => _destination;
        public float Speed => _bot.GetPlayer.Speed;
        public Vector3 CurrentMoveDirection { get; private set; }
        
        public bool IsWaitingForPath { get; private set; }
        
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public Vector3 GetLookAheadPoint(float dist)
        {
            return _path.CalcForwardPoint(_bot.Position, dist);
        }

        public VultureMover(BotOwner bot)
        {
            _bot = bot;
            _path = new VulturePath();
            _stuckDetector = new VultureStuckDetector();
        }
        
        /// <summary>
        /// Starts movement to a target position.
        /// </summary>
        /// <param name="destination">Target position</param>
        /// <param name="speed">Movement speed multiplier (0-1)</param>
        public bool MoveTo(Vector3 destination, float speed = 1.0f)
        {
            // Redundancy check: if already moving to this spot, don't re-submit async request
            if (_isActive && (_destination - destination).sqrMagnitude < 0.1f)
                return true;

            _destination = destination;
            _targetSpeed = speed;
            HasArrived = false;
            
            // Stop default bot movement to prevent interference
            _bot.Mover.Stop();
            _bot.Mover.Pause = true;
            IsWaitingForPath = true;

            // SNAPPING: Ensure we start from a valid NavMesh point to prevent "stuck in geometry" pathing failures.
            Vector3 snappedStart = _bot.Position;
            if (NavMesh.SamplePosition(_bot.Position, out NavMeshHit hit, 2.0f, NavMesh.AllAreas))
            {
                snappedStart = hit.position;
                if (Plugin.DebugLogging.Value && Vector3.Distance(_bot.Position, hit.position) > 0.5f)
                    Plugin.Log.LogInfo($"[VultureMover] Path start snapped {Vector3.Distance(_bot.Position, hit.position):F1}m to NavMesh.");
            }

            VultureNavQueue.SubmitRequest(snappedStart, destination, OnPathCalculated);
            
            return true;
        }

        private void OnPathCalculated(NavMeshPath path, bool success)
        {
            IsWaitingForPath = false;

            if (!success)
            {
                if (Plugin.DebugLogging.Value)
                    Plugin.Log.LogWarning($"[VultureMover] Async path failed to {_destination}");
                _isActive = false;
                return;
            }

            _path.ApplyNavMeshPath(path);
            _stuckDetector.Reset();
            _cornerStartTime = Time.time;
            _isActive = true;

            if (Plugin.DebugLogging.Value)
                Plugin.Log.LogInfo($"[VultureMover] Async Path ready with {_path.Corners.Length} corners");
        }

        public void SetSprint(bool shouldSprint)
        {
            _sprintRequested = shouldSprint;
        }

        public void SetTargetSpeed(float speed)
        {
            _targetSpeed = speed;
        }
        
        /// <summary>
        /// Stops all movement.
        /// </summary>
        public void Stop()
        {
            _isActive = false;
            _path?.Clear();
            _stuckDetector?.Reset();
            
            // Stop player input
            try
            {
                var player = _bot?.GetPlayer;
                if (player != null && player.gameObject != null && player.CharacterController != null)
                {
                    player.CharacterController.SetSteerDirection(Vector3.zero);
                    // Only call Move if the player isn't being destroyed/disposed
                    if (player.enabled && player.gameObject.activeInHierarchy)
                    {
                        try { player.Move(Vector2.zero); } catch { }
                    }
                }
            }
            catch { }
        }
        
        /// <summary>
        /// Update movement. Call every frame while movement is active.
        /// </summary>
        /// <param name="shouldSteer">If true, mover handles steering. If false, external logic handles it.</param>
        public void Update(bool shouldSteer = true)
        {
            if (_bot == null || _bot.GetPlayer == null) return;

            // Sync Stamina state (Throttled)
            if (Time.frameCount % 15 == 0)
            {
                var state = VultureLayer.GetOrCreateState(_bot);
                if (state != null)
                {
                    float curStamina = _bot.GetPlayer.Physical.Stamina.NormalValue;
                    if (curStamina < SPRINT_STAMINA_STOP) state.IsStaminaDepleted = true;
                    else if (curStamina > SPRINT_STAMINA_RESUME) state.IsStaminaDepleted = false;
                }
            }

            if (IsWaitingForPath)
            {
                // Bot continues default brain behavior or stays still while waiting.
                // We don't return here because we might want to sync stamina even while waiting.
            }

            if (!_isActive || !_path.HasPath || IsWaitingForPath)
                return;

            Vector3 botPos = _bot.Position;
            
            // -------------------------------------------------------------------------
            // 1. Check Arrival
            // -------------------------------------------------------------------------
            Vector3 toDestination = _destination - botPos;
            // Explicitly use 3D distance to prevent multi-floor "ghost arrival"
            float distToTarget = toDestination.magnitude;

            // Dynamic Arrival Distance for Precision
            float finalArrivalDist = FINAL_ARRIVAL_DISTANCE;
            if (_bot.Mover.Sprinting)
                finalArrivalDist = 0.8f; // Wider for momentum
            else if (_bot.GetPlayer.MovementContext.PoseLevel < 0.7f)
                finalArrivalDist = 0.15f; // Hyper-precise for creeping
            else
                finalArrivalDist = 0.4f; // Walk precision

            if (distToTarget <= finalArrivalDist)
            {
                HasArrived = true;
                Stop();
                if (Plugin.DebugLogging.Value) Plugin.Log.LogInfo("[VultureMover] Arrived at destination");
                return;
            }

            // Sticky Arrival: Handles cases where bots are stuck near the finish line.
            if (toDestination.sqrMagnitude <= STICKY_ARRIVAL_DISTANCE * STICKY_ARRIVAL_DISTANCE)
            {
                if (_stuckDetector.IsStuck && _stuckDetector.StuckDuration > 1.5f)
                {
                    HasArrived = true;
                    Stop();
                    if (Plugin.DebugLogging.Value) Plugin.Log.LogWarning("[VultureMover] Forced arrival: Stuck near destination.");
                    return;
                }
            }
            
            // -------------------------------------------------------------------------
            // 2. Path Progress & Corner Management
            // -------------------------------------------------------------------------
            float cornerDistance = CORNER_ARRIVAL_DISTANCE;
            if (_bot.Mover.Sprinting)
                cornerDistance = 0.8f; // Wider for momentum
            else if (_bot.GetPlayer.MovementContext.PoseLevel < 0.7f)
                cornerDistance = 0.15f; // Hyper-precise for creeping
            else
                cornerDistance = 0.4f; // Walk precision

            bool cornerReached = _path.HasReachedCurrentCorner(botPos, cornerDistance);
            
            // Smart Corner Cut (Conservative)
            if (!cornerReached && !_path.IsLastCorner)
            {
                Vector3 toCurrentCorner = _path.CurrentCorner - botPos;
                toCurrentCorner.y = 0;
                // Reduced from 2.5f to 1.0f for stability
                if (toCurrentCorner.sqrMagnitude < 1.0f) 
                {
                    int nextIndex = _path.CurrentCornerIndex + 1;
                    if (nextIndex < _path.Corners.Length)
                    {
                        Vector3 nextCorner = _path.Corners[nextIndex];
                        if (!NavMesh.Raycast(botPos, nextCorner, out _, NavMesh.AllAreas))
                        {
                            cornerReached = true;
                        }
                    }
                }
            }
            
            if (cornerReached)
            {
                _path.AdvanceCorner();
                _cornerStartTime = Time.time;
                if (_path.IsComplete)
                {
                    HasArrived = true;
                    Stop();
                    return;
                }
            }

            // -------------------------------------------------------------------------
            // 3. Stuck Detection & Remediation (Paced)
            // -------------------------------------------------------------------------
            if (Time.frameCount % 5 == 0)
            {
                float pathSegmentLength = 0f;
                if (_path.HasPath && _path.CurrentCornerIndex > 0)
                {
                    Vector3 segmentStart = _path.Corners[_path.CurrentCornerIndex - 1];
                    Vector3 segmentEnd = _path.CurrentCorner;
                    pathSegmentLength = Vector3.Distance(segmentStart, segmentEnd);
                }

                float distToDest = Vector3.Distance(botPos, _destination);
                
                // Spin Detection Upgrade
                UpdateSpinDetection(botPos);
                
                var remediation = _stuckDetector.Update(botPos, _targetSpeed, distToDest, pathSegmentLength);
                HandleRemediation(remediation);
            }

            // -------------------------------------------------------------------------
            // 4. Movement Execution
            // -------------------------------------------------------------------------
            Vector3 moveDirection = _path.CurrentCorner - botPos;
            moveDirection.y = 0;
            moveDirection.Normalize();

            // Path Correction (Continuous Spring Force)
            // Scale spring strength inversely with path complexity (jitter)
            // On straight paths: full spring. Near stairs/corners: weaken to let corner-targeting dominate.
            Vector3 pathCorrection = CalculatePathCorrection(new Vector2(botPos.x, botPos.z));
            float pathJitter = _path.CalculatePathAngleJitter(5f);
            float springStrength = Mathf.Clamp01(1.0f - pathJitter / 60f);
            moveDirection = (moveDirection + pathCorrection * springStrength).normalized;

            CurrentMoveDirection = moveDirection;
            
            _bot.AIData.SetPosToVoxel(botPos);
            
            // Safe access to player and components
            var player = _bot.GetPlayer;
            if (player == null || !player.enabled || !player.gameObject.activeInHierarchy) return;

            if (_bot.AimingManager?.CurrentAiming != null)
                _bot.AimingManager.CurrentAiming.Move(_targetSpeed);

            // -------------------------------------------------------------------------
            // 6. Stamina & Speed Logic
            // -------------------------------------------------------------------------
            float finalSpeed = _targetSpeed;
            bool safeToSprint = _sprintRequested;

            // Slow down near corners to prevent overshooting
            Vector3 toCorner = _path.CurrentCorner - botPos;
            toCorner.y = 0;
            float cornerDist = toCorner.magnitude;
            if (cornerDist <= CORNER_SLOWDOWN_DIST && !_path.IsLastCorner)
            {
                // Scale speed proportionally: full speed at 0.85m, ~10% speed at point-blank
                float slowFactor = Mathf.Clamp01(cornerDist / CORNER_SLOWDOWN_DIST);
                finalSpeed *= Mathf.Max(slowFactor, 0.1f);
            }

            if (IsNearDoor(botPos))
            {
                finalSpeed = Mathf.Min(finalSpeed, DOOR_APPROACH_SPEED);
                safeToSprint = false;
            }

            float stamina = _bot.GetPlayer.Physical.Stamina.NormalValue;
            bool isUnderFire = _bot.Memory.IsUnderFire;

            if (safeToSprint && !isUnderFire)
            {
                if (_bot.Mover.Sprinting)
                {
                    if (stamina < SPRINT_STAMINA_STOP) safeToSprint = false;
                }
                else
                {
                    if (stamina < SPRINT_STAMINA_RESUME) safeToSprint = false;
                }
            }

            // Indoor sprint decision is handled by VultureMoveLogic.UpdateMovementStance()

            if (safeToSprint && _path.CalculatePathAngleJitter(10f) > 30f)
            {
               safeToSprint = false; 
            }

            // Sprint look-direction gate: disable sprint if bot isn't facing the next corner.
            // Prevents moonwalking through turns — bot must face forward to sprint.
            if (safeToSprint)
            {
                try
                {
                    Vector3 lookDir = _bot.GetPlayer.Transform.forward;
                    lookDir.y = 0;
                    Vector3 cornerDir = moveDirection;
                    cornerDir.y = 0;
                    if (lookDir.sqrMagnitude > 0.001f && cornerDir.sqrMagnitude > 0.001f)
                    {
                        float angle = Vector3.Angle(lookDir.normalized, cornerDir.normalized);
                        if (angle > SPRINT_LOOK_ANGLE_MAX)
                            safeToSprint = false;
                    }
                }
                catch { }
            }

            // -------------------------------------------------------------------------
            // 7. Apply Actions
            // -------------------------------------------------------------------------
            if (_bot.Mover.Sprinting != safeToSprint)
                _bot.Mover.Sprint(safeToSprint);

            // Door Handling (Paced)
            if (Time.frameCount % 5 == 0)
            {
                CheckAndOpenDoors(botPos, moveDirection);
            }
            
            ApplyMovement(moveDirection, finalSpeed, shouldSteer);
        }

        private void UpdateSpinDetection(Vector3 botPos)
        {
            if (!_isActive || Time.time < _remediationPauseUntil) return;

            float currentYaw = _bot.GetPlayer.Rotation.x;
            float deltaYaw = Mathf.DeltaAngle(_lastYaw, currentYaw);
            _cumulativeRotation += Mathf.Abs(deltaYaw);
            _lastYaw = currentYaw;

            if (Time.time > _spinCheckTimer)
            {
                // If bot rotated > 360 degrees in 1.5s with zero progress, it's spinning.
                if (_cumulativeRotation > 360f && _stuckDetector.IsStuck)
                {
                    if (Plugin.DebugLogging.Value) Plugin.Log.LogWarning($"[VultureMover] Spin detected ({_cumulativeRotation:F0} deg). Forcing immediate Jump.");
                    HandleRemediation(VultureStuckDetector.RemediationAction.Jump);
                }
                _cumulativeRotation = 0f;
                _spinCheckTimer = Time.time + 1.5f;
            }
        }

        private bool IsNearDoor(Vector3 botPos)
        {
             // Check local voxel data for doors
             var voxelData = _bot.VoxelesPersonalData?.CurVoxel;
             if (voxelData != null && voxelData.DoorLinks.Count > 0)
             {
                 foreach(var link in voxelData.DoorLinks)
                 {
                     if (link.Door != null && (link.Door.transform.position - botPos).sqrMagnitude < 9.0f) // < 3m
                     {
                         return true;
                     }
                 }
             }
             return false;
        }
        
        private bool _doorsIgnored = false;
        private void IgnoreDoors()
        {
            if (_doorsIgnored) return;
            
            // Prevent physics snagging on door frames during navigation.
            // NavMesh handles doorway traversal; we disable collision to avoid frame catches.
            try
            {
                // Get bot's primary collision component
                Collider botCollider = null;
                var characterController = _bot.GetPlayer.CharacterController;
                if (characterController != null)
                {
                    botCollider = characterController as Collider;
                }
                
                // Fallback: search for any collider on player object
                if (botCollider == null)
                {
                    var colliders = _bot.GetPlayer.gameObject.GetComponents<Collider>();
                    if (colliders != null && colliders.Length > 0)
                        botCollider = colliders[0];
                }
                
                if (botCollider == null) return;

                int processedCount = 0;
                var doorList = Vulture.AI.VultureMapUtil.AllDoors;
                if (doorList != null)
                {
                    foreach (var doorObj in doorList)
                    {
                        if (doorObj == null) continue;
                        
                        // Handle doors with multiple collision components (frames, leaves, handles)
                        var allDoorColliders = doorObj.GetComponentsInChildren<Collider>(true);
                        if (allDoorColliders != null)
                        {
                            foreach (var doorCollider in allDoorColliders)
                            {
                                if (doorCollider != null)
                                {
                                    Physics.IgnoreCollision(botCollider, doorCollider, true);
                                }
                            }
                        }
                        processedCount++;
                    }
                }
                
                _doorsIgnored = true;
                if (Plugin.DebugLogging.Value) Plugin.Log.LogInfo($"[VultureMover] Disabled collision with {processedCount} door objects.");
            }
            catch (System.Exception ex)
            {
                if (Plugin.DebugLogging.Value) Plugin.Log.LogError($"[VultureMover] Error disabling door collisions: {ex}");
            }
        }

        /// <summary>
        /// Calculates a correction vector to keep the bot on the path line.
        /// Uses 2D segment deviation spring logic to snap back to the navmesh line.
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private Vector3 CalculatePathCorrection(Vector2 botPos2d)
        {
            if (!_path.HasPath || _path.CurrentCornerIndex <= 0) return Vector3.zero;

            Vector3 prev = _path.Corners[_path.CurrentCornerIndex - 1];
            Vector3 next = _path.Corners[_path.CurrentCornerIndex];

            Vector2 prev2d = new Vector2(prev.x, prev.z);
            Vector2 next2d = new Vector2(next.x, next.z);

            // Vector projection to find the closest point on the current segment
            Vector2 line = next2d - prev2d;
            float lineLenSq = line.sqrMagnitude;
            if (lineLenSq < 1e-4) return Vector3.zero;

            float t = Vector2.Dot(botPos2d - prev2d, line) / lineLenSq;
            t = Mathf.Clamp01(t);
            Vector2 closestPoint = prev2d + t * line;

            // Spring effect: directional vector from current pos to path line
            Vector2 deviation2d = closestPoint - botPos2d;
            
            return new Vector3(deviation2d.x, 0, deviation2d.y);
        }
        
        // ... (IsOnNarrowEdge removed - no longer needed) ...
        // ... (CalculateWallRepulsion removed) ...
        
        // Update Voxel Position (Required for AI to know where it is)
        
        private void ApplyMovement(Vector3 worldDirection, float moveSpeed, bool shouldSteer)
        {
            try
            {
                var player = _bot.GetPlayer;
                if (player == null || !player.enabled || !player.gameObject.activeInHierarchy) return;
                
                var movementContext = player.MovementContext;
                if (movementContext == null) return;
                
                // Steer direction: pass full 3D direction so CharacterController handles stairs/ramps
                // This is the critical difference — NOT stripping Y here allows terrain traversal
                if (shouldSteer || _sprintRequested)
                {
                    if (worldDirection != Vector3.zero)
                    {
                        if (_stuckDetector.IsStuck && _stuckDetector.StuckDuration > 1.0f)
                        {
                            // Rate-limit the steering by Slerping toward the new direction very slowly
                            _lastSteerDir = Vector3.Slerp(_lastSteerDir, worldDirection.normalized, Time.deltaTime * 2.0f);
                        }
                        else
                        {
                            _lastSteerDir = worldDirection.normalized;
                        }

                        if (player.CharacterController != null)
                            player.CharacterController.SetSteerDirection(_lastSteerDir);
                    }
                }
                
                // SPRINTING: Enable sprint if requested and conditions met.
                bool canSprint = _sprintRequested && movementContext.CanSprint;
                player.EnableSprint(canSprint);
                
                // Apply speed
                _bot.Mover.SetTargetMoveSpeed(moveSpeed);
                
                // Convert world direction to local 2D input for Player.Move()
                // This naturally drops Y since it's forward/strafe only
                float yaw = player.Rotation.x;
                Vector3 localDir = Quaternion.Euler(0f, 0f, yaw) * new Vector2(worldDirection.x, worldDirection.z);
                Vector2 localInput = new Vector2(localDir.x, localDir.y);
                
                // If sprinting, force input forward for engine reliability
                if (canSprint)
                {
                    localInput = Vector2.up; // (0,1)
                }
                
                // Stop vanilla mover so it doesn't fight our steering
                _bot.Mover.Stop();
                _bot.Mover.Pause = true;
                
                // Apply final movement input
                player.Move(localInput);
                _bot.AimingManager?.CurrentAiming?.Move(player.Speed);
            }
            catch (System.Exception ex)
            {
                if (Plugin.DebugLogging.Value)
                    Plugin.Log.LogError($"[VultureMover] Movement error: {ex.Message}");
            }
        }
        
        
        
        /// <summary>
        /// Checks for closed doors nearby and opens them.
        /// Includes selective collision padding.
        /// </summary>
        private void CheckAndOpenDoors(Vector3 botPos, Vector3 moveDir)
        {
            try
            {
                var voxelData = _bot.VoxelesPersonalData?.CurVoxel;
                if (voxelData == null || voxelData.DoorLinks.Count == 0)
                    return;
                
                var botCollider = _bot.GetPlayer.CharacterController.GetCollider();

                foreach (var doorLink in voxelData.DoorLinks)
                {
                    var door = doorLink.Door;
                    if (door == null) continue;
                    
                    Vector3 toDoor = door.transform.position - botPos;
                    float distSqr = toDoor.sqrMagnitude;
                    
                    if (distSqr > DOOR_CHECK_RADIUS * DOOR_CHECK_RADIUS)
                        continue;
                    
                    // Selective collision padding while near door
                    if (botCollider != null && door.Collider != null)
                    {
                        EFTPhysicsClass.IgnoreCollision(botCollider, door.Collider, true);
                    }

                    toDoor.y = 0;
                    if (Vector3.Dot(toDoor.normalized, moveDir) < 0.2f)
                        continue;
                    
                    if (door.DoorState == EDoorState.Open || 
                        door.DoorState == EDoorState.Locked || 
                        !door.Operatable || 
                        !door.enabled ||
                        door.InteractingPlayer != null)
                        continue;
                    
                    var player = _bot.GetPlayer;
                    if (player != null && player.enabled && player.gameObject.activeInHierarchy)
                    {
                        player.vmethod_1(door, new InteractionResult(EInteractionType.Open));
                    }
                    
                    if (Plugin.DebugLogging.Value)
                        Plugin.Log.LogInfo("[VultureMover] Approached and padded door collision.");
                    
                    break;
                }
            }
            catch { }
        }
        
        private void HandleRemediation(VultureStuckDetector.RemediationAction action)
        {
            if (action != VultureStuckDetector.RemediationAction.None && Plugin.DebugLogging.Value)
                Plugin.Log.LogInfo($"[VultureMover] Stuck! Attempting: {action} (Tier {_stuckDetector.CurrentTier})");
            
            try
            {
                var player = _bot.GetPlayer;
                if (player == null || !player.enabled || !player.gameObject.activeInHierarchy) return;

                var movementContext = player.MovementContext;
                if (movementContext == null) return;
                
                switch (action)
                {
                    case VultureStuckDetector.RemediationAction.Vault:
                        movementContext.TryVaulting();
                        _remediationPauseUntil = Time.time + 0.75f; // Animation breather
                        break;
                        
                    case VultureStuckDetector.RemediationAction.Jump:
                        movementContext.TryJump();
                        _remediationPauseUntil = Time.time + 0.5f; // Jump breather
                        break;
 
                    case VultureStuckDetector.RemediationAction.Repath:
                        // Force recalculation of path
                        // ADAPTATION: Add a tiny "nudge" to help break physics contact
                        Vector3 nudge = (Random.insideUnitSphere * 0.15f);
                        nudge.y = 0;
                        player.Teleport(_bot.Position + nudge);
                        
                        MoveTo(_destination, _targetSpeed);
                        break;
                        
                    case VultureStuckDetector.RemediationAction.Teleport:
                        // Last resort: teleport to nearest valid NavMesh position
                        TryTeleportUnstuck();
                        break;
 
                    case VultureStuckDetector.RemediationAction.ForceArrival:
                        // Special case: we are very close to destination but blocked by prop/crate.
                        // Consider it "Good Enough" and stop.
                        HasArrived = true;
                        Stop();
                        if (Plugin.DebugLogging.Value) Plugin.Log.LogWarning($"[VultureMover] Safe Arrival triggered (Stuck near goal). Stopping.");
                        break;
                }
            }
            catch (System.Exception ex)
            {
                if (Plugin.DebugLogging.Value)
                    Plugin.Log.LogError($"[VultureMover] Remediation error: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Last resort unstuck: teleport to nearest valid NavMesh position.
        /// Includes safety checks (proximity and visibility) for teleportation.
        /// </summary>
        private void TryTeleportUnstuck()
        {
            Vector3 botPos = _bot.Position;
            
            // Proximity Check: Ensure no human players are nearby
            // Don't teleport if a human player is too close
            foreach (var player in GetHumanPlayers())
            {
                if ((player.Position - botPos).sqrMagnitude < 100f) // 10m
                {
                    if (Plugin.DebugLogging.Value) Plugin.Log.LogWarning($"[VultureMover] Teleport aborted: Player {player.Profile.Nickname} too close.");
                    return;
                }
            }

            // Visibility Check: Ensure bot is not visible to any human player
            // Don't teleport if a human player can see the bot
            if (IsVisibleToAnyPlayer())
            {
                if (Plugin.DebugLogging.Value) Plugin.Log.LogWarning($"[VultureMover] Teleport aborted: Bot is visible to a human player.");
                return;
            }

            // Try to find a valid position on the NavMesh nearby
            for (float radius = 1f; radius <= 5f; radius += 1f)
            {
                if (NavMesh.SamplePosition(botPos, out NavMeshHit hit, radius, NavMesh.AllAreas))
                {
                    // Found valid position - teleport there
                    // Teleport to the NEXT corner if available, otherwise current.
                    Vector3 targetPos = _path.CurrentCorner;
                    if (_path.CurrentCornerIndex + 1 < _path.Corners.Length)
                    {
                        targetPos = _path.Corners[_path.CurrentCornerIndex + 1];
                    }
                    
                    // Validate targetPos is on NavMesh (prevent teleporting inside fence)
                    if (NavMesh.SamplePosition(targetPos, out NavMeshHit targetHit, 1.5f, NavMesh.AllAreas))
                    {
                        targetPos = targetHit.position;
                    }
                    
                    Vector3 teleportPos = targetPos + Vector3.up * 0.5f; 
                    _bot.GetPlayer.Teleport(teleportPos);
                    
                    if (Plugin.DebugLogging.Value)
                        Plugin.Log.LogWarning($"[VultureMover] ESCALATED TELEPORT bot to {teleportPos} (clearance)");
                    
                    // Reset and repath
                    _stuckDetector.Reset();
                    MoveTo(_destination, _targetSpeed);
                    return;
                }
            }
            
            // Teleport failed - give up
            if (Plugin.DebugLogging.Value)
                Plugin.Log.LogError("[VultureMover] Teleport failed - no valid NavMesh position found");
            
            Stop();
        }

        private System.Collections.Generic.List<Player> GetHumanPlayers()
        {
            var humans = new System.Collections.Generic.List<Player>();
            
            // Use Singleton<GameWorld>.Instance.AllAlivePlayersList
            var gameWorld = Comfort.Common.Singleton<GameWorld>.Instance;
            if (gameWorld != null && gameWorld.AllAlivePlayersList != null)
            {
                foreach (var p in gameWorld.AllAlivePlayersList)
                {
                    if (p != null && !p.IsAI) humans.Add(p);
                }
            }
            return humans;
        }

        private bool IsVisibleToAnyPlayer()
        {
            var humans = GetHumanPlayers();
            var botPlayer = _bot.GetPlayer;
            var bodyParts = botPlayer.PlayerBones.BodyPartCollidersDictionary;
            
            // Key body parts to check for visibility
            EBodyPartColliderType[] partsToCheck = {
                EBodyPartColliderType.HeadCommon,
                EBodyPartColliderType.Pelvis,
                EBodyPartColliderType.LeftForearm,
                EBodyPartColliderType.RightForearm,
                EBodyPartColliderType.LeftCalf,
                EBodyPartColliderType.RightCalf
            };

            foreach (var human in humans)
            {
                Vector3 humanHead = human.PlayerBones.Head.Original.position;
                foreach (var partType in partsToCheck)
                {
                    if (bodyParts.TryGetValue(partType, out var collider))
                    {
                        // Use Linecast check to verify LOS
                        if (!Physics.Linecast(humanHead, collider.transform.position, out _, LayerMaskClass.HighPolyWithTerrainMask))
                        {
                            // No obstacle between human head and bot body part -> Visible!
                            return true;
                        }
                    }
                }
            }
            return false;
        }
    }
}
