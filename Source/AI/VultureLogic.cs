using DrakiaXYZ.BigBrain.Brains;
using EFT;
using UnityEngine;
using UnityEngine.AI;

namespace Luc1dShadow.Vulture
{
    public class VultureLogic : CustomLogic
    {
        private Vector3 _ambushPos;
        private bool _isMoving;
        private float _moveStartTime;
        private float _logicStartTime;
        private bool _isGreeding;
        private bool _isCreeping;
        private bool _targetIsExplosion;

        private float _nextBaitTime;

        public VultureLogic(BotOwner botOwner) : base(botOwner)
        {
        }

        public override void Start()
        {
            // Find target (re-query as Layer decided to start)
            var combatEvent = CombatSoundListener.GetNearestEvent(BotOwner.Position, MapSettings.GetEffectiveRange());
            if (combatEvent != null)
            {
                _targetIsExplosion = combatEvent.Value.IsExplosion;
                _ambushPos = CalculateAmbushPoint(combatEvent.Value.Position);
                _isMoving = true;
                _moveStartTime = Time.time;
                _logicStartTime = Time.time;
                _isGreeding = false;
                
                // Initialize Bait Timer
                _nextBaitTime = Time.time + UnityEngine.Random.Range(5f, 15f); // Wait a bit before first bait
                
                // Move
                BotOwner.GoToPoint(_ambushPos, true);
                
                if (Plugin.DebugLogging.Value)
                {
                    string eventType = _targetIsExplosion ? "explosion" : "shot";
                    Plugin.Log.LogInfo($"[Vulture] Bot {BotOwner.Profile.Nickname} moving to ambush {eventType} at {_ambushPos}");
                }

                // Voice Line (Immersion)
                if (Plugin.EnableVoiceLines.Value)
                {
                    // "OnFight" signals they are entering combat/acknowledging the fight
                    BotOwner.BotTalk.Say(EPhraseTrigger.OnFight);
                    if (Plugin.DebugLogging.Value) Plugin.Log.LogInfo($"[Vulture] {BotOwner.Profile.Nickname} triggered Voice Line: OnFight");
                }

                // Flashlight Discipline (Immersion)
                if (Plugin.FlashlightDiscipline.Value)
                {
                    if (BotOwner.BotLight != null && BotOwner.BotLight.IsEnable)
                    {
                         BotOwner.BotLight.TurnOff(false, true);
                         if (Plugin.DebugLogging.Value) Plugin.Log.LogInfo($"[Vulture] {BotOwner.Profile.Nickname} active Flashlight Discipline (Lights OFF).");
                    }
                }
            }
            else
            {
                // Should not happen if Layer checked correctly, but handling anyway
                _isMoving = false;
            }
        }

        public override void Stop()
        {
            _isMoving = false;
            BotOwner.StopMove();
        }

