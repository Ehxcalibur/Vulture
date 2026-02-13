using DrakiaXYZ.BigBrain.Brains;
using EFT;
using Luc1dShadow.Vulture.Integration;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;
using Luc1dShadow.Vulture.AI;

namespace Luc1dShadow.Vulture
{
    public class VultureLayer : CustomLayer
    {
        private bool _isActive;
#pragma warning disable CS0414
        private bool _isAirdropVulture; 
#pragma warning restore CS0414
        private float _nextPossibleVultureTime;
        private Vector3 _currentTargetPosition;
        
        private bool _loggedCombatSuppression;
        private bool _loggedDisabledByRole;
        private float _lastYieldTime; // New: Hysteresis for combat yielding
        
        private bool _lastIsActive;
        private int _lastIsActiveFrame = -1;
        
        // State Persistence
        private static Dictionary<int, AI.VultureBotState> _botStates = new Dictionary<int, AI.VultureBotState>();
        
        public static AI.VultureBotState GetOrCreateState(BotOwner bot)
        {
            if (!_botStates.TryGetValue(bot.Id, out var state))
            {
                state = new AI.VultureBotState(bot);
                _botStates[bot.Id] = state;
            }
            return state;
        }

        public static bool IsVulture(BotOwner bot)
        {
            if (_botStates.TryGetValue(bot.Id, out var state))
            {
                return state.IsVultureActive;
            }
            return false;
        }

        public static void RemoveState(BotOwner bot)
        {
             if (_botStates.TryGetValue(bot.Id, out var state))
             {
                if (state.AmbushPos != Vector3.zero)
                {
                     Luc1dShadow.Vulture.AI.VultureMapUtil.ReleasePoint(state.AmbushPos);
                }
                 state.Dispose();
                 _botStates.Remove(bot.Id);
             }
        }

        public static void ClearCache()
        {
            foreach (var state in _botStates.Values)
            {
                state.Dispose();
            }
            _botStates.Clear();
            _squadVultureTargets.Clear();
            _storedTargets.Clear();
            if (Plugin.DebugLogging.Value) Plugin.Log.LogInfo("[VultureLayer] Cache cleared.");
        }
        
        private float _activationTime;
        
        // Static dictionary to share vulture targets between squad members
        private static Dictionary<string, (Vector3 Position, string TargetId)> _squadVultureTargets = new Dictionary<string, (Vector3, string)>();
        private static Dictionary<Vector3, float> _stalledAmbushPoints = new Dictionary<Vector3, float>();
        private static Dictionary<string, (Vector3 Position, string TargetId, float Expiration)> _storedTargets = new Dictionary<string, (Vector3, string, float)>();
        
        // SQUAD GREED SYNC: Track when a squad leader (or member) initiates Greed
        private static Dictionary<string, float> _squadGreedActivationTimes = new Dictionary<string, float>();

        public static void ReportGreed(BotOwner bot)
        {
            if (bot.BotsGroup == null) return;
            // Use Squad ID or Leader ID as key
            string squadId = bot.BotsGroup.Id.ToString(); 
            _squadGreedActivationTimes[squadId] = Time.time;
        }

        public static bool IsSquadGreeding(BotOwner bot)
        {
            if (bot.BotsGroup == null) return false;
            string squadId = bot.BotsGroup.Id.ToString();
            
            if (_squadGreedActivationTimes.TryGetValue(squadId, out float time))
            {
                // Usage window: 10 seconds. If a teammate greed-ed 20s ago, ignore it.
                return Time.time - time < 10f;
            }
            return false;
        }

        public static void StoreTarget(string profileId, Vector3 target, string targetId, float duration)
        {
             if (string.IsNullOrEmpty(profileId)) return;
             _storedTargets[profileId] = (target, targetId, Time.time + duration);
        }

        public VultureLayer(BotOwner bot, int priority) : base(bot, priority) { }

        public override string GetName() => "Vulture";

        public static bool TryGetStoredTarget(string profileId, out Vector3 target, out string targetId)
        {
            target = Vector3.zero;
            targetId = null;
            if (_storedTargets.TryGetValue(profileId, out var data))
            {
                if (Time.time < data.Expiration)
                {
                    target = data.Position;
                    targetId = data.TargetId;
                    return true;
                }
                _storedTargets.Remove(profileId);
            }
            return false;
        }

