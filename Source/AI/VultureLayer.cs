using DrakiaXYZ.BigBrain.Brains;
using EFT;
using Luc1dShadow.Vulture.Integration;
using System;
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
#pragma warning disable CS0414 // Field is assigned but never used - reserved for VultureLogic differentiation
        private bool _isAirdropVulture; // True if vulturing an airdrop, false if combat vulture
#pragma warning restore CS0414
        private float _nextPossibleVultureTime;
        private Vector3 _currentTargetPosition;
        
        // One-shot log flags to prevent spam
        private bool _loggedCombatSuppression;
        private bool _loggedDisabledByRole;
        
        // Static dictionary to map BotOwner to their VultureLogic for ShouldStop check
        private static Dictionary<BotOwner, VultureLogic> _activeLogics = new Dictionary<BotOwner, VultureLogic>();
        
        // Public method for VultureLogic to register itself
        public static void RegisterLogic(BotOwner bot, VultureLogic logic)
        {
            _activeLogics[bot] = logic;
        }
        
        public static void UnregisterLogic(BotOwner bot)
        {
            _activeLogics.Remove(bot);
        }

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
            // If VultureLogic signals it should stop, end the action
            if (_activeLogics.TryGetValue(BotOwner, out var logic) && logic != null && logic.ShouldStop)
            {
                if (Plugin.DebugLogging.Value) Plugin.Log.LogInfo($"[VultureLayer] {BotOwner?.Profile?.Nickname ?? "Unknown"} - Logic signaled ShouldStop. Ending action.");
                _isActive = false;
                return true;
            }
            
            // If we are no longer active, action is ending
            return !_isActive;
        }

        public override bool IsActive()
        {
            try
            {
                // Must have BotOwner
                if (BotOwner == null || BotOwner.Profile == null) return false;

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
                             _ambushEndTime = Time.time + 60f; 
                             if (Plugin.DebugLogging.Value) Plugin.Log.LogInfo($"[VultureLayer] {BotOwner.Profile?.Nickname ?? "Unknown"} switching to GREED mode.");
                             return true; // Still active
                        }
    
                        _isActive = false;
                        _isGreeding = false;
                        // End of ambush naturally
                        return false;
                    }
                    return true;
                }
    
                // Cooldown Check
                if (Time.time < _nextPossibleVultureTime) return false;
    
                bool hasEvents = CombatSoundListener.RecentEvents.Count > 0;
                bool hasAirdrops = Plugin.EnableAirdropVulturing.Value && AirdropListener.ActiveAirdrops.Count > 0;
                
                if (!hasEvents && !hasAirdrops) return false;
    
                // CRITICAL: Combat Override
                if (BotOwner.Memory == null) return false;
    
                // If under fire, enemy visible, danger detected, OR shot landed nearby - yield to SAIN/Combat layers
                bool isUnderFire = BotOwner.Memory.IsUnderFire;
                bool hasVisibleEnemy = BotOwner.Memory.GoalEnemy != null && BotOwner.Memory.GoalEnemy.IsVisible;
                
                // Refined 'HaveEnemy' check: Only suppress if enemy is actually a threat (seen recently)
                // If we have an enemy but haven't seen them in 60s, we can probably Vulture
                bool hasActiveEnemy = false;
                float timeSinceLastSeen = 9999f;
                
                if (BotOwner.Memory.HaveEnemy && BotOwner.Memory.GoalEnemy != null)
                {
                     timeSinceLastSeen = Time.time - BotOwner.Memory.GoalEnemy.PersonalLastSeenTime;
                     if (timeSinceLastSeen < 60f) 
                     {
                         hasActiveEnemy = true;
                     }
                }
                
                // Check for near-miss shots: any shot within 5m of this bot
                bool nearMissDetected = false;
                float nearMissRadius = 5f; 
                
                try
                {
                    var recentEvents = CombatSoundListener.RecentEvents; 
                    for(int i = 0; i < recentEvents.Count; i++)
                    {
                        var evt = recentEvents[i];
                        if (!evt.IsExplosion && Vector3.Distance(BotOwner.Position, evt.Position) < nearMissRadius)
                        {
                            nearMissDetected = true;
                            break;
                        }
                    }
                }
                catch (Exception) { }
                
                // SPECIAL: Grenade Suppression handling
                bool shouldSuppress = false;
                string suppressionReason = "";
    
                if (hasVisibleEnemy)
                {
                    shouldSuppress = true;
                    suppressionReason = "visible enemy";
                }
                else if (hasActiveEnemy)
                {
                    shouldSuppress = true;
                    suppressionReason = $"active enemy (seen {timeSinceLastSeen:F1}s ago)";
                }
                else if (nearMissDetected)
                {
                    shouldSuppress = true;
                    suppressionReason = "near-miss shot";
                }
                else if (isUnderFire)
                {
                    if (Plugin.DebugLogging.Value && !_loggedCombatSuppression)
                    {
                         Plugin.Log.LogInfo($"[VultureLayer] {BotOwner.Profile.Nickname} is Under Fire but ignoring suppression to VULTURE (No visible enemy/near miss).");
                    }
                    shouldSuppress = false; 
                }
    
                if (shouldSuppress)
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
                
                if (isFollower && Plugin.SquadCoordination.Value)
                {
                    if (BotOwner.BotsGroup != null)
                    {
                        foreach (var member in BotOwner.BotsGroup.Members)
                        {
                            if (member == null || member == BotOwner) continue;
                            
                            string memberProfileId = member.ProfileId;
                            if (_squadVultureTargets.TryGetValue(memberProfileId, out Vector3 leaderTarget))
                            {
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
                var combatEvent = CombatSoundListener.GetNearestEvent(BotOwner.Position, effectiveRange);
                
                if (Plugin.DebugLogging.Value && hasEvents && combatEvent == null)
                {
                    var anyEvent = CombatSoundListener.RecentEvents.Count > 0 ? CombatSoundListener.RecentEvents[0] : (CombatSoundListener.CombatEvent?)null;
                    if (anyEvent != null)
                    {
                        float dist = Vector3.Distance(BotOwner.Position, anyEvent.Value.Position);
                        // Only log periodically to reduce spam? For now, keep it.
                        Plugin.Log.LogInfo($"[VultureLayer] {BotOwner.Profile.Nickname} - event exists at {dist:F0}m but range is {effectiveRange:F0}m");
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
                    
                    if (Plugin.DebugLogging.Value && intensity > 1)
                        Plugin.Log.LogInfo($"[VultureLayer] {BotOwner.Profile.Nickname} Multi-Shot Intensity: {intensity} events, effective chance: {effectiveChance}%");
    
                    if (Plugin.SAINEnabled.Value && SAINIntegration.IsSAINLoaded)
                    {
                        string personality = SAINIntegration.GetPersonality(BotOwner);
                        if (!string.IsNullOrEmpty(personality))
                        {
                            if (SAINIntegration.IsAggressivePersonality(personality))
                            {
                                effectiveChance += Plugin.SAINAggressionModifier.Value;
                                if (Plugin.DebugLogging.Value) 
                                    Plugin.Log.LogInfo($"[VultureLayer] {BotOwner.Profile.Nickname} ({personality}) is AGGRESSIVE. Bonus +{Plugin.SAINAggressionModifier.Value}% chance.");
                            }
                            else if (SAINIntegration.IsCautiousPersonality(personality))
                            {
                                effectiveChance -= Plugin.SAINCautiousModifier.Value;
                                if (Plugin.DebugLogging.Value) 
                                    Plugin.Log.LogInfo($"[VultureLayer] {BotOwner.Profile.Nickname} ({personality}) is CAUTIOUS. Penalty -{Plugin.SAINCautiousModifier.Value}% chance.");
                            }
                        }
                    }
                    
                    effectiveChance = Mathf.Clamp(effectiveChance, 0, 100);
    
                    int roll = UnityEngine.Random.Range(0, 100);
                    if (roll > effectiveChance)
                    {
                        _nextPossibleVultureTime = Time.time + 180f;
                        if (Plugin.DebugLogging.Value) 
                            Plugin.Log.LogInfo($"[VultureLayer] {BotOwner.Profile.Nickname} decided to IGNORE shot (Rolled {roll} > {effectiveChance}). Cooldown active.");
                        
                        return false;
                    }
    
                    if (Plugin.BossAvoidance.Value && CombatSoundListener.IsInBossZone(combatEvent.Value.Position, Plugin.BossAvoidanceRadius.Value))
                    {
                        _nextPossibleVultureTime = Time.time + 60f;
                        if (Plugin.DebugLogging.Value)
                            Plugin.Log.LogInfo($"[VultureLayer] {BotOwner.Profile.Nickname} AVOIDING boss zone!");
                        
                        return false;
                    }
    
                    string eventType = combatEvent.Value.IsExplosion ? "Explosion" : "Shot";
                    if (Plugin.DebugLogging.Value) Plugin.Log.LogInfo($"[VultureLayer] Bot {BotOwner.Profile.Nickname} activating Vulture! {eventType} at {combatEvent.Value.Position} dist: {Vector3.Distance(BotOwner.Position, combatEvent.Value.Position)}");
                    
                    _isActive = true;
                    _currentTargetPosition = combatEvent.Value.Position;
                    _ambushEndTime = Time.time + Plugin.AmbushDuration.Value;
                    
                    if (Plugin.SquadCoordination.Value && BotOwner.BotsGroup != null)
                    {
                        _squadVultureTargets[BotOwner.ProfileId] = _currentTargetPosition;
                        if (Plugin.DebugLogging.Value && BotOwner.BotsGroup.MembersCount > 1)
                            Plugin.Log.LogInfo($"[VultureLayer] {BotOwner.Profile.Nickname} broadcasting to squad.");
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
                        
                        _isActive = true;
                        _isAirdropVulture = true;
                        _currentTargetPosition = airdrop.Value.Position;
                        _ambushEndTime = Time.time + Plugin.AirdropAmbushDuration.Value;
                        
                        if (Plugin.SquadCoordination.Value && BotOwner.BotsGroup != null)
                        {
                            _squadVultureTargets[BotOwner.ProfileId] = _currentTargetPosition;
                            if (Plugin.DebugLogging.Value && BotOwner.BotsGroup.MembersCount > 1)
                                Plugin.Log.LogInfo($"[VultureLayer] {BotOwner.Profile.Nickname} broadcasting AIRDROP to squad.");
                        }
                        
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
                Plugin.Log.LogInfo($"[VultureLayer] Layer START for {name} (Role: {role}). Bot Enabled: {enabled}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[VultureLayer] Error in Start: {ex}");
            }
        }

        public override void Stop()
        {
            _isActive = false;
            _isGreeding = false;
            _isAirdropVulture = false;
            
            // Clean up logic registration
            UnregisterLogic(BotOwner);
            
            // Remove squad vulture target when we stop
            if (BotOwner != null)
                _squadVultureTargets.Remove(BotOwner.ProfileId);
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
            
            // NOTE: User requested removal of Boss Followers. They should stay with boss.
            
            if (isPMC && Plugin.EnablePMCs.Value) return true;
            if (isPScav && Plugin.EnablePScavs.Value) return true;
            if (isScav && Plugin.EnableScavs.Value) return true;
            if (isRaider && Plugin.EnableRaiders.Value) return true;

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
