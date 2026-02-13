using DrakiaXYZ.BigBrain.Brains;
using EFT;
using UnityEngine;
using Luc1dShadow.Vulture.AI;

namespace Luc1dShadow.Vulture
{
    /// <summary>
    /// Handles ambush holding behavior. Includes paranoia scanning, baiting, and greed timer.
    /// EnterBy: "Ambush Hold"
    /// </summary>
    public class VultureHoldLogic : CustomLogic
    {
        private VultureBotState _state;
        private float _nextLookTime;
        private float _nextValidationTime;

        public VultureHoldLogic(BotOwner botOwner) : base(botOwner) { }

        public override void Start()
        {
            _state = VultureLayer.GetOrCreateState(BotOwner);
            _nextValidationTime = Time.time + 10f;
            if (Plugin.DebugLogging.Value) Plugin.Log.LogInfo($"[VultureHoldLogic] Start for {BotOwner.Profile.Nickname}");
        }

        public override void Stop() { }

        public override void Update(CustomLayer.ActionData data)
        {
            if (_state == null) return;

            try
            {
                // PERIODIC VALIDATION: Check if survivors still exist in the area.
                // If not, we can abort early instead of waiting the full duration.
                if (Time.time > _nextValidationTime)
                {
                    _nextValidationTime = Time.time + 10f;
                    Vector3 targetPos = _state.CombatEvent?.Position ?? _state.AmbushPos;
                    
                    // NARROWED RADIUS: 40m for hold validation to avoid "holding forever" for distant unrelated fights.
                    // If area is clear, we transition to Greed (Search/Scavenge) instead of Aborting.
                    // This handles the "Neutralized" target case naturally via the survivor check.
                    if (!VulturePathUtil.IsSurvivorAliveInArea(targetPos, 40f, BotOwner))
                    {
                        if (Plugin.DebugLogging.Value) Plugin.Log.LogInfo($"[VultureHoldLogic] {BotOwner.Profile.Nickname} - Area clear (40m). Pushing Greed.");
                        TryGreedOrAbort();
                        return;
                    }
                }

                // SQUAD SYNC: Check if team is pushing
                if (!_state.IsGreeding && VultureLayer.IsSquadGreeding(BotOwner))
                {
                    if (Plugin.DebugLogging.Value) Plugin.Log.LogInfo($"[VultureHoldLogic] Squad is pushing! syncing Greed for {BotOwner.Profile.Nickname}");
                    ActivateGreedMode();
                    return;
                }

                // Greed Mode Trigger — ambush timer expired (based on arrival time, not pathing start)
                if (Plugin.LootGreed.Value && !_state.IsGreeding && Time.time - _state.IdleStartTime > Plugin.AmbushDuration.Value)
                {
                    TryGreedOrAbort();
                    return;
                }

                // Max Hold Time
                if (Time.time - _state.IdleStartTime > _state.MaxIdleTime)
                {
                    _state.ShouldStop = true;
                    return;
                }

                // Set posture based on cover height
                float targetPose = 0.1f; // Default to deep crouch (Low/None)
                if (_state.AmbushCoverHeight == Luc1dShadow.Vulture.AI.VultureCoverValidator.CoverheightType.High)
                {
                   targetPose = 1.0f; // Stand for high cover (walls/corners) for better mobility/peeking
                }
                BotOwner.SetPose(targetPose);
                try
                {
                    BotOwner.Mover.Sprint(false);
                    BotOwner.Mover.SetTargetMoveSpeed(0f);
                    BotOwner.GetPlayer.EnableSprint(false);
                    BotOwner.GetPlayer.Move(Vector2.zero);
                }
                catch { }

                // Silence fast-forward
                var combatEvent = CombatSoundListener.GetNearestEvent(BotOwner.Position, 9999f);
                if (combatEvent != null)
                {
                    // Validation: If holding for a target that's gone cold (>150s), investigate or abort
                    if (Time.time - combatEvent.Value.Time > 150f && _state.CombatEvent != null) 
                    {
                          if (Vector3.Distance(combatEvent.Value.Position, _state.CombatEvent.Value.Position) < 5f)
                          {
                               if (Plugin.DebugLogging.Value) Plugin.Log.LogInfo("[VultureHoldLogic] Target stale (>150s). Checking for Greed Investigation.");
                               TryGreedOrAbort();
                               return;
                          }
                    }

                    float lastEventTime = combatEvent?.Time ?? 0f;
                    if (combatEvent == null && _state.CombatEvent != null) lastEventTime = _state.CombatEvent.Value.Time;

                    float quietTime = Time.time - Mathf.Max(_state.IdleStartTime, lastEventTime);
                    float silenceThreshold = Plugin.AmbushDuration.Value / 2f;

                    if (quietTime > silenceThreshold)
                    {
                        float remainingTime = Plugin.AmbushDuration.Value - (Time.time - _state.IdleStartTime);
                        if (remainingTime > 10f)
                        {
                             if (Plugin.DebugLogging.Value) Plugin.Log.LogInfo($"[VultureHoldLogic] Area quiet for {quietTime:F1}s (> {silenceThreshold:F1}s). Fast-forwarding to GREED.");
                             _state.IdleStartTime = Time.time - Plugin.AmbushDuration.Value - 1f;
                             return; 
                        }
                    }
                }

                // Baiting
                if (Plugin.EnableBaiting.Value && Time.time > _state.NextBaitTime)
                {
                    _state.NextBaitTime = Time.time + Random.Range(10f, 25f);
                    if (Random.Range(0, 100) < Plugin.BaitingChance.Value) TryBait();
                }

                HandleParanoia();
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError($"[VultureHoldLogic] CRASH in Update: {ex}");
                _state.ShouldStop = true;
            }
        }