        public override Action GetNextAction()
        {
            var state = GetOrCreateState(BotOwner);
            switch (state.Phase)
            {
                case AI.VulturePhase.Initializing:
                    return new Action(typeof(VultureMoveLogic), "Vulture Init");
                case AI.VulturePhase.Move:
                    return new Action(typeof(VultureMoveLogic), "Vulture Move");
                case AI.VulturePhase.Hold:
                    return new Action(typeof(VultureHoldLogic), "Vulture Hold");
                case AI.VulturePhase.Greed:
                    return new Action(typeof(VultureGreedLogic), "Vulture Greed");
                case AI.VulturePhase.PostGreed:
                    return new Action(typeof(VulturePostGreedLogic), "Vulture Post-Greed");
                case AI.VulturePhase.Search:
                    return new Action(typeof(VultureSearchLogic), "Vulture Search");
                default:
                    return new Action(typeof(VultureMoveLogic), "Vulture Default");
            }
        }

        public override bool IsCurrentActionEnding()
        {
            if (_botStates.TryGetValue(BotOwner.Id, out var state))
            {
                // CRITICAL: Handover to BigBrain when internal phase changes
                if (state.PhaseChanged)
                {
                    state.PhaseChanged = false;
                    return true;
                }
            }

              // If under fire or combat suppressed, force yield immediately
              if (_isActive && ShouldYield(out string reason))
              {
                   if (Plugin.DebugLogging.Value) Plugin.Log.LogInfo($"[VultureLayer] {BotOwner.Profile.Nickname} forcing Vulture yield: {reason}");
                   
                   _lastYieldTime = Time.time; // Mark the yield time for hysteresis
                   
                   // If we have a state, tell it to stop
                   if (_botStates.TryGetValue(BotOwner.Id, out state))
                   {
                       state.ShouldStop = true;
                       // Inject threat memory so they don't immediately try to Vulture again in the same spot
                       InjectThreatMemory(reason);
                   }
                   
                   _isActive = false; // Set flag directly
                   return true;
              }

            if (!_isActive) return true;
            
            if (_botStates.TryGetValue(BotOwner.Id, out state))
            {
                if (state.ShouldStop)
                {
                    // BLACKLIST: If the action is ending because of a stall (ShouldStop), 
                    // blacklist the ambush point so we don't immediately re-try the same stuck spot.
                    if (state.IsMoving && state.Mover.IsStuck)
                    {
                        var ambushPos = state.AmbushPos;
                        _stalledAmbushPoints[ambushPos] = Time.time + 60f; // 60s blacklist
                        if (Plugin.DebugLogging.Value)
                            Plugin.Log.LogWarning($"[VultureLayer] Blacklisting stalled ambush point at {ambushPos} for 60s.");
                    }

                    Stop();
                    RemoveState(BotOwner); // Force fresh state for the next activation
                    return true;
                }
                return false;
            }
            
            return true;
        }

        private bool ShouldYield(out string reason)
        {
            reason = "";
            if (BotOwner == null || BotOwner.Memory == null) return false;

            // 1. Combat state check
            if (BotOwner.Memory.IsUnderFire)
            {
                reason = "Under Fire";
                return true; 
            }

            if (BotOwner.Memory.HaveEnemy)
            {
                var enemy = BotOwner.Memory.GoalEnemy;
                if (enemy != null && enemy.Person != null && enemy.Person.HealthController != null && enemy.Person.HealthController.IsAlive)
                {
                    if (enemy.IsVisible || enemy.CanShoot)
                    {
                        reason = "Visible/CanShoot Enemy";
                        return true;
                    }

                    float timeSinceLastSeen = Time.time - enemy.PersonalLastSeenTime;
                    if (timeSinceLastSeen < 15f) // Reduced from 30s to allow faster tactical recovery
                    {
                        reason = $"Active Enemy ({timeSinceLastSeen:F1}s)";
                        return true;
                    }
                }
            }

            // 2. Local Threat Clearance (Dynamic Radius based on phase)
            AI.VultureBotState state = null;
            _botStates.TryGetValue(BotOwner.Id, out state);
            string ignoreId = state?.TargetProfileId;
            
            float threatRadius = 30f; // Default Move radius
            if (state != null && (state.Phase == AI.VulturePhase.Hold || state.Phase == AI.VulturePhase.Greed || state.Phase == AI.VulturePhase.PostGreed))
            {
                threatRadius = 12.5f; // Tactical Hold radius — allow stalked target to be close but unseen
            }

            if (IsLocalThreatPresent(threatRadius, ignoreId, out string threatReason))
            {
                // Re-Activation Dampening
                // If we JUST started Vulturing (< 5s ago), ignore "Soft" threats like nearby shots
                // unless they are extremely close (< 8m).
                bool isSoftThreat = threatReason.Contains("Recent Local Shot");
                if (Time.time < _activationTime + 5f && isSoftThreat)
                {
                     // Double check: Is it REALLY close?
                     if (Plugin.DebugLogging.Value) Plugin.Log.LogInfo($"[VultureLayer] Ignoring Local Threat ({threatReason}) due to Dampening (Active for {Time.time - _activationTime:F1}s)");
                     return false;
                }

                reason = $"Local Threat ({threatReason})";
                return true;
            }

            // 3. Squad Combat Sync
            if (BotOwner.BotsGroup != null)
            {
                for (int i = 0; i < BotOwner.BotsGroup.MembersCount; i++)
                {
                    var member = BotOwner.BotsGroup.Member(i);
                    if (member != null && !member.IsDead && member.Memory.IsUnderFire)
                    {
                         reason = $"Squad Member {member.Profile.Nickname} Under Fire";
                         return true;
                    }
                }
            }

            return false;
        }

