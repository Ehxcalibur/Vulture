using DrakiaXYZ.BigBrain.Brains;
using EFT;
using UnityEngine;
using UnityEngine.AI;
using Luc1dShadow.Vulture.AI;

namespace Luc1dShadow.Vulture
{
    /// <summary>
    /// Handles greed push movement toward combat zone after ambush timer expires.
    /// EnterBy: "Greed Push"
    /// </summary>
    public class VultureGreedLogic : CustomLogic
    {
        private VultureBotState _state;

        public VultureGreedLogic(BotOwner botOwner) : base(botOwner) { }

        public override void Start()
        {
            _state = VultureLayer.GetOrCreateState(BotOwner);
            if (Plugin.DebugLogging.Value) Plugin.Log.LogInfo($"[VultureGreedLogic] Start for {BotOwner.Profile.Nickname}");
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
                        if (Plugin.DebugLogging.Value) Plugin.Log.LogInfo($"[VultureGreedLogic] {BotOwner.Profile.Nickname} recovery from suppression.");
                    }
                    return;
                }
                else if (localIntensity > Plugin.CourageThreshold.Value)
                {
                    _state.IsSuppressed = true;
                    _state.SuppressionTimer = Time.time + 5.0f; // Minimum 5s suppression
                    BotOwner.StopMove();
                    BotOwner.SetPose(0.1f);
                    if (Plugin.DebugLogging.Value) Plugin.Log.LogInfo($"[VultureGreedLogic] {BotOwner.Profile.Nickname} suppressed by intensity {localIntensity}.");
                    return;
                }

                // Decide sprint based on environment
                bool isIndoors = BotOwner.AIData.EnvironmentId != 0;
                bool isUnderFire = BotOwner.Memory.IsUnderFire;
                bool shouldSprint = (!isIndoors || isUnderFire) && !_state.IsStaminaDepleted;

                _state.Mover.SetSprint(shouldSprint);
                if (shouldSprint)
                {
                    _state.Mover.SetTargetSpeed(1.0f);
                    BotOwner.SetPose(1.0f);
                }
                else
                {
                    // If fatigued and not under fire, stand up to recover faster
                    if (_state.IsStaminaDepleted && !isUnderFire)
                    {
                        _state.Mover.SetTargetSpeed(0.5f);
                        BotOwner.SetPose(0.9f); // Brisk walk height
                    }
                    else
                    {
                        _state.Mover.SetTargetSpeed(0.5f);
                        BotOwner.SetPose(0.8f);
                    }
                }

                // Advanced Steering: Look-Ahead Smoothing
                if (_state.Mover.IsSprinting || _state.Mover.IsMoving)
                {
                    // Dynamic Look-Ahead with Horizon Alignment
                    Vector3 lookPoint = _state.Mover.GetLookAheadPoint(1.5f);
                    
                    // Origin should be the bot's head
                    Vector3 headPos = BotOwner.GetPlayer.MainParts[BodyPartType.head].Position;
                    
                    // Vector from head to target
                    Vector3 lookDir = (lookPoint - headPos).normalized;
                    
                    // Force gaze to be horizontal during movement
                    lookDir.y = 0;
                    
                    BotOwner.Steering.LookToDirection(lookDir, 540f);
                }

                _state.Mover.Update(true);

                // Movement Check
                if (!_state.Mover.IsMoving && !_state.Mover.HasArrived)
                {
                     if (Time.time - _state.MoveStartTime > 1.5f) 
                     {
                        if (Plugin.DebugLogging.Value) Plugin.Log.LogWarning($"[VultureGreedLogic] {BotOwner.Profile.Nickname} movement stopped prematurely. Aborting.");
                        _state.ShouldStop = true;
                        return;
                     }
                }

                // Arrival
                if (_state.Mover.HasArrived)
                {
                    HandleGreedArrival();
                    return;
                }

                // Timeout
                if (Time.time - _state.MoveStartTime > 90f)
                {
                    _state.IsMoving = false;
                    _state.IsGreeding = false;
                    _state.Mover.Stop();
                    _state.ShouldStop = true;
                    if (Plugin.DebugLogging.Value) Plugin.Log.LogWarning($"[VultureGreedLogic] {BotOwner.Profile?.Nickname ?? "Unknown"} - Greed movement timeout.");
                }
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError($"[VultureGreedLogic] CRASH in Update: {ex}");
                _state.ShouldStop = true;
            }
        }

        private void HandleGreedArrival()
        {
            _state.IsGreeding = false;
            _state.IsMoving = false;

            if (BotOwner.Memory.HaveEnemy || BotOwner.Memory.IsUnderFire)
            {
                _state.ShouldStop = true;
                return;
            }

            if (VulturePathUtil.TryFindNearestDeadBody(BotOwner, out Vector3 bodyPos))
            {
                if (VulturePathUtil.TryFindCoverWithLOSToBody(BotOwner, _state.Path, bodyPos, out Vector3 coverPos, out VultureCoverValidator.CoverheightType heightType))
                {
                    _state.PostGreedBodyPosition = bodyPos;
                    _state.IsPostGreedHolding = true;
                    _state.PostGreedHoldStartTime = Time.time;
                    _state.AmbushPos = coverPos;
                    _state.AmbushCoverHeight = heightType;
                    _state.IsScanning = false;

                    _state.Mover.SetSprint(false);
                    if (_state.Mover.MoveTo(coverPos, 0.5f))
                    {
                        _state.IsMoving = true;
                        _state.MoveStartTime = Time.time;
                        _state.Phase = VulturePhase.PostGreed;
                        _state.PhaseChanged = true;
                        if (Plugin.DebugLogging.Value) Plugin.Log.LogInfo($"[VultureGreedLogic] {BotOwner.Profile.Nickname} found body, moving to 8-12m cover.");
                    }
                    else
                    {
                        _state.IsPostGreedHolding = false;
                        _state.ShouldStop = true;
                    }
                }
                else
                {
                    // No cover with LOS, transition to search anyway to be proactive
                    TransitionToSearch();
                }
            }
            else
            {
                // No body found, start search/clear phase
                TransitionToSearch();
            }
        }

        private void TransitionToSearch()
        {
            if (Plugin.DebugLogging.Value) Plugin.Log.LogInfo($"[VultureGreedLogic] {BotOwner.Profile.Nickname} found no body. Transitioning to SEARCH.");
            
            // Pick a few search points around the event
            _state.SearchPoints.Clear();
            var eventPos = _state.CombatEvent?.Position ?? BotOwner.Position;
            
            for (int i = 0; i < 7; i++)
            {
                // Generate points in a tight 8-15m ring around the event
                Vector3 randomOffset = Random.insideUnitSphere * 15f;
                if (randomOffset.magnitude < 8f) randomOffset = randomOffset.normalized * 8f;
                randomOffset.y = 0;
                
                if (NavMesh.SamplePosition(eventPos + randomOffset, out NavMeshHit hit, 5f, NavMesh.AllAreas))
                {
                    _state.SearchPoints.Add(hit.position);
                }
            }

            if (_state.SearchPoints.Count > 0)
            {
                _state.CurrentSearchPointIndex = 0;
                _state.Phase = VulturePhase.Search;
                _state.PhaseChanged = true;
            }
            else
            {
                _state.ShouldStop = true;
            }
        }
    }
}