        private void HandleParanoia()
        {
            if (!Plugin.Paranoia.Value) return;
            if (Time.time < _state.IdleStartTime + 2.5f) return;

            if (Time.time > _nextLookTime)
            {
                _nextLookTime = Time.time + Random.Range(5f, 9f);
                var targetEvent = _state.CombatEvent ?? CombatSoundListener.GetNearestEvent(BotOwner.Position, 9999f);

                if (Random.value > 0.5f && targetEvent != null)
                {
                    BotOwner.Steering.LookToPoint(targetEvent.Value.Position, 180f);
                }
                else
                {
                    Vector3 randomDir = Random.insideUnitSphere * 10f;
                    randomDir.y = 1.2f; // Horizon look height
                    BotOwner.Steering.LookToDirection(randomDir, 180f);
                }
            }
        }

        private void TryBait()
        {
            if (BotOwner.WeaponManager.HaveBullets)
            {
                Vector3 baitTarget = BotOwner.Position + (BotOwner.LookDirection * 5f) + (Vector3.down * 0.5f);
                BotOwner.Steering.LookToPoint(baitTarget);
                BotOwner.ShootData.Shoot();
                if (Plugin.EnableVoiceLines.Value && Random.value > 0.5f) BotOwner.BotTalk.Say(EPhraseTrigger.Suppress);
            }
        }

        private void TryGreedOrAbort()
        {
            // Use stored event primarily to prevent switching to a new, distant target.
            var combatEvent = _state.CombatEvent;
            
            // Fallback: If no stored event (rare), check recent
            if (combatEvent == null) 
                 combatEvent = CombatSoundListener.GetNearestEvent(BotOwner.Position, 9999f);

            if (combatEvent != null)
            {
                float distToTarget = Vector3.Distance(BotOwner.Position, combatEvent.Value.Position);
                
                // RANGE GATE: Only Greed if within Tier 3 max ambush distance
                if (distToTarget <= Plugin.AmbushTier3.Value)
                {
                    ActivateGreedMode();
                }
                else
                {
                    if (Plugin.DebugLogging.Value) Plugin.Log.LogInfo($"[VultureHoldLogic] {BotOwner.Profile.Nickname} - Target out of Greed range ({distToTarget:F1}m > {Plugin.AmbushTier3.Value}m). Aborting.");
                    _state.ShouldStop = true;
                }
            }
            else
            {
                _state.ShouldStop = true;
            }
        }

        private void ActivateGreedMode()
        {
            _state.IsGreeding = true;
            
            // Use stored event or find one
            var combatEvent = _state.CombatEvent ?? CombatSoundListener.GetNearestEvent(BotOwner.Position, 9999f);
            
            if (combatEvent != null)
            {
                if (!VulturePathUtil.TryFindGreedTarget(BotOwner, _state.Path, combatEvent.Value.Position, out Vector3 target))
                {
                    _state.IsGreeding = false;
                    _state.ShouldStop = true;
                    return;
                }

                // Release the previous ambush point claim before taking a new one (Greed push)
                Luc1dShadow.Vulture.AI.VultureMapUtil.ReleasePoint(_state.AmbushPos);

                _state.AmbushPos = target;
                _state.Mover.SetSprint(true);
                if (_state.Mover.MoveTo(target, 1.0f))
                {
                    _state.IsMoving = true;
                    _state.MoveStartTime = Time.time;
                    BotOwner.SetPose(1f);
                    _state.Phase = VulturePhase.Greed;
                    _state.PhaseChanged = true;
                    
                    // SYNC: Tell squad we are going in!
                    VultureLayer.ReportGreed(BotOwner);
                    
                    if (Plugin.DebugLogging.Value) Plugin.Log.LogInfo($"[Vulture] GREEDING to {target}");
                }
                else
                {
                    _state.IsGreeding = false;
                    _state.ShouldStop = true;
                }
            }
            else
            {
                _state.IsGreeding = false;
                _state.ShouldStop = true;
            }
        }
    }
}