        private bool IsLocalThreatPresent(float radius, string ignoreProfileId, out string reason)
        {
            reason = "";
            float radiusSq = radius * radius;

            // A. Check for any non-friendly players nearby
            var gameWorld = Comfort.Common.Singleton<GameWorld>.Instance;
            if (gameWorld != null)
            {
                foreach (var player in gameWorld.AllAlivePlayersList)
                {
                    if (player == null || player.ProfileId == BotOwner.ProfileId) continue;
                    
                    // TARGET FILTER: Ignore the specific profile we are stalking for proximity checks
                    if (ignoreProfileId != null && player.ProfileId == ignoreProfileId) continue;

                    // Is it an enemy or potential threat?
                    // We use side comparison to avoid direct Player object passing to avoid build-time dependency issues.
                    if (BotOwner.BotsGroup != null && player.Profile.Side != BotOwner.Profile.Side)
                    {
                        if ((player.Position - BotOwner.Position).sqrMagnitude < radiusSq)
                        {
                            reason = $"Enemy Close ({player.Profile.Nickname})";
                            return true;
                        }
                    }
                }
            }

            // B. Check for recently fired shots nearby (Near Miss)
            float nearMissBound = Mathf.Min(radius, Plugin.NearMissRadius.Value); 
            // Use strict 10s window and ignore self/squad events
            var nearEvent = CombatSoundListener.GetNearestEvent(BotOwner.Position, nearMissBound, 10f, BotOwner.ProfileId, BotOwner.BotsGroup);

            if (nearEvent != null)
            {
                // Safety: If the near-event is the target we are stalking, don't yield on proximity alone
                if (ignoreProfileId != null && nearEvent.Value.ShooterProfileId == ignoreProfileId) return false;

                reason = $"Recent Local Shot/Explosion";
                return true;
            }

            return false;
        }

        public override bool IsActive()
        {
            try
            {
                // PERFORMANCE: Frame Staggering
                // If not already active, only run full checks every 10 frames.
                if (!_isActive && _lastIsActiveFrame != -1 && (Time.frameCount + BotOwner.Id) % 10 != 0)
                {
                    if (Time.frameCount - _lastIsActiveFrame < 15)
                        return _lastIsActive;
                }

                bool result = IsActiveInternal();
                _lastIsActive = result;
                _lastIsActiveFrame = Time.frameCount;
                return result;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[VultureLayer] IsActive CRASH: {ex}");
                return false;
            }
        }