        public override void Update(CustomLayer.ActionData data)
        {
            // Check for Greed Mode
            if (Plugin.LootGreed.Value && !_isGreeding && Time.time - _logicStartTime > Plugin.AmbushDuration.Value)
            {
                 // Start Greeding
                 _isGreeding = true;
                 
                 // Get the event again
                 var combatEvent = CombatSoundListener.GetNearestEvent(BotOwner.Position, 9999f); 
                 if (combatEvent != null)
                 {
                     // Push exact pos
                      BotOwner.GoToPoint(combatEvent.Value.Position, true);
                      BotOwner.SetPose(1f); // Stand up for the push
                      BotOwner.Mover.SetTargetMoveSpeed(0.8f); // Fast walk / run
                      _isMoving = true;
                      _ambushPos = combatEvent.Value.Position; // Update target
                      
                      if (Plugin.DebugLogging.Value) Plugin.Log.LogInfo($"[Vulture] Bot {BotOwner.Profile.Nickname} GREEDING to {combatEvent.Value.Position}");
                 }
                 else 
                 {
                      if (Plugin.DebugLogging.Value) Plugin.Log.LogInfo($"[Vulture] Bot {BotOwner.Profile.Nickname} wanted to Greed but lost event info.");
                 }
            }
            
            // If Greeding, just let them move. 
            // If normal ambush and moving, handle Silent Approach.

            if (_isGreeding)
            {
                // Simple move logic for greed
                if (!_isMoving && (BotOwner.Position - _ambushPos).sqrMagnitude < 2f)
                {
                    // Arrived at greed spot
                     if (Plugin.DebugLogging.Value) Plugin.Log.LogInfo($"[Vulture] Bot {BotOwner.Profile.Nickname} finished GREED push.");
                }
                return;
            }

            if (!_isMoving)
            {
                // Holding Ambush Position
                
                // Silence Trigger during Hold: If combat has gone silent, reduce remaining ambush time
                var combatEvent = CombatSoundListener.GetNearestEvent(BotOwner.Position, 9999f);
                if (combatEvent != null)
                {
                    float timeSinceEvent = Time.time - combatEvent.Value.Time;
                    if (timeSinceEvent > Plugin.SilenceTriggerDuration.Value)
                    {
                        // Combat has gone silent - fast-forward to Greed by adjusting start time
                        // This effectively makes the remaining ambush time = 0
                        float remainingTime = Plugin.AmbushDuration.Value - (Time.time - _logicStartTime);
                        if (remainingTime > 5f) // Only if significant time remaining
                        {
                            _logicStartTime = Time.time - Plugin.AmbushDuration.Value + 5f; // Leave 5s buffer
                            if (Plugin.DebugLogging.Value) Plugin.Log.LogInfo($"[Vulture] {BotOwner.Profile.Nickname} SILENCE during hold - reducing ambush timer to push early.");
                        }
                    }
                }
                
                // Handle Baiting
                if (Plugin.EnableBaiting.Value && Time.time > _nextBaitTime)
                {
                    _nextBaitTime = Time.time + UnityEngine.Random.Range(10f, 25f); // Cooldown
                    
                    if (UnityEngine.Random.Range(0, 100) < Plugin.BaitingChance.Value)
                    {
                        TryBait();
                    }
                }
                
                HandleParanoia(data);
                return;
            }
            
            // Dynamic Courage (Fear Factor)
            // Check intensity in the area we are heading to (Ambush Position) or current position? 
            // Current position is safer logic (am I in a hot zone?), but target position logic is "is it too hot to push?"
            // Let's check intensity at our current location to simulate "suppression" or fear of nearby chaos.
            int localIntensity = CombatSoundListener.GetEventIntensity(BotOwner.Position, 50f, 5f);
            if (localIntensity > Plugin.CourageThreshold.Value)
            {
                // Too scary! Wait.
                BotOwner.StopMove();
                if (Plugin.DebugLogging.Value) Plugin.Log.LogInfo($"[Vulture] {BotOwner.Profile.Nickname} HESITATING. Intensity {localIntensity} > {Plugin.CourageThreshold.Value}");
                
                // Optional: Crouch low
                BotOwner.SetPose(0.1f);
                
                // Don't proceed with movement logic this frame
                return; 
            }
            else
            {
                // If we were stopped, resume? 
                // GoToPoint should handle this if called again, but we might need to re-enable movement if we manually stopped it.
                // For now, next frame will just hit the movement block below.
            }

            // 1. Silent Approach (Creep last X meters)
            if (Plugin.SilentApproach.Value)
            {
               // Check Silence Trigger (Post-Fight Rush)
               // Only valid if we have a valid combat event time
               var combatEvent = CombatSoundListener.GetNearestEvent(BotOwner.Position, 9999f);
               bool silenceTriggered = false;
               
               if (combatEvent != null)
               {
                   float timeSinceEvent = UnityEngine.Time.time - combatEvent.Value.Time;
                   if (timeSinceEvent > Plugin.SilenceTriggerDuration.Value)
                   {
                       silenceTriggered = true;
                   }
               }
               
               float distRemaining = (BotOwner.Position - _ambushPos).magnitude;
               if (!silenceTriggered && distRemaining < Plugin.SilentApproachDistance.Value && distRemaining > 2f)
               {
                   BotOwner.Mover.SetTargetMoveSpeed(0.2f); 
                   BotOwner.SetPose(0.6f); // Lower stance (not full crawl)
                   
                   if (!_isCreeping)
                   {
                        _isCreeping = true;
                        if (Plugin.DebugLogging.Value) Plugin.Log.LogInfo($"[Vulture] {BotOwner.Profile.Nickname} entering SILENT approach (Creep).");
                   }
               }
               else if (silenceTriggered && _isCreeping)
               {
                   // We were creeping, but now it's been too quiet. RUSH!
                   _isCreeping = false;
                   BotOwner.Mover.SetTargetMoveSpeed(1.0f); // Sprint/Run
                   BotOwner.SetPose(1.0f); // Stand up
                   
                   if (Plugin.DebugLogging.Value) Plugin.Log.LogInfo($"[Vulture] {BotOwner.Profile.Nickname} SILENCE TRIGGERED! Switched to RUSH.");
               }
            }


            // Check if arrived
            float dist = (BotOwner.Position - _ambushPos).sqrMagnitude;
            if (dist < 2f * 2f) // 2m tolerance
            {
                _isMoving = false;
                BotOwner.SetPose(0.1f); // Deep Crouch/Prone? 0.5 is good crouch.
                if (Plugin.DebugLogging.Value)
                     Plugin.Log.LogInfo($"[Vulture] Bot {BotOwner.Profile.Nickname} arrived at ambush point. Crouching.");
            }
            
            // Timeout if taking too long to move?
            if (Time.time - _moveStartTime > 90f) // Increased to 90s for slow creep
            {
                 _isMoving = false; // Give up moving
            }
        }

