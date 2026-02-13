using EFT;
using UnityEngine;
using UnityEngine.AI;
using Luc1dShadow.Vulture.AI.Movement;
using Luc1dShadow.Vulture.Integration;

namespace Luc1dShadow.Vulture.AI
{
    public enum VulturePhase
    {
        Initializing,
        Move,
        Hold,
        Greed,
        PostGreed,
        Search
    }

    /// <summary>
    /// Holds the persistent state of a Vulture bot.
    /// Shared across all Logic classes. Survives BigBrain logic restarts.
    /// </summary>
    public class VultureBotState
    {
        public BotOwner Bot { get; }
        public VultureMover Mover { get; }
        
        // Phase Control
        public VulturePhase Phase { get; set; } = VulturePhase.Initializing;
        public bool PhaseChanged { get; set; }
        
        // Navigation State
        public NavMeshPath Path { get; }
        public Vector3 AmbushPos { get; set; }
        public bool IsMoving { get; set; }
        public bool HasArrived { get; set; }
        
        // Combat State
        public CombatSoundListener.CombatEvent? CombatEvent { get; set; }
        public bool IsTargetExplosion { get; set; }
        
        // Logic Flags
        public bool IsGreeding { get; set; }
        public bool IsCreeping { get; set; }
        public bool IsPostGreedHolding { get; set; }
        public bool IsScanning { get; set; }
        public bool SearchFailed { get; set; }
        public bool IsSuppressed { get; set; }
        public bool IsStaminaDepleted { get; set; }
        public bool IsVultureActive { get; set; }
        public bool InitializationAttempted { get; set; }
        public bool InitializationFailed { get; set; }
        
        // Cover State
        public VultureCoverValidator.CoverheightType AmbushCoverHeight { get; set; }
        
        // Timers & Delays
        public float MoveStartTime { get; set; }
        public float LogicStartTime { get; set; }
        public float PostGreedHoldStartTime { get; set; }
        public float IdleStartTime { get; set; }
        public float NextBaitTime { get; set; }
        public float NextScanTime { get; set; }
        public float MaxIdleTime { get; set; }
        public float SuppressionTimer { get; set; }
        
        public Vector3 ScanTarget { get; set; }
        public Vector3 PostGreedBodyPosition { get; set; }
        
        // Search Phase State
        public System.Collections.Generic.List<Vector3> SearchPoints { get; set; } = new System.Collections.Generic.List<Vector3>();
        public int CurrentSearchPointIndex { get; set; } = -1;
        public float SearchWaitStartTime { get; set; }

        public bool ShouldStop { get; set; }
        public bool LoggedStall { get; set; }
        public string TargetProfileId { get; set; }
        public float LastRolledEventTime { get; set; }
        
        // Failure Tracking
        public Vector3 LastFailedTargetPos { get; set; }
        public float LastFailedTargetTime { get; set; }

        public VultureBotState(BotOwner bot)
        {
            Bot = bot;
            
            // Initialize persistent components
            Mover = new VultureMover(bot);
            Path = new NavMeshPath();
            
            // Default Times
            LogicStartTime = Time.time;
        }