        private bool IsActiveInternal()
        {
            try
            {
                CleanBlacklist();
                
                // Must have BotOwner
                if (BotOwner == null || BotOwner.Profile == null) return false;

                // Check state
                if (_isActive)
                {
                    return true;
                }

                // CRITICAL: Combat Override & Hysteresis
                // Check this BEFORE fresh event overrides to prevent gunfights from triggering Vulture
                if (BotOwner.Memory == null) return false;

                if (Time.time - _lastYieldTime < 10.0f) return false; // Increased hysteresis (10s) for transition safety

                // HANDOFF SAFETY: Do not activate if bot is juggling weapons/hands
                if (BotOwner.WeaponManager != null && BotOwner.WeaponManager.Selector != null && BotOwner.WeaponManager.Selector.IsChanging)
                {
                    return false;
                }

                // SAIN SAFETY: If SAIN is active, wait until it explicitly releases the bot
                if (SAINIntegration.IsSAINLoaded && !SAINIntegration.IsSAINFinishedWithBot(BotOwner))
                {
                    return false;
                }

                if (ShouldYield(out string suppressionReason))
                {
                     if (Plugin.DebugLogging.Value && !_loggedCombatSuppression)
                     {
                         Plugin.Log.LogInfo($"[VultureLayer] {BotOwner.Profile.Nickname} suppressed by combat ({suppressionReason}).");
                         _loggedCombatSuppression = true;
                     }
                     return false;
                 }
                else
                {
                     _loggedCombatSuppression = false; 
                }
    
                // Cooldown Check with Fresh Event Override
                if (Time.time < _nextPossibleVultureTime)
                {
                    // Check for fresh (<5s), loud event to override wait
                    var freshEvent = CombatSoundListener.RecentEvents.FirstOrDefault(e => Time.time - e.Time < 5f && !e.IsSilenced);
                    if (freshEvent.Time > 0)
                    {
                        // Check if we already have a state without creating one
                        if (_botStates.TryGetValue(BotOwner.Id, out var state))
                        {
                            if (state.LastRolledEventTime >= freshEvent.Time) return false;
                        }

                        float dist = Vector3.Distance(BotOwner.Position, freshEvent.Position);
                        if (dist < MapSettings.GetEffectiveRange() * 0.5f)
                        {
                            // We will proceed to roll below
                        }
                        else return false;
                    }
                    else return false;
                }
    
                bool hasEvents = CombatSoundListener.RecentEvents.Count > 0;
                bool hasAirdrops = Plugin.EnableAirdropVulturing.Value && AirdropListener.ActiveAirdrops.Count > 0;
                
                if (!hasEvents && !hasAirdrops) return false;
    
                // 1. Config/Role Checks
                if (!IsBotEnabled()) 
                {
                    if (Plugin.DebugLogging.Value && hasEvents && !_loggedDisabledByRole) 
                    {
                        Plugin.Log.LogInfo($"[VultureLayer] {BotOwner.Profile.Nickname} disabled by role/config (Role: {BotOwner.Profile.Info.Settings.Role}).");
                        _loggedDisabledByRole = true;
                    }
                    return false;
                }
                else
                {
                     _loggedDisabledByRole = false; 
                }
    
                // 2. Squad Checks
                bool isFollower = BotOwner.BotFollower?.BossToFollow != null;
                BotOwner boss = BotOwner.BotFollower?.BossToFollow as BotOwner;
                
                if (Plugin.SquadCoordination.Value && BotOwner.BotsGroup != null)
                {
                    // A. Follower: Sync with Boss/Leader
                    if (isFollower && boss != null)
                    {
                        if (_squadVultureTargets.TryGetValue(boss.ProfileId, out var bossTarget))
                        {
                            var state = GetOrCreateState(BotOwner);
                            
                            // Check if we recently failed for this specific target position
                            if (state.InitializationFailed && (state.LastFailedTargetPos - bossTarget.Position).sqrMagnitude < 4f)
                            {
                                // If it's been a while, we can try again
                                if (Time.time - state.LastFailedTargetTime < 60f)
                                {
                                    return false; // Stay in base logic if we can't find a spot
                                }
                            }

                            if (!_isActive || Vector3.Distance(_currentTargetPosition, bossTarget.Position) > 1f)
                            {
                                // Check if the leader's target is blacklisted for us
                                if (_stalledAmbushPoints.TryGetValue(bossTarget.Position, out float expiry) && Time.time < expiry)
                                {
                                    return false; // Point is blacklisted, don't sync
                                }

                                _isActive = true;
                                _activationTime = Time.time;
                                _currentTargetPosition = bossTarget.Position;

                                state.ResetLifecycle(); // Reset for clean start BEFORE initialization
                                if (state.InitializeWithTarget(bossTarget.Position, bossTarget.TargetId))
                                {
                                    if (Plugin.DebugLogging.Value)
                                        Plugin.Log.LogInfo($"[VultureLayer] {BotOwner.Profile.Nickname} syncing with Leader {boss.Profile.Nickname} (Target: {bossTarget.TargetId})");
                                }
                                else
                                {
                                     // If initialization failed, IsActive MUST return false to prevent loop
                                     _isActive = false;
                                     return false;
                                }
                            }
                            return true;
                        }
                    }
                    else
                    {
                        // B. Leader: Bottom-Up Propagation 
                        // If the bot is a leader without a target, check if any follower has one
                        foreach (var member in BotOwner.BotsGroup.Members)
                        {
                            if (member == null || member == BotOwner) continue;
                            if (_squadVultureTargets.TryGetValue(member.ProfileId, out var followerTarget))
                            {
                                if (!_isActive)
                                {
                                    _isActive = true;
                                    _activationTime = Time.time;
                                    _currentTargetPosition = followerTarget.Position;
                                    
                                    var state = GetOrCreateState(BotOwner);
                                    
                                    // Check for previous failure on this target
                                    if (state.InitializationFailed && (state.LastFailedTargetPos - followerTarget.Position).sqrMagnitude < 4f)
                                    {
                                         if (Time.time - state.LastFailedTargetTime < 60f) continue;
                                    }

                                    state.ResetLifecycle(); // Reset for clean start BEFORE initialization
                                    if (state.InitializeWithTarget(followerTarget.Position, followerTarget.TargetId))
                                    {
                                        // Broadcast new leader target to everyone else
                                        _squadVultureTargets[BotOwner.ProfileId] = followerTarget;
                                        
                                        if (Plugin.DebugLogging.Value)
                                            Plugin.Log.LogInfo($"[VultureLayer] Leader {BotOwner.Profile.Nickname} adopting follower {member.Profile.Nickname}'s target!");
                                    }
                                    else
                                    {
                                         _isActive = false;
                                         continue;
                                    }
                                }
                                return true;
                            }
                        }
                    }
                }
                
                // Non-leaders with squad size check
                if (BotOwner.BotsGroup != null && BotOwner.BotsGroup.MembersCount < Plugin.MinSquadSize.Value) 
                {
                     if (Plugin.DebugLogging.Value && hasEvents) 
                          Plugin.Log.LogInfo($"[VultureLayer] {BotOwner.Profile.Nickname} ignored: Squad too small ({BotOwner.BotsGroup.MembersCount})");
                     return false;
                }
                
                if (isFollower && !Plugin.SquadCoordination.Value) return false;
    
                // 3. Combat Event Check (shots OR explosions)
                float effectiveRange = MapSettings.GetEffectiveRange();
                // Only react to events from last 90 seconds (Default was 300s/5m which is too old for initiation)
                // Filter out self and squad events to prevent friendly fire panic
                var combatEvent = CombatSoundListener.GetNearestEvent(BotOwner.Position, effectiveRange, 90f, BotOwner.ProfileId, BotOwner.BotsGroup);
                
                if (Plugin.DebugLogging.Value && hasEvents && combatEvent == null)
                {
                    var anyEvent = CombatSoundListener.RecentEvents.Count > 0 ? CombatSoundListener.RecentEvents[0] : (CombatSoundListener.CombatEvent?)null;
                    if (anyEvent != null)
                    {
                        float dist = Vector3.Distance(BotOwner.Position, anyEvent.Value.Position);
                        // Only log periodically to reduce spam? For now, keep it.
                        // FAIL: This spams every frame if an event exists but is slightly out of range.
                        // Plugin.Log.LogInfo($"[VultureLayer] {BotOwner.Profile.Nickname} - event exists at {dist:F0}m but range is {effectiveRange:F0}m");
                    }
                }
                
                if (combatEvent != null)
                {
                    // SQUAD SELF-SHOT FILTER: Don't vulture on our own or squad members' shots
                    string shooterId = combatEvent.Value.ShooterProfileId;
                    if (!string.IsNullOrEmpty(shooterId))
                    {
                        // Check if shooter is self
                        if (shooterId == BotOwner.ProfileId)
                        {
                            combatEvent = null;
                        }
                        // Check if shooter is a squad member
                        else if (BotOwner.BotsGroup != null)
                        {
                            for (int i = 0; i < BotOwner.BotsGroup.MembersCount; i++)
                            {
                                var member = BotOwner.BotsGroup.Member(i);
                                if (member != null && member.ProfileId == shooterId)
                                {
                                    combatEvent = null;
                                    break;
                                }
                            }
                        }
                    }
                }

                if (combatEvent != null)
                {
                    int intensity = CombatSoundListener.GetEventIntensity(
                        combatEvent.Value.Position, 
                        MapSettings.GetEffectiveRange() * 0.5f,
                        Plugin.IntensityWindow.Value
                    );
                    
                    int extraShots = Mathf.Max(0, intensity - 1);
                    int effectiveChance = Mathf.Min(95, Plugin.VultureChance.Value + (extraShots * Plugin.MultiShotIntensity.Value));
                    
                    effectiveChance = Mathf.Clamp(effectiveChance, 0, 100);
    
                    // Personality check (Silent)
                    string personality = "";
                    if (Plugin.SAINEnabled.Value && SAINIntegration.IsSAINLoaded)
                    {
                        personality = SAINIntegration.GetPersonality(BotOwner);
                        if (!string.IsNullOrEmpty(personality))
                        {
                            if (SAINIntegration.IsAggressivePersonality(personality))
                                effectiveChance += Plugin.SAINAggressionModifier.Value;
                            else if (SAINIntegration.IsCautiousPersonality(personality))
                                effectiveChance -= Plugin.SAINCautiousModifier.Value;
                        }
                    }

                    var state = GetOrCreateState(BotOwner);
                    state.LastRolledEventTime = combatEvent.Value.Time;

                    int roll = UnityEngine.Random.Range(0, 100);
                    if (roll > effectiveChance)
                    {
                        _nextPossibleVultureTime = Time.time + 180f;
                        if (Plugin.DebugLogging.Value) 
                            Plugin.Log.LogInfo($"[VultureLayer] {BotOwner.Profile.Nickname} decided to IGNORE combat event (Rolled {roll} > {effectiveChance}%, Intensity: {intensity}). Cooldown active.");
                        
                        return false;
                    }
    
                    if (Plugin.BossAvoidance.Value && CombatSoundListener.IsInBossZone(combatEvent.Value.Position, Plugin.BossAvoidanceRadius.Value))
                    {
                        _nextPossibleVultureTime = Time.time + 60f;
                        if (Plugin.DebugLogging.Value)
                            Plugin.Log.LogInfo($"[VultureLayer] {BotOwner.Profile.Nickname} AVOIDING boss zone!");
                        
                        return false;
                    }

                    // Log Activation Details (Only once upon success)
                    if (Plugin.DebugLogging.Value)
                    {
                        if (Time.time < _nextPossibleVultureTime)
                        {
                            float d = Vector3.Distance(BotOwner.Position, combatEvent.Value.Position);
                            Plugin.Log.LogInfo($"[VultureLayer] {BotOwner.Profile.Nickname} OVERRIDING cooldown due to fresh loud combat ({d:F0}m)");
                        }

                        if (intensity > 1)
                            Plugin.Log.LogInfo($"[VultureLayer] {BotOwner.Profile.Nickname} Multi-Shot Intensity: {intensity} events, calculated chance: {effectiveChance}%");

                        if (!string.IsNullOrEmpty(personality))
                        {
                            if (SAINIntegration.IsAggressivePersonality(personality))
                                Plugin.Log.LogInfo($"[VultureLayer] {BotOwner.Profile.Nickname} ({personality}) is AGGRESSIVE. Bonus +{Plugin.SAINAggressionModifier.Value}% chance.");
                            else if (SAINIntegration.IsCautiousPersonality(personality))
                                Plugin.Log.LogInfo($"[VultureLayer] {BotOwner.Profile.Nickname} ({personality}) is CAUTIOUS. Penalty -{Plugin.SAINCautiousModifier.Value}% chance.");
                        }

                        string eventType = combatEvent.Value.IsExplosion ? "Explosion" : "Shot";
                        Plugin.Log.LogInfo($"[VultureLayer] Bot {BotOwner.Profile.Nickname} activating Vulture! {eventType} at {combatEvent.Value.Position} dist: {Vector3.Distance(BotOwner.Position, combatEvent.Value.Position)}");
                    }
                    
                    _currentTargetPosition = combatEvent.Value.Position;
                    
                    // Get or create state (initialization now happens in Logic Start)
                    state = GetOrCreateState(BotOwner);
                    
                    // If we already failed to initialize for this exact event or position, don't try again.
                    if (state.InitializationFailed)
                    {
                        if (state.LastRolledEventTime >= combatEvent.Value.Time) return false;
                        if ((state.LastFailedTargetPos - combatEvent.Value.Position).sqrMagnitude < 4f) return false;
                    }
                    
                    _isActive = true;
                    _activationTime = Time.time;
                    
                    // Reset lifecycle for clean start (FIX: Carry-over timers/ShouldStop)
                    state.ResetLifecycle();
                    
                    if (Plugin.SquadCoordination.Value && BotOwner.BotsGroup != null)
                    {
                        // BROADCAST: Share the threat position (not our destination) with the squad
                        _squadVultureTargets[BotOwner.ProfileId] = (_currentTargetPosition, state.TargetProfileId);
                        
                        if (Plugin.DebugLogging.Value && BotOwner.BotsGroup.MembersCount > 1)
                            Plugin.Log.LogInfo($"[VultureLayer] {BotOwner.Profile.Nickname} broadcasting threat target to squad.");
                    }
                    
                    return true;
                }
    
                // 4. Airdrop Check (if no combat event triggered)
                if (Plugin.EnableAirdropVulturing.Value && AirdropListener.ActiveAirdrops.Count > 0)
                {
                    var airdrop = AirdropListener.GetNearestAirdrop(BotOwner.Position, Plugin.AirdropDetectionRange.Value);
                    
                    if (airdrop != null)
                    {
                        // Roll for airdrop vulture chance
                        int roll = UnityEngine.Random.Range(0, 100);
                        if (roll > Plugin.AirdropVultureChance.Value)
                        {
                            _nextPossibleVultureTime = Time.time + 120f; // Shorter cooldown for airdrops
                            if (Plugin.DebugLogging.Value) 
                                Plugin.Log.LogInfo($"[VultureLayer] {BotOwner.Profile.Nickname} decided to IGNORE airdrop (Rolled {roll} > {Plugin.AirdropVultureChance.Value}).");
                            
                            return false;
                        }

                        if (Plugin.DebugLogging.Value) 
                            Plugin.Log.LogInfo($"[VultureLayer] Bot {BotOwner.Profile.Nickname} activating AIRDROP Vulture! Airdrop at {airdrop.Value.Position} dist: {Vector3.Distance(BotOwner.Position, airdrop.Value.Position)}");
                        
                        _isAirdropVulture = true;
                        _currentTargetPosition = airdrop.Value.Position;
                        
                        // Initialize state for airdrop target
                        var airdropState = GetOrCreateState(BotOwner);
                        airdropState.ResetLifecycle(); // Reset BEFORE initialization
                        if (!airdropState.InitializeWithTarget(airdrop.Value.Position))
                        {
                            RemoveState(BotOwner);
                            _nextPossibleVultureTime = Time.time + 60f;
                            return false;
                        }
                        
                        _isActive = true;
                        _activationTime = Time.time;
                        
                        if (Plugin.SquadCoordination.Value && BotOwner.BotsGroup != null)
                        {
                            _squadVultureTargets[BotOwner.ProfileId] = (_currentTargetPosition, null); // Airdrops have no specific profile target
                            if (Plugin.DebugLogging.Value && BotOwner.BotsGroup.MembersCount > 1)
                                Plugin.Log.LogInfo($"[VultureLayer] {BotOwner.Profile.Nickname} broadcasting AIRDROP to squad.");
                        }
                        
                        return true;
                    }
                }
                
                // 5. Stored Target Check (Persistence / Resume Vulturing)
                if (TryGetStoredTarget(BotOwner.ProfileId, out Vector3 storedPos, out string storedTargetId))
                {
                     // Verify if any survivors remain in the fight area
                     if (!VulturePathUtil.IsSurvivorAliveInArea(storedPos, 25f, BotOwner))
                     {
                         if (Plugin.DebugLogging.Value) 
                         Plugin.Log.LogInfo($"[VultureLayer] No survivors remain near {storedPos}. Clearing persistence for {BotOwner.Profile.Nickname}.");
                         _storedTargets.Remove(BotOwner.ProfileId);
                         return false;
                     }
 
                     if (Vector3.Distance(BotOwner.Position, storedPos) < 5f)
                     {
                         _storedTargets.Remove(BotOwner.ProfileId);
                     }
                     else
                     {
                         if (Plugin.DebugLogging.Value) Plugin.Log.LogInfo($"[VultureLayer] {BotOwner.Profile.Nickname} RESUMING vulture to stored target {storedPos}!");
                         
                         _currentTargetPosition = storedPos;
                         
                         var resumeState = GetOrCreateState(BotOwner);
                         resumeState.ResetLifecycle(); // Reset BEFORE initialization
                         if (!resumeState.InitializeWithTarget(storedPos, storedTargetId))
                         {
                             RemoveState(BotOwner);
                             _storedTargets.Remove(BotOwner.ProfileId);
                             return false;
                         }
                         
                         _isActive = true;
                         _activationTime = Time.time;
                         return true;
                     }
                }
    
                return false;
            }
            catch (Exception ex)
            {
                // Catch global crash in layer to prevent logic death - ALWAYS LOG ERRORS
                Plugin.Log.LogError($"[VultureLayer] CRASH/NRE in IsActive: {ex.Message} \nStack: {ex.StackTrace}");
                return false;
            }
        }

