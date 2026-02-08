using DrakiaXYZ.BigBrain.Brains;
using EFT;
using Luc1dShadow.Vulture.Integration; // Add namespace
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace Luc1dShadow.Vulture
{
    public class VultureLayer : CustomLayer
    {
        private bool _isActive;
        private float _ambushEndTime;
        private bool _isGreeding;
        private float _nextPossibleVultureTime;
        private Vector3 _currentTargetPosition;
        
        // One-shot log flags to prevent spam
        private bool _loggedCombatSuppression;
        private bool _loggedDisabledByRole;

        // Static dictionary to share vulture targets between squad members
        // Key: Leader ProfileId, Value: Target position
        private static Dictionary<string, Vector3> _squadVultureTargets = new Dictionary<string, Vector3>();

        public override string GetName() => "VultureLayer";

        public VultureLayer(BotOwner botOwner, int priority) : base(botOwner, priority) 
        {
        }

        public override Action GetNextAction()
        {
            return new Action(typeof(VultureLogic), "Vulture Ambush");
        }
        
        public override bool IsCurrentActionEnding()
        {
            // If we are no longer active, action is ending
            return !_isActive;
        }

        public override bool IsActive()
        {
            // Check state
            if (_isActive)
            {
                // If time is up, stop OR switch to Greed
                if (Time.time > _ambushEndTime)
                {
                    if (Plugin.LootGreed.Value && !_isGreeding)
                    {
                         // Switch to Greed Mode
                         _isGreeding = true;
                         // Add extra time for the push (e.g. 60s)
                         _ambushEndTime = Time.time + 60f; 
                         if (Plugin.DebugLogging.Value) Plugin.Log.LogInfo($"[VultureLayer] {BotOwner.Profile.Nickname} switching to GREED mode.");
                         return true; // Still active
                    }

                    _isActive = false;
                    _isGreeding = false;
                    return false;
                }
                return true;
            }

            // Cooldown Check
            if (Time.time < _nextPossibleVultureTime) return false;

            bool hasEvents = CombatSoundListener.RecentEvents.Count > 0;

            // CRITICAL: Combat Override
            // If under fire, enemy visible, danger detected, OR shot landed nearby - yield to SAIN/Combat layers
            bool isUnderFire = BotOwner.Memory.IsUnderFire;
            bool hasVisibleEnemy = BotOwner.Memory.GoalEnemy != null && BotOwner.Memory.GoalEnemy.IsVisible;
            bool haveEnemy = BotOwner.Memory.HaveEnemy;
            
            // Check for near-miss shots: any shot within 5m of this bot
            bool nearMissDetected = false;
            float nearMissRadius = 5f; // 5 meters ~ about 16 feet
            foreach (var evt in CombatSoundListener.RecentEvents)
            {
                if (!evt.IsExplosion && Vector3.Distance(BotOwner.Position, evt.Position) < nearMissRadius)
                {
                    nearMissDetected = true;
                    break;
                }
            }
            
            if (isUnderFire || hasVisibleEnemy || haveEnemy || nearMissDetected)
            {
                 if (Plugin.DebugLogging.Value && !_loggedCombatSuppression)
                 {
                     string reason = isUnderFire ? "under fire" : 
                                    hasVisibleEnemy ? "visible enemy" : 
                                    haveEnemy ? "has enemy" : "near-miss shot";
                     Plugin.Log.LogInfo($"[VultureLayer] {BotOwner.Profile.Nickname} suppressed by combat ({reason}).");
                     _loggedCombatSuppression = true;
                 }
                 return false;
            }
            else
            {
                 _loggedCombatSuppression = false; // Reset when no longer in combat
            }

            // 1. Config/Role Checks
            if (!IsBotEnabled()) 
            {
                // Only log once per bot to avoid spam
                if (Plugin.DebugLogging.Value && hasEvents && !_loggedDisabledByRole) 
                {
                    Plugin.Log.LogInfo($"[VultureLayer] {BotOwner.Profile.Nickname} disabled by role/config.");
                    _loggedDisabledByRole = true;
                }
                return false;
            }
            else
            {
                 _loggedDisabledByRole = false; // Reset if role becomes enabled
            }

            // 2. Squad Checks
            bool isFollower = BotOwner.BotFollower?.BossToFollow != null;
            
            // Squad Coordination: Check if our leader is vulturing
            if (isFollower && Plugin.SquadCoordination.Value)
            {
                // Find our leader by iterating through group members
                foreach (var member in BotOwner.BotsGroup.Members)
                {
                    if (member == null || member == BotOwner) continue;
                    
                    string memberProfileId = member.ProfileId;
                    if (_squadVultureTargets.TryGetValue(memberProfileId, out Vector3 leaderTarget))
                    {
                        // A squad member is vulturing! Join them
                        if (!_isActive)
                        {
                            _isActive = true;
                            _currentTargetPosition = leaderTarget;
                            _ambushEndTime = Time.time + Plugin.AmbushDuration.Value;
                            
                            if (Plugin.DebugLogging.Value)
                                Plugin.Log.LogInfo($"[VultureLayer] {BotOwner.Profile.Nickname} following squad member {member.Profile.Nickname} to vulture at {leaderTarget}");
                        }
                        return true;
                    }
                }
            }
            
            // Non-leaders with squad size check
            if (BotOwner.BotsGroup.MembersCount < Plugin.MinSquadSize.Value) 
            {
                 if (Plugin.DebugLogging.Value && hasEvents) 
                      Plugin.Log.LogInfo($"[VultureLayer] {BotOwner.Profile.Nickname} ignored: Squad too small ({BotOwner.BotsGroup.MembersCount})");
                 return false;
            }
            
            // Original followers-don't-initiate check (but Squad Coordination above takes priority)
            if (isFollower && !Plugin.SquadCoordination.Value) return false;

            // 3. Combat Event Check (shots OR explosions)
            float effectiveRange = MapSettings.GetEffectiveRange();
            var combatEvent = CombatSoundListener.GetNearestEvent(BotOwner.Position, effectiveRange);
            
            // Diagnostic: Log when bot evaluates events
            if (Plugin.DebugLogging.Value && hasEvents && combatEvent == null)
            {
                var anyEvent = CombatSoundListener.RecentEvents.Count > 0 ? CombatSoundListener.RecentEvents[0] : (CombatSoundListener.CombatEvent?)null;
                if (anyEvent != null)
                {
                    float dist = Vector3.Distance(BotOwner.Position, anyEvent.Value.Position);
                    Plugin.Log.LogInfo($"[VultureLayer] {BotOwner.Profile.Nickname} - event exists at {dist:F0}m but range is {effectiveRange:F0}m");
                }
            }
            
            if (combatEvent != null)
            {
                // Calculate Multi-Shot Intensity bonus
                int intensity = CombatSoundListener.GetEventIntensity(
                    combatEvent.Value.Position, 
                    MapSettings.GetEffectiveRange() * 0.5f,
                    Plugin.IntensityWindow.Value
                );
                
                // Effective chance = base + (intensity bonus * extra shots)
                // First shot doesn't give bonus, each additional shot adds bonus %
                int extraShots = Mathf.Max(0, intensity - 1);
                int effectiveChance = Mathf.Min(95, Plugin.VultureChance.Value + (extraShots * Plugin.IntensityBonus.Value));
                
                if (Plugin.DebugLogging.Value && intensity > 1)
                    Plugin.Log.LogInfo($"[VultureLayer] {BotOwner.Profile.Nickname} Multi-Shot Intensity: {intensity} events, effective chance: {effectiveChance}%");

                // Integration: SAIN Personality Modifiers
                if (Plugin.SAINEnabled.Value && SAINIntegration.IsSAINLoaded)
                {
                    string personality = SAINIntegration.GetPersonality(BotOwner);
                    if (!string.IsNullOrEmpty(personality))
                    {
                        if (SAINIntegration.IsAggressivePersonality(personality))
                        {
                            effectiveChance += Plugin.SAINAggressionModifier.Value;
                            if (Plugin.DebugLogging.Value) 
                                Plugin.Log.LogInfo($"[VultureLayer] {BotOwner.Profile.Nickname} ({personality}) is AGGRESSIVE. Bonus +{Plugin.SAINAggressionModifier.Value}% chance. Total: {effectiveChance}%");
                        }
                        else if (SAINIntegration.IsCautiousPersonality(personality))
                        {
                            effectiveChance -= Plugin.SAINCautiousModifier.Value;
                            if (Plugin.DebugLogging.Value) 
                                Plugin.Log.LogInfo($"[VultureLayer] {BotOwner.Profile.Nickname} ({personality}) is CAUTIOUS. Penalty -{Plugin.SAINCautiousModifier.Value}% chance. Total: {effectiveChance}%");
                        }
                    }
                }
                
                // Clamp final chance
                effectiveChance = Mathf.Clamp(effectiveChance, 0, 100);

                // Chance Roll
                int roll = UnityEngine.Random.Range(0, 100);
                if (roll > effectiveChance)
                {
                    // Failed roll - Ignore for 180s
                    _nextPossibleVultureTime = Time.time + 180f;
                    if (Plugin.DebugLogging.Value) 
                        Plugin.Log.LogInfo($"[VultureLayer] {BotOwner.Profile.Nickname} decided to IGNORE shot (Rolled {roll} > {effectiveChance}). Cooldown active.");
                    return false;
                }

                // TIER 2: Boss Avoidance Check
                if (Plugin.BossAvoidance.Value && CombatSoundListener.IsInBossZone(combatEvent.Value.Position, Plugin.BossAvoidanceRadius.Value))
                {
                    _nextPossibleVultureTime = Time.time + 60f; // Short cooldown, boss might move
                    if (Plugin.DebugLogging.Value)
                        Plugin.Log.LogInfo($"[VultureLayer] {BotOwner.Profile.Nickname} AVOIDING boss zone near {combatEvent.Value.Position}!");
                    return false;
                }

                string eventType = combatEvent.Value.IsExplosion ? "Explosion" : "Shot";
                if (Plugin.DebugLogging.Value) Plugin.Log.LogInfo($"[VultureLayer] Bot {BotOwner.Profile.Nickname} activating Vulture! {eventType} at {combatEvent.Value.Position} dist: {Vector3.Distance(BotOwner.Position, combatEvent.Value.Position)}");
                
                // New engagement
                _isActive = true;
                _currentTargetPosition = combatEvent.Value.Position;
                _ambushEndTime = Time.time + Plugin.AmbushDuration.Value;
                
                // TIER 2: Squad Coordination - Register as vulturing for followers
                if (Plugin.SquadCoordination.Value)
                {
                    _squadVultureTargets[BotOwner.ProfileId] = _currentTargetPosition;
                    if (Plugin.DebugLogging.Value && BotOwner.BotsGroup.MembersCount > 1)
                        Plugin.Log.LogInfo($"[VultureLayer] {BotOwner.Profile.Nickname} broadcasting vulture target to squad ({BotOwner.BotsGroup.MembersCount} members)");
                }
                
                return true;
            }
            else if (hasEvents && Plugin.DebugLogging.Value)
            {
                 // Uncomment for verbose range debugging:
                 // var anyEvent = CombatSoundListener.GetNearestEvent(BotOwner.Position, 9999f);
                 // if (anyEvent != null)
                 // {
                 //     float dist = Vector3.Distance(BotOwner.Position, anyEvent.Value.Position);
                 //     Plugin.Log.LogInfo($"[VultureLayer] {BotOwner.Profile.Nickname} hears shot but too far: {dist}m > {MapSettings.GetEffectiveRange()}m");
                 // }
            }

            return false;
        }

        public override void Start()
        {
            // Called when layer becomes active
        }

        public override void Stop()
        {
            _isActive = false;
            _isGreeding = false;
            
            // Remove squad vulture target when we stop
            _squadVultureTargets.Remove(BotOwner.ProfileId);
        }

        private bool IsBotEnabled()
        {
            var profile = BotOwner.Profile;
            var role = profile.Info.Settings.Role;
            bool isPScav = IsPlayerScav(profile);
            
            // Check PMC by WildSpawnType
            bool isPMC = role == WildSpawnType.pmcBEAR || role == WildSpawnType.pmcUSEC;
            // Check Scav by WildSpawnType
            bool isScav = !isPScav && (role == WildSpawnType.assault || role == WildSpawnType.assaultGroup);

            if (isPMC && Plugin.EnablePMCs.Value) return true;
            if (isPScav && Plugin.EnablePScavs.Value) return true;
            if (isScav && Plugin.EnableScavs.Value) return true;

            return false;
        }

        private bool IsPlayerScav(Profile profile)
        {
             if (profile.Info.Nickname.Contains(" (")) return true;
             return profile.Info.Settings.Role == WildSpawnType.assault 
                 && !string.IsNullOrEmpty(profile.Info.MainProfileNickname);
        }
    }
}