        /// <summary>
        /// Called by VultureLayer when first activating. Finds combat event and ambush point.
        /// Sets Phase to Move on success, or ShouldStop on failure.
        /// </summary>
        public bool Initialize()
        {
            // Initialize Map Safety (One-time check, efficient)
            VultureMapUtil.Initialize();
            InitializationAttempted = true;
            LoggedStall = false;

            // Find target - use self and squad filters for consistency with Layer selection
            var combatEvent = CombatSoundListener.GetNearestEvent(Bot.Position, MapSettings.GetEffectiveRange(), 90f, Bot.ProfileId, Bot.BotsGroup);
            
            if (combatEvent != null)
            {
                CombatEvent = combatEvent;
                IsTargetExplosion = combatEvent.Value.IsExplosion;
                
                // Try to find a valid ambush point
                if (VulturePathUtil.TryFindAmbushPoint(Bot, Path, combatEvent.Value.Position, out Vector3 ambushPos, out VultureCoverValidator.CoverheightType heightType))
                {
                    AmbushPos = ambushPos;
                    AmbushCoverHeight = heightType;
                    TargetProfileId = combatEvent.Value.ShooterProfileId;
                    IsMoving = true;
                    MoveStartTime = Time.time;
                    LogicStartTime = Time.time;
                    
                    IsGreeding = false;
                    IsCreeping = false;
                    IsPostGreedHolding = false;
                    ShouldStop = false;
                    
                    // Set fail-safe timeout
                    MaxIdleTime = Plugin.AmbushDuration.Value + 30f;
                    
                    // Initialize Bait Timer
                    NextBaitTime = Time.time + Random.Range(5f, 15f);
                    
                    // Set Phase
                    Phase = VulturePhase.Move;
                    IsVultureActive = true;

                    // Move
                    Mover.SetSprint(true);
                    Mover.MoveTo(ambushPos, 1.0f);
                    
                    if (Plugin.DebugLogging.Value)
                    {
                        string eventType = IsTargetExplosion ? "explosion" : "shot";
                        Plugin.Log.LogInfo($"[Vulture] Bot {Bot.Profile.Nickname} moving to ambush {eventType} at {ambushPos}");
                    }

                    // Immersion
                    if (Plugin.EnableVoiceLines.Value) Bot.BotTalk.Say(EPhraseTrigger.OnFight);
                    if (Plugin.FlashlightDiscipline.Value && Bot.BotLight != null && Bot.BotLight.IsEnable)
                    {
                         Bot.BotLight.TurnOff(false, true);
                    }
                    
                    return true;
                }
                else
                {
                    Plugin.Log.LogWarning($"[Vulture] Bot {Bot.Profile.Nickname} failed to find valid ambush point. Aborting.");
                    SearchFailed = true;
                    InitializationFailed = true;
                    ShouldStop = true;
                    return false;
                }
            }
            else
            {
                IsMoving = false;
                InitializationFailed = true;
                ShouldStop = true;
                return false;
            }
        }

        /// <summary>
        /// Called by VultureLayer for airdrop/stored target activations where we already know the position.
        /// </summary>
        public bool InitializeWithTarget(Vector3 targetPos, string targetProfileId = null)
        {
            VultureMapUtil.Initialize();
            InitializationAttempted = true;
            LoggedStall = false;
            TargetProfileId = targetProfileId;

            if (VulturePathUtil.TryFindAmbushPoint(Bot, Path, targetPos, out Vector3 ambushPos, out VultureCoverValidator.CoverheightType heightType))
            {
                AmbushPos = ambushPos;
                AmbushCoverHeight = heightType;
                IsMoving = true;
                MoveStartTime = Time.time;
                LogicStartTime = Time.time;

                IsGreeding = false;
                IsCreeping = false;
                IsPostGreedHolding = false;
                ShouldStop = false;

                MaxIdleTime = Plugin.AmbushDuration.Value + 30f;
                NextBaitTime = Time.time + Random.Range(5f, 15f);
                Phase = VulturePhase.Move;
                IsVultureActive = true;

                Mover.SetSprint(true);
                Mover.MoveTo(ambushPos, 1.0f);

                if (Plugin.DebugLogging.Value)
                    Plugin.Log.LogInfo($"[Vulture] Bot {Bot.Profile.Nickname} moving to target at {ambushPos}");

                if (Plugin.EnableVoiceLines.Value) Bot.BotTalk.Say(EPhraseTrigger.OnFight);
                if (Plugin.FlashlightDiscipline.Value && Bot.BotLight != null && Bot.BotLight.IsEnable)
                    Bot.BotLight.TurnOff(false, true);

                return true;
            }
            else
            {
                Plugin.Log.LogWarning($"[Vulture] Bot {Bot.Profile.Nickname} failed to find ambush for target. Aborting.");
                SearchFailed = true;
                InitializationFailed = true;
                ShouldStop = true;
                
                // Track failure
                LastFailedTargetPos = targetPos;
                LastFailedTargetTime = Time.time;
                
                return false;
            }
        }

        public void ResetLifecycle()
        {
            InitializationAttempted = false;
            InitializationFailed = false;
            ShouldStop = false;
            LoggedStall = false;
            IsVultureActive = false;
            
            MoveStartTime = Time.time;
            LogicStartTime = Time.time;
            
            SearchFailed = false;
            IsSuppressed = false;
            AmbushPos = Vector3.zero;
            
            if (Mover != null)
            {
                Mover.Stop();
            }
        }

        public void Dispose()
        {
            if (Mover != null)
            {
                Mover.Stop();
            }
        }
    }
}