        public override void Start()
        {
            // Called when layer becomes active
            try
            {
                bool enabled = IsBotEnabled();
                string name = BotOwner?.Profile?.Nickname ?? "Unknown";
                string role = BotOwner?.Profile?.Info?.Settings?.Role.ToString() ?? "Unknown";
                if (Plugin.DebugLogging.Value)
                    Plugin.Log.LogInfo($"[VultureLayer] Layer START for {name} (Role: {role}). Bot Enabled: {enabled}");

            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[VultureLayer] Error in Start: {ex}");
            }
        }

        public override void Stop()
        {
            // PERSISTENCE: Save state if interrupted for other reasons (e.g. state change, priority shift)
            if (_isActive && _botStates.TryGetValue(BotOwner.Id, out var state))
            {
                if (state.Phase == AI.VulturePhase.Move || state.Phase == AI.VulturePhase.Hold)
                {
                    StoreTarget(BotOwner.ProfileId, _currentTargetPosition, state.TargetProfileId, 300f);
                }
            }

            _isActive = false;
            _isAirdropVulture = false;
            
            // Cleanup persistent state (stops mover)
            RemoveState(BotOwner);
            
            // Remove squad vulture target when we stop
            if (BotOwner != null)
                _squadVultureTargets.Remove(BotOwner.ProfileId);
                
            // Set a cooldown to prevent rapid-fire reactivation loops
            // (e.g. if logic fails or action ends, don't restart immediately)
            // Increased to 45s to avoid SAIN/Vulture oscillation during combat tracking.
            _nextPossibleVultureTime = Time.time + 45f; 
        }

