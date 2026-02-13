using DrakiaXYZ.BigBrain.Brains;
using EFT;
using UnityEngine;
using Luc1dShadow.Vulture.AI;

namespace Luc1dShadow.Vulture
{
    /// <summary>
    /// Handles movement to ambush point. Includes sprint, silent approach, stamina walk, and steering.
    /// EnterBy: "Moving to Ambush"
    /// </summary>
    public class VultureMoveLogic : CustomLogic
    {
        private VultureBotState _state;
        private float _lastStateChangeTime;
        private bool _lastSprintState;

        public VultureMoveLogic(BotOwner botOwner) : base(botOwner) { }

        public override void Start()
        {
            _state = VultureLayer.GetOrCreateState(BotOwner);
            
            // LAZY INITIALIZATION: Perform heavy search/pathing only once logic is active
            if (!_state.InitializationAttempted)
            {
                if (!_state.Initialize())
                {
                    // Initialization failure sets ShouldStop = true internally
                    _state.ShouldStop = true;
                    return;
                }
            }

            if (Plugin.DebugLogging.Value) Plugin.Log.LogInfo($"[VultureMoveLogic] Start for {BotOwner.Profile.Nickname}");
        }

        public override void Stop() { }

        public override void Update(CustomLayer.ActionData data)
        {
            if (_state == null) return;

            try
            {
                // Courage Check — suppress movement under heavy fire with hysteresis
                int localIntensity = CombatSoundListener.GetEventIntensity(BotOwner.Position, 50f, 5f);
                
                if (_state.IsSuppressed)
                {
                    BotOwner.StopMove();
                    BotOwner.SetPose(0.1f);

                    // Hysteresis: Stay suppressed until timer expires AND intensity drops below 80% of threshold
                    if (Time.time > _state.SuppressionTimer && localIntensity < Plugin.CourageThreshold.Value * 0.8f)
                    {
                        _state.IsSuppressed = false;
                        if (Plugin.DebugLogging.Value) Plugin.Log.LogInfo($"[VultureMoveLogic] {BotOwner.Profile.Nickname} recovery from suppression.");
                    }
                    return;
                }
                else if (localIntensity > Plugin.CourageThreshold.Value)
                {
                    _state.IsSuppressed = true;
                    _state.SuppressionTimer = Time.time + 5.0f; // Minimum 5s suppression
                    BotOwner.StopMove();
                    BotOwner.SetPose(0.1f);
                    if (Plugin.DebugLogging.Value) Plugin.Log.LogInfo($"[VultureMoveLogic] {BotOwner.Profile.Nickname} suppressed by intensity {localIntensity}.");
                    return;
                }

                // Sprint / Silent Approach
                UpdateMovementStance();

                // Advanced Steering: Look-Ahead Smoothing
                // If sprinting or moving fast, look ahead.
                bool isMovingFast = _state.Mover.IsSprinting || (_state.Mover.IsMoving && !_state.IsCreeping);
                
                if (isMovingFast)
                {
                    // Dynamic Look-Ahead with Horizon Alignment
                    Vector3 lookPoint = _state.Mover.GetLookAheadPoint(1.5f);
                    
                    // Origin should be the bot's head, not its feet
                    Vector3 headPos = BotOwner.GetPlayer.MainParts[BodyPartType.head].Position;
                    
                    // Vector from head to target
                    Vector3 lookDir = (lookPoint - headPos).normalized;
                    
                    // Force gaze to be horizontal during movement
                    // This prevents the "looking up/down" issue while walking on uneven terrain or pipes
                    lookDir.y = 0.01f; // Slight upward bias for horizon
                    
                    BotOwner.Steering.LookToDirection(lookDir, 540f);
                    
                    _state.IsScanning = false;
                }
                else
                {
                    // Creep mode: look FORWARD along path
                    Vector3 creepLookPoint = _state.Mover.GetLookAheadPoint(3.0f);
                    Vector3 headPos = BotOwner.GetPlayer.MainParts[BodyPartType.head].Position;
                    
                    Vector3 creepLookDir = (creepLookPoint - headPos).normalized;
                    creepLookDir.y = 0; // Force horizontal
                    
                    if (creepLookDir != Vector3.zero)
                    {
                        BotOwner.Steering.LookToDirection(creepLookDir, 180f);
                    }
                }

                _state.Mover.Update(true);

                // Dead Movement Check (with 3.0s grace period for recalculations/nudges)
                if (!_state.Mover.IsMoving && !_state.Mover.HasArrived && !_state.Mover.IsWaitingForPath)
                {
                     // IMMUNITY: If the mover is currently performing a remediation (Jump/Vault), ignore the stall check.
                     // The mover sets IsMoving = false during the animation pause.
                     if (_state.Mover.IsRemediating)
                     {
                         return;
                     }

                     float timeSinceStart = Time.time - _state.MoveStartTime;
                     
                     // SOFT STALL: 3 seconds. The mover is likely trying to jump/vault or repath.
                     // We don't abort here anymore; we let the mover handle it.
                     // HARD STALL: 10 seconds. Even with remediation, we haven't made progress. Give up.
                     if (timeSinceStart > 10.0f)
                     {
                        if (Plugin.DebugLogging.Value && !_state.LoggedStall) 
                        {
                            Plugin.Log.LogWarning($"[VultureMoveLogic] {BotOwner.Profile.Nickname} HARD STALL for {timeSinceStart:F1}s. (Dist to Ambush: {Vector3.Distance(BotOwner.Position, _state.AmbushPos):F1}m).");
                            _state.LoggedStall = true;
                        }
                        
                        // GRACEFUL RECOVERY: If we are close enough (<15m), just start searching the area.
                        // This behaves like a human who reached "roughly" the right spot and got stuck on a small prop.
                        if (Vector3.Distance(BotOwner.Position, _state.AmbushPos) < 15f)
                        {
                             if (Plugin.DebugLogging.Value) Plugin.Log.LogInfo($"[VultureMoveLogic] {BotOwner.Profile.Nickname} stall near destination. Shifting to Search.");
                             _state.Phase = VulturePhase.Search;
                             _state.PhaseChanged = true;
                             _state.IsMoving = false;
                        }
                        else
                        {
                            _state.ShouldStop = true;
                        }
                        return;
                     }
                }

                // Arrival Check (Vertical-Safe backstop)
                float arrivalDist = Vector3.Distance(BotOwner.Position, _state.AmbushPos);
                if (_state.Mover.HasArrived || arrivalDist < 1.0f)
                {
                    _state.IsMoving = false;
                    _state.IdleStartTime = Time.time;
                    _state.Phase = VulturePhase.Hold;
                    _state.PhaseChanged = true;

                    try
                    {
                        _state.Mover.SetSprint(false);
                        BotOwner.Mover.Sprint(false);
                        BotOwner.Mover.SetTargetMoveSpeed(0f);
                        BotOwner.GetPlayer.EnableSprint(false);
                        BotOwner.GetPlayer.Move(Vector2.zero);
                    }
                    catch { }

                    float arrivalPose = 0.1f;
                    if (_state.AmbushCoverHeight == Luc1dShadow.Vulture.AI.VultureCoverValidator.CoverheightType.High)
                    {
                       arrivalPose = 1.0f;
                    }
                    BotOwner.SetPose(arrivalPose);

                    if (_state.CombatEvent != null)
                        try { BotOwner.Steering.LookToPoint(_state.CombatEvent.Value.Position); } catch { }
                    else
                        try { BotOwner.Steering.LookToPoint(_state.AmbushPos + (BotOwner.LookDirection * 10f)); } catch { }

                    if (Plugin.DebugLogging.Value) Plugin.Log.LogInfo($"[VultureMoveLogic] {BotOwner.Profile.Nickname} arrived at ambush. -> Hold");
                    return;
                }

                // Timeout
                if (Time.time - _state.MoveStartTime > 90f)
                {
                    _state.IsMoving = false;
                    _state.Mover.Stop();
                    _state.ShouldStop = true;
                    if (Plugin.DebugLogging.Value) Plugin.Log.LogWarning($"[VultureMoveLogic] {BotOwner.Profile?.Nickname ?? "Unknown"} - Movement timeout.");
                }
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError($"[VultureMoveLogic] CRASH in Update: {ex}");
                _state.ShouldStop = true;
            }
        }