        private float _nextLookTime;
        private void HandleParanoia(CustomLayer.ActionData data)
        {
            if (!Plugin.Paranoia.Value) return;

            if (Time.time > _nextLookTime)
            {
                _nextLookTime = Time.time + UnityEngine.Random.Range(3f, 6f);
                
                // Look somewhat towards the combat event, but randomized
                 var combatEvent = CombatSoundListener.GetNearestEvent(BotOwner.Position, MapSettings.GetEffectiveRange());
                 if (combatEvent != null)
                 {
                     Vector3 dir = (combatEvent.Value.Position - BotOwner.Position).normalized;
                     // Randomize direction by +/- 45 degrees
                     float angle = UnityEngine.Random.Range(-45f, 45f);
                     Vector3 lookDir = Quaternion.Euler(0, angle, 0) * dir;
                     Vector3 lookPos = BotOwner.Position + lookDir * 10f;
                     
                     BotOwner.Steering.LookToPoint(lookPos);
                     if (Plugin.DebugLogging.Value) Plugin.Log.LogInfo($"[Vulture] {BotOwner.Profile.Nickname} Paranoia check (Head Swivel).");
                 }
            }
        }
        
        private void TryBait()
        {
            // Simple bait: Turn slightly and fire a shot into the ground/air
            // User requested firing shots while holding ambush.
            
            if (BotOwner.WeaponManager.HaveBullets)
            {
                // Aim at a random point nearby (e.g. into a wall or ground)
                // We don't want to hit invalid geometry, so keep it close to ground level but away
                Vector3 baitTarget = BotOwner.Position + (BotOwner.LookDirection * 5f) + (Vector3.down * 0.5f);
                
                // Force look
                BotOwner.Steering.LookToPoint(baitTarget);
                
                // Shoot (One tap)
                BotOwner.ShootData.Shoot();
                
                if (Plugin.DebugLogging.Value) Plugin.Log.LogInfo($"[Vulture] {BotOwner.Profile.Nickname} fired a BAIT shot.");
                
                // Add a "Suppression" line to sell the fake fight
                if (Plugin.EnableVoiceLines.Value && UnityEngine.Random.value > 0.5f)
                {
                    BotOwner.BotTalk.Say(EPhraseTrigger.Suppress);
                }
            }
        }

        private Vector3 CalculateAmbushPoint(Vector3 targetPos)
        {
            Vector3 botPos = BotOwner.Position;
            Vector3 dir = (botPos - targetPos).normalized;
            if (dir == Vector3.zero) dir = Random.insideUnitSphere.normalized;

            float min = Plugin.AmbushDistanceMin.Value;
            float max = Plugin.AmbushDistanceMax.Value;
            Vector3 idealPos = targetPos + (dir * Random.Range(min, max));

            // Smart Ambush: Find nearby cover instead of open ground
            if (Plugin.SmartAmbush.Value)
            {
                // Try to find a cover point near idealPos
                // Using 1000 as maxSqrDist (30m roughly sqr is 900)
                var coverPoint = BotOwner.Covers.GetClosestPoint(idealPos, null, false, 2500);
                if (coverPoint != null)
                {
                     if (Plugin.DebugLogging.Value) Plugin.Log.LogInfo($"[Vulture] Smart Ambush found cover at {coverPoint.Position} (Ideal: {idealPos})");
                     return coverPoint.Position;
                }
            }

            if (NavMesh.SamplePosition(idealPos, out var hit, 10f, NavMesh.AllAreas))
            {
                return hit.position;
            }
            return botPos; // Fallback
        }
    }
}