        private void CleanBlacklist()
        {
            var now = Time.time;
            var toRemove = new List<Vector3>();
            foreach (var kvp in _stalledAmbushPoints)
            {
                if (now >= kvp.Value) toRemove.Add(kvp.Key);
            }
            foreach (var key in toRemove) _stalledAmbushPoints.Remove(key);
        }

        private bool IsBotEnabled()
        {
            var profile = BotOwner?.Profile;
            if (profile == null || profile.Info == null) return false;

            var role = profile.Info.Settings.Role;
            bool isPScav = IsPlayerScav(profile);
            
            // Check PMC by WildSpawnType
            bool isPMC = role == WildSpawnType.pmcBEAR || role == WildSpawnType.pmcUSEC;
            
            // Check Scav-like roles (Assault, Cursed, Marksman if we want, etc.)
            bool isScav = !isPScav && (
                role == WildSpawnType.assault || 
                role == WildSpawnType.assaultGroup || 
                role == WildSpawnType.cursedAssault
            );

            // Check Raiders/Rogues
            bool isRaider = role == WildSpawnType.pmcBot || role == WildSpawnType.exUsec;

            // Check Goons
            bool isGoon = role == WildSpawnType.bossKnight || 
                          role == WildSpawnType.followerBigPipe || 
                          role == WildSpawnType.followerBirdEye;
            
            // NOTE: User requested removal of Boss Followers. They should stay with boss.
            
            if (isPMC && Plugin.EnablePMCs.Value) return true;
            if (isPScav && Plugin.EnablePScavs.Value) return true;
            if (isScav && Plugin.EnableScavs.Value) return true;
            if (isRaider && Plugin.EnableRaiders.Value) return true;
            if (isGoon && Plugin.EnableGoons.Value) return true;

            return false;
        }