        private void UpdateMovementStance()
        {
            float distRemaining = (BotOwner.Position - _state.AmbushPos).magnitude;
            bool shouldSprint = true;

            if (Plugin.SilentApproach.Value)
            {
                var combatEvent = CombatSoundListener.GetNearestEvent(BotOwner.Position, 9999f);
                bool silenceTriggered = (combatEvent != null && (Time.time - combatEvent.Value.Time > Plugin.SilenceTriggerDuration.Value));
                if (!silenceTriggered && distRemaining < Plugin.SilentApproachDistance.Value && distRemaining > 2f)
                {
                    shouldSprint = false;
                }
                else if (silenceTriggered && _state.IsCreeping)
                {
                    shouldSprint = true;
                }
            }

            // HYSTERESIS
            if (shouldSprint != _lastSprintState)
            {
                if (Time.time - _lastStateChangeTime < 3.0f) shouldSprint = _lastSprintState;
                else
                {
                    _lastStateChangeTime = Time.time;
                    _lastSprintState = shouldSprint;
                }
            }

            if (shouldSprint)
            {
                // FATIGUE CHECK: Force walk if out of stamina
                if (_state.IsStaminaDepleted)
                {
                    shouldSprint = false;
                }
            }

            if (shouldSprint)
            {
                // INDOOR SPRINT LOCK: Force walking if indoors unless under fire (survival instinct)
                bool isIndoors = BotOwner.AIData.EnvironmentId != 0;
                bool isUnderFire = BotOwner.Memory.IsUnderFire;

                if (isIndoors && !isUnderFire)
                {
                    shouldSprint = false;
                }
                
                _state.IsCreeping = !shouldSprint;
                _state.Mover.SetSprint(shouldSprint);
                
                if (shouldSprint)
                {
                    // SPRINT: Full speed
                    _state.Mover.SetTargetSpeed(1.0f);
                    BotOwner.SetPose(1.0f);
                }
                else
                {
                    // BRISK WALK: Indoor transition
                    _state.Mover.SetTargetSpeed(0.6f); 
                    BotOwner.SetPose(0.9f);
                }
            }
            else
            {
                if (!_state.IsCreeping)
                {
                    _state.IsCreeping = true;
                }

                _state.Mover.SetSprint(false);
                
                // CREEP: Silent Approach or Stealth
                _state.Mover.SetTargetSpeed(0.25f);
                
                // If fatigued, stand up to recover faster unless under immediate threat
                if (_state.IsStaminaDepleted && !BotOwner.Memory.IsUnderFire)
                {
                    BotOwner.SetPose(0.9f); // Brisk walk height
                }
                else
                {
                    BotOwner.SetPose(0.6f); // Creep height
                }

                // Flashlight Discipline
                if (Plugin.FlashlightDiscipline.Value)
                {
                    var botLight = BotOwner.BotLight;
                    if (botLight != null && botLight.IsEnable)
                    {
                        botLight.TurnOff(false, true);
                    }
                }
            }
        }
    }
}