        private bool IsPlayerScav(Profile profile)
        {
             if (profile.Info.Nickname.Contains(" (")) return true;
             return profile.Info.Settings.Role == WildSpawnType.assault 
                 && !string.IsNullOrEmpty(profile.Info.MainProfileNickname);
        }

        public static EPlayerSide GetBotSide(string profileId)
        {
            var gameWorld = Comfort.Common.Singleton<GameWorld>.Instance;
            if (gameWorld != null)
            {
                var player = gameWorld.GetAlivePlayerByProfileID(profileId);
                if (player != null) return player.Profile.Side;
            }
            return EPlayerSide.Savage;
        }

        private void InjectThreatMemory(string reason)
        {
            if (BotOwner == null || BotOwner.Memory == null) return;

            try
            {
                // Injection of threat memory. 
                // CRITICAL: We avoid AddEnemy because SAIN IsAI=false hack makes it unstable.
                // We use AddPointToSearch which achieves the same tactical orientation without the crash.
                
                Vector3 targetPos = Vector3.zero;
                if (reason.Contains("Enemy Close"))
                {
                    // Extract position if we have a nearby player
                    string nickname = reason.Split('(').Last().TrimEnd(')');
                    var gameWorld = Comfort.Common.Singleton<GameWorld>.Instance;
                    if (gameWorld != null)
                    {
                        foreach (var player in gameWorld.AllAlivePlayersList)
                        {
                            if (player != null && player.Profile.Nickname == nickname)
                            {
                                targetPos = player.Position;
                                break;
                            }
                        }
                    }
                }
                else if (reason.Contains("Shot/Explosion"))
                {
                    var recentEvent = CombatSoundListener.GetNearestEvent(BotOwner.Position, 50f, 10f, BotOwner.ProfileId, BotOwner.BotsGroup);
                    if (recentEvent != null) targetPos = recentEvent.Value.Position;
                }

                if (targetPos != Vector3.zero && BotOwner.BotsGroup != null)
                {
                    try {
                        BotOwner.BotsGroup.AddPointToSearch(targetPos, 80f, BotOwner);
                        if (Plugin.DebugLogging.Value) Plugin.Log.LogInfo($"[VultureLayer] Injected Search Point at {targetPos} (Reason: {reason})");
                    } catch { }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[VultureLayer] Error injecting threat memory: {ex}");
            }
        }
    }
}
