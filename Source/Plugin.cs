using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using DrakiaXYZ.BigBrain.Brains;
using Luc1dShadow.Vulture.Integration; // Add namespace
using System.Collections.Generic;
using UnityEngine;
using HarmonyLib;

namespace Luc1dShadow.Vulture
{
    [BepInPlugin("com.luc1dshadow.vulture", "Vulture", "1.0.3")]
    [BepInDependency("xyz.drakia.bigbrain", "0.3.0")]
    [BepInDependency("me.sol.sain", BepInDependency.DependencyFlags.SoftDependency)] // Load after SAIN if present
    public class Plugin : BaseUnityPlugin
    {
        public static ManualLogSource Log;
        private const int LATEST_CONFIG_VERSION = 5;
        
        // Configuration

        public static ConfigEntry<float> DetectionRange;
        public static ConfigEntry<int> MinSquadSize;
        public static ConfigEntry<int> VultureChance;
        public static ConfigEntry<float> AmbushDuration;
        public static ConfigEntry<float> AmbushTier1;
        public static ConfigEntry<float> AmbushTier2;
        public static ConfigEntry<float> AmbushTier3;
        
        public static ConfigEntry<bool> EnablePMCs;
        public static ConfigEntry<bool> EnableScavs;
        public static ConfigEntry<bool> EnablePScavs;
        public static ConfigEntry<bool> EnableRaiders;
        public static ConfigEntry<bool> EnableGoons;
        
        public static ConfigEntry<bool> SilentApproach;
        public static ConfigEntry<float> SilentApproachDistance;
        
        public static ConfigEntry<bool> Paranoia;
        
        public static ConfigEntry<bool> LootGreed;
        
        public static ConfigEntry<bool> DebugLogging;

        // Tier 1 New Features
        public static ConfigEntry<bool> EnableExplosionDetection;
        public static ConfigEntry<int> IntensityBonus;
        public static ConfigEntry<float> IntensityWindow;

        // Tier 2: Boss Avoidance
        public static ConfigEntry<bool> BossAvoidance;
        public static ConfigEntry<float> BossAvoidanceRadius;
        public static ConfigEntry<float> BossZoneDecay;

        // Tier 3: Dynamic Courage & Silence Trigger
        public static ConfigEntry<float> BaseDetectionRange;
        public static ConfigEntry<int> MultiShotIntensity;
        public static ConfigEntry<int> CourageThreshold;
        public static ConfigEntry<float> SilenceTriggerDuration;

        // Tier 2: Squad Coordination
        public static ConfigEntry<bool> SquadCoordination;

        // Per-Map Multipliers
        public static ConfigEntry<float> FactoryMultiplier;
        public static ConfigEntry<float> LabsMultiplier;
        public static ConfigEntry<float> InterchangeMultiplier;
        public static ConfigEntry<float> ReserveMultiplier;
        public static ConfigEntry<float> CustomsMultiplier;
        public static ConfigEntry<float> GroundZeroMultiplier;
        public static ConfigEntry<float> StreetsMultiplier;
        public static ConfigEntry<float> ShorelineMultiplier;
        public static ConfigEntry<float> LighthouseMultiplier;
        public static ConfigEntry<float> WoodsMultiplier;

        // Time-of-Day
        public static ConfigEntry<bool> NightTimeModifier;
        public static ConfigEntry<float> NightRangeMultiplier;

        // Tier 3: Voice Lines
        public static ConfigEntry<bool> EnableVoiceLines;
        
        // Tier 3: Flashlight Discipline
        public static ConfigEntry<bool> FlashlightDiscipline;

        // Tier 2: Smart Ambush & Baiting
        public static ConfigEntry<bool> SmartAmbush;
        public static ConfigEntry<bool> EnableBaiting;

        public static ConfigEntry<int> BaitingChance;

        // Integration: SAIN
        public static ConfigEntry<bool> SAINEnabled;
        public static ConfigEntry<int> SAINAggressionModifier;
        public static ConfigEntry<int> SAINCautiousModifier;

        // Airdrop Vulturing
        public static ConfigEntry<bool> EnableAirdropVulturing;
        public static ConfigEntry<float> AirdropDetectionRange;
        public static ConfigEntry<float> AirdropAmbushDistanceMin;
        public static ConfigEntry<float> AirdropAmbushDistanceMax;
        public static ConfigEntry<float> AirdropAmbushDuration;
        public static ConfigEntry<int> AirdropVultureChance;

        // GUI
        public static ConfigEntry<KeyCode> GUIKey;

        // New Config Entries
        public static ConfigEntry<bool> EnableBushVision;
        public static ConfigEntry<float> BushVisionRange;
        public static ConfigEntry<float> NearMissRadius;

        private void Awake()
        {
            Log = Logger;
            Log.LogInfo("Vulture: Initializing...");

            try
            {
                // Hidden config attribute - all options hidden from F12, shown in custom GUI
                var hidden = new ConfigurationManagerAttributes { Browsable = false };

                // General Settings
                DebugLogging = Config.Bind("1. General", "Debug Logging", false,
                    new ConfigDescription("Enable deeper logging for Vulture logic.", null, hidden));
                


                // Triggers
                BaseDetectionRange = Config.Bind("2. Triggers", "Base Detection Range", 150f,
                    new ConfigDescription("Maximum distance bots can hear unsuppressed shots (before map multiplier).", null, hidden));

                VultureChance = Config.Bind("2. Triggers", "Vulture Chance", 50,
                    new ConfigDescription("Percent chance a bot will investigate a gunshot.", new AcceptableValueRange<int>(0, 100), hidden));

                MultiShotIntensity = Config.Bind("2. Triggers", "Multi-Shot Intensity", 5,
                    new ConfigDescription("Bonus % chance per additional shot in the same area.", new AcceptableValueRange<int>(0, 20), hidden));
                
                CourageThreshold = Config.Bind("2. Triggers", "Courage Threshold (Fear)", 15,
                    new ConfigDescription("If more than this many shots/explosions occur in 5s, the bot will hesitate/wait.", new AcceptableValueRange<int>(0, 30), hidden));

                // Behavior
                AmbushDuration = Config.Bind("3. Behaviors", "Ambush Duration", 90f,
                    new ConfigDescription("How long (seconds) bots hold the ambush angle.", null, hidden));
                
                SilenceTriggerDuration = Config.Bind("3. Behaviors", "Silence Trigger (Rush)", 45f,
                    new ConfigDescription("If approaching and no shots heard for this long (seconds), bots will rush instead of creep.", null, hidden));

                AmbushTier1 = Config.Bind("3. Behaviors", "Ambush Range Tier 1", 20f,
                    new ConfigDescription("Closest distance bots aim to hold from combat. Default: 20m", new AcceptableValueRange<float>(5f, 40f), hidden));
                AmbushTier2 = Config.Bind("3. Behaviors", "Ambush Range Tier 2", 30f,
                    new ConfigDescription("Middle distance bots aim to hold from combat. Default: 30m", new AcceptableValueRange<float>(10f, 60f), hidden));
                AmbushTier3 = Config.Bind("3. Behaviors", "Ambush Range Tier 3", 40f,
                    new ConfigDescription("Furthest distance bots aim to hold from combat. Default: 40m", new AcceptableValueRange<float>(15f, 80f), hidden));

                EnableBushVision = Config.Bind("3. Behaviors", "Enable Bush Vision", true,
                    new ConfigDescription("Bots can see through foliage at close range while in Vulture mode.", null, hidden));

                BushVisionRange = Config.Bind("3. Behaviors", "Bush Vision Range", 10f,
                    new ConfigDescription("Maximum distance bots can see through bushes (meters).", new AcceptableValueRange<float>(0f, 30f), hidden));

                NearMissRadius = Config.Bind("3. Behaviors", "Near Miss Sensitivity", 12f,
                    new ConfigDescription("How close a shot must be (meters) to pull a bot out of ambush and into combat.", new AcceptableValueRange<float>(1f, 30f), hidden));

                EnableExplosionDetection = Config.Bind("Triggers", "Detect Explosions", true, 
                    new ConfigDescription("Also react to grenade explosions, not just gunshots", null, hidden));

                // Multi-Shot Intensity
                IntensityWindow = Config.Bind("Triggers", "Intensity Window", 15f, 
                    new ConfigDescription("Time window (seconds) for counting shots", new AcceptableValueRange<float>(5f, 60f), hidden));

                // Bot Roles
                EnablePMCs = Config.Bind("Bot Roles", "Enable for PMCs", true, 
                    new ConfigDescription("Enable for USEC/BEAR via Vulture", null, hidden));
                EnableScavs = Config.Bind("Bot Roles", "Enable for Scavs", false, 
                    new ConfigDescription("Enable for AI Scavs via Vulture", null, hidden));
                EnablePScavs = Config.Bind("Bot Roles", "Enable for Player Scavs", false, 
                    new ConfigDescription("Enable for Player Scavs via Vulture", null, hidden));
                EnableRaiders = Config.Bind("Bot Roles", "Enable for Raiders/Rogues", false, 
                    new ConfigDescription("Enable for Raiders and Rogues via Vulture", null, hidden));
                EnableGoons = Config.Bind("Bot Roles", "Enable for Goons", false, 
                    new ConfigDescription("Enable for the Goon Squad (Knight, BigPipe, BirdEye) via Vulture", null, hidden));

                // Legacy / Hidden
                MinSquadSize = Config.Bind("Triggers", "Min Squad Size", 1, 
                    new ConfigDescription("Minimum squad size to activate Vulture behavior", null, hidden));
                
                // Keep DetectionRange for migration logic, but it's hidden. 
                // The active one is BaseDetectionRange.
                DetectionRange = Config.Bind("internal", "Detection Range", 150f, 
                    new ConfigDescription("Legacy detection range for migration", null, hidden));


                LootGreed = Config.Bind("Behaviors", "Greed", true, 
                    new ConfigDescription("Bot will push aggressively toward combat zone after ambush", null, hidden));
                
                // Advanced
                SilentApproach = Config.Bind("Advanced", "Silent Approach", true, 
                    new ConfigDescription("Creep (crouch-walk) to the ambush point.", null, hidden));
                SilentApproachDistance = Config.Bind("Advanced", "Silent Approach Distance", 35f, 
                    new ConfigDescription("Distance from target to start creeping.", null, hidden));
                Paranoia = Config.Bind("Advanced", "Paranoia", true, 
                    new ConfigDescription("Randomly check angles (head swivel) while ambushing.", null, hidden));

                // Tier 2: Boss Avoidance
                BossAvoidance = Config.Bind("Advanced", "Boss Avoidance", true, 
                    new ConfigDescription("Vultures avoid areas where bosses were detected firing.", null, hidden));
                BossAvoidanceRadius = Config.Bind("Advanced", "Boss Avoidance Radius", 75f, 
                    new ConfigDescription("Radius around boss activity to avoid (meters).", new AcceptableValueRange<float>(25f, 200f), hidden));
                BossZoneDecay = Config.Bind("Advanced", "Boss Zone Decay", 120f, 
                    new ConfigDescription("How long (seconds) a boss zone remains 'hot'.", new AcceptableValueRange<float>(30f, 300f), hidden));

                // Tier 2: Squad Coordination
                SquadCoordination = Config.Bind("Behaviors", "Squad Coordination", true, 
                    new ConfigDescription("When a squad leader vultures, followers join.", null, hidden));

                // Tier 3: Voice Lines
                EnableVoiceLines = Config.Bind("Immersion", "Voice Lines", true, 
                    new ConfigDescription("Bots whisper voice lines when they hear shots.", null, hidden));
                
                // Tier 3: Flashlight Discipline
                FlashlightDiscipline = Config.Bind("Immersion", "Flashlight Discipline", true, 
                    new ConfigDescription("Bots turn off flashlights/lasers when stalking.", null, hidden));

                // Tier 2: Smart Ambush / Baiting
                SmartAmbush = Config.Bind("Behaviors", "Smart Ambush", true, 
                    new ConfigDescription("Use cover points for ambush instead of open ground.", null, hidden));
                EnableBaiting = Config.Bind("Behaviors", "Baiting", true, 
                    new ConfigDescription("Bots fire decoy shots while holding ambush to lure enemies.", null, hidden));
                BaitingChance = Config.Bind("Behaviors", "Baiting Chance", 25, 
                    new ConfigDescription("Chance % to fire a bait shot during ambush.", new AcceptableValueRange<int>(0, 100), hidden));

                // Integrations: SAIN
                SAINEnabled = Config.Bind("Integrations", "Enable SAIN", true, 
                    new ConfigDescription("Enable integration with SAIN personalities.", null, hidden));
                SAINAggressionModifier = Config.Bind("Integrations", "Aggression Mod", 20, 
                    new ConfigDescription("Bonus % chance for aggressive SAIN bots (GigaChad, Chad, Wreckless).", new AcceptableValueRange<int>(0, 100), hidden));
                SAINCautiousModifier = Config.Bind("Integrations", "Cautious Mod", 20, 
                    new ConfigDescription("Reduced % chance for cautious SAIN bots (Rat, Timmy, Coward).", new AcceptableValueRange<int>(0, 100), hidden));

                // Airdrop Vulturing
                EnableAirdropVulturing = Config.Bind("2. Triggers", "Enable Airdrop Vulturing", true,
                    new ConfigDescription("Bots will vulture around landed airdrops.", null, hidden));
                AirdropDetectionRange = Config.Bind("Triggers", "Airdrop Detection Range", 300f, 
                    new ConfigDescription("Maximum distance bots can detect airdrops (meters).", new AcceptableValueRange<float>(100f, 600f), hidden));
                AirdropAmbushDistanceMin = Config.Bind("Triggers", "Airdrop Ambush Min", 20f, 
                    new ConfigDescription("Minimum distance to hold from airdrop (meters). Default: 20m", new AcceptableValueRange<float>(20f, 80f), hidden));
                AirdropAmbushDistanceMax = Config.Bind("Triggers", "Airdrop Ambush Max", 30f, 
                    new ConfigDescription("Maximum distance to hold from airdrop (meters). Default: 30m", new AcceptableValueRange<float>(30f, 100f), hidden));
                AirdropAmbushDuration = Config.Bind("Triggers", "Airdrop Ambush Duration", 180f, 
                    new ConfigDescription("How long to hold ambush position near airdrop (seconds).", new AcceptableValueRange<float>(60f, 600f), hidden));
                AirdropVultureChance = Config.Bind("Triggers", "Airdrop Vulture Chance", 75, 
                    new ConfigDescription("Chance % for a bot to vulture an airdrop.", new AcceptableValueRange<int>(0, 100), hidden));

                // Per-Map Multipliers
                FactoryMultiplier = Config.Bind("Maps", "Factory", 0.5f, 
                    new ConfigDescription("Detection range multiplier for Factory", new AcceptableValueRange<float>(0.1f, 3f), hidden));
                LabsMultiplier = Config.Bind("Maps", "Labs", 0.67f, 
                    new ConfigDescription("Detection range multiplier for Labs", new AcceptableValueRange<float>(0.1f, 3f), hidden));
                InterchangeMultiplier = Config.Bind("Maps", "Interchange", 1.0f, 
                    new ConfigDescription("Detection range multiplier for Interchange", new AcceptableValueRange<float>(0.1f, 3f), hidden));
                ReserveMultiplier = Config.Bind("Maps", "Reserve", 1.67f, 
                    new ConfigDescription("Detection range multiplier for Reserve", new AcceptableValueRange<float>(0.1f, 3f), hidden));
                CustomsMultiplier = Config.Bind("Maps", "Customs", 2.0f, 
                    new ConfigDescription("Detection range multiplier for Customs", new AcceptableValueRange<float>(0.1f, 3f), hidden));
                GroundZeroMultiplier = Config.Bind("Maps", "Ground Zero", 1.33f, 
                    new ConfigDescription("Detection range multiplier for Ground Zero", new AcceptableValueRange<float>(0.1f, 3f), hidden));
                StreetsMultiplier = Config.Bind("Maps", "Streets", 2.0f, 
                    new ConfigDescription("Detection range multiplier for Streets", new AcceptableValueRange<float>(0.1f, 3f), hidden));
                ShorelineMultiplier = Config.Bind("Maps", "Shoreline", 2.33f, 
                    new ConfigDescription("Detection range multiplier for Shoreline", new AcceptableValueRange<float>(0.1f, 3f), hidden));
                LighthouseMultiplier = Config.Bind("Maps", "Lighthouse", 2.67f, 
                    new ConfigDescription("Detection range multiplier for Lighthouse", new AcceptableValueRange<float>(0.1f, 3f), hidden));
                WoodsMultiplier = Config.Bind("Maps", "Woods", 3.0f, 
                    new ConfigDescription("Detection range multiplier for Woods", new AcceptableValueRange<float>(0.1f, 3f), hidden));
                


                // Time-of-Day Settings
                NightTimeModifier = Config.Bind("Time", "Night Modifier", true, 
                    new ConfigDescription("Reduce detection range at night (harder to pinpoint sounds)", null, hidden));
                NightRangeMultiplier = Config.Bind("Time", "Night Multiplier", 0.65f, 
                    new ConfigDescription("Detection range multiplier during night (22:00-05:00)", new AcceptableValueRange<float>(0.2f, 1f), hidden));

                // GUI Key - ONLY VISIBLE OPTION IN F12
                GUIKey = Config.Bind("1. Vulture", "Config Menu Key", KeyCode.F7, 
                    "Press this key to open the Vulture configuration menu");

                if (Plugin.DebugLogging.Value)
                {
                    Log.LogInfo("Vulture: Config Loaded.");
                    Log.LogInfo($"Vulture Settings: Chance={VultureChance.Value}%, Range={BaseDetectionRange.Value}m, Ambush={AmbushDuration.Value}s");
                    Log.LogInfo($"Vulture GUI: Press {GUIKey.Value} to open config menu");
                }

                // Note: SAIN integration is checked lazily when the GUI is opened or a bot is processed
                // This avoids load order issues since SAIN may load after Vulture
            
                // Register Combat Sound Listener (gunshots + explosions)
                new CombatSoundListener().Enable();

                // Enable Vision Patches (Bush bypass)
                Luc1dShadow.Vulture.Patches.VisionPatches.Enable();

                // Register BigBrain Layer
                // Need to cover all potential PMC brains
                List<string> brains = new List<string>() 
                { 
                    "PMC",
                    "PmcUsec", 
                    "PmcBear",
                    "Assault"
                };
                BrainManager.AddCustomLayer(typeof(VultureLayer), brains, 101);
                
                if (Plugin.DebugLogging.Value) Log.LogInfo("Vulture: Layer Registered.");

                // Attach GUI component
                gameObject.AddComponent<VultureGUI>();
                if (Plugin.DebugLogging.Value) Log.LogInfo("Vulture: GUI Ready.");

                // Attach Airdrop Listener component
                gameObject.AddComponent<AirdropListener>();
                if (Plugin.DebugLogging.Value) Log.LogInfo("Vulture: Airdrop Listener Ready.");
                // Register Cleanup Patch
                Harmony.CreateAndPatchAll(typeof(GameWorldDisposePatch));
                if (Plugin.DebugLogging.Value) Log.LogInfo("Vulture: Cleanup Patch Registered.");

                // Internal Config Version & Nuclear Reset (MUST BE LAST)
                var configVersion = Config.Bind("Internal", "ConfigVersion", 1, new ConfigDescription("Internal version tracking", null, hidden));
                if (configVersion.Value < LATEST_CONFIG_VERSION)
                {
                    ResetAllSettingsToDefaults();
                    configVersion.Value = LATEST_CONFIG_VERSION;
                    Config.Save();
                }
            }
            catch (System.Exception ex)
            {
                Log.LogError($"Vulture: CRITICAL ERROR in Awake: {ex}");
            }
        }

        private void ResetAllSettingsToDefaults()
        {
            Log.LogWarning("Vulture: Configuration version mismatch. Resetting ALL settings to defaults.");

            DebugLogging.Value = (bool)DebugLogging.DefaultValue;
            BaseDetectionRange.Value = (float)BaseDetectionRange.DefaultValue;
            VultureChance.Value = (int)VultureChance.DefaultValue;
            MultiShotIntensity.Value = (int)MultiShotIntensity.DefaultValue;
            CourageThreshold.Value = (int)CourageThreshold.DefaultValue;
            AmbushDuration.Value = (float)AmbushDuration.DefaultValue;
            SilenceTriggerDuration.Value = (float)SilenceTriggerDuration.DefaultValue;
            AmbushTier1.Value = (float)AmbushTier1.DefaultValue;
            AmbushTier2.Value = (float)AmbushTier2.DefaultValue;
            AmbushTier3.Value = (float)AmbushTier3.DefaultValue;
            EnableBushVision.Value = (bool)EnableBushVision.DefaultValue;
            BushVisionRange.Value = (float)BushVisionRange.DefaultValue;
            NearMissRadius.Value = (float)NearMissRadius.DefaultValue;
            EnableExplosionDetection.Value = (bool)EnableExplosionDetection.DefaultValue;
            IntensityWindow.Value = (float)IntensityWindow.DefaultValue;
            EnablePMCs.Value = (bool)EnablePMCs.DefaultValue;
            EnableScavs.Value = (bool)EnableScavs.DefaultValue;
            EnablePScavs.Value = (bool)EnablePScavs.DefaultValue;
            EnableRaiders.Value = (bool)EnableRaiders.DefaultValue;
            EnableGoons.Value = (bool)EnableGoons.DefaultValue;
            MinSquadSize.Value = (int)MinSquadSize.DefaultValue;
            DetectionRange.Value = (float)DetectionRange.DefaultValue;
            LootGreed.Value = (bool)LootGreed.DefaultValue;
            SilentApproach.Value = (bool)SilentApproach.DefaultValue;
            SilentApproachDistance.Value = (float)SilentApproachDistance.DefaultValue;
            Paranoia.Value = (bool)Paranoia.DefaultValue;
            BossAvoidance.Value = (bool)BossAvoidance.DefaultValue;
            BossAvoidanceRadius.Value = (float)BossAvoidanceRadius.DefaultValue;
            BossZoneDecay.Value = (float)BossZoneDecay.DefaultValue;
            SquadCoordination.Value = (bool)SquadCoordination.DefaultValue;
            EnableVoiceLines.Value = (bool)EnableVoiceLines.DefaultValue;
            FlashlightDiscipline.Value = (bool)FlashlightDiscipline.DefaultValue;
            SmartAmbush.Value = (bool)SmartAmbush.DefaultValue;
            EnableBaiting.Value = (bool)EnableBaiting.DefaultValue;
            BaitingChance.Value = (int)BaitingChance.DefaultValue;
            SAINEnabled.Value = (bool)SAINEnabled.DefaultValue;
            SAINAggressionModifier.Value = (int)SAINAggressionModifier.DefaultValue;
            SAINCautiousModifier.Value = (int)SAINCautiousModifier.DefaultValue;
            EnableAirdropVulturing.Value = (bool)EnableAirdropVulturing.DefaultValue;
            AirdropDetectionRange.Value = (float)AirdropDetectionRange.DefaultValue;
            AirdropAmbushDistanceMin.Value = (float)AirdropAmbushDistanceMin.DefaultValue;
            AirdropAmbushDistanceMax.Value = (float)AirdropAmbushDistanceMax.DefaultValue;
            AirdropAmbushDuration.Value = (float)AirdropAmbushDuration.DefaultValue;
            AirdropVultureChance.Value = (int)AirdropVultureChance.DefaultValue;
            FactoryMultiplier.Value = (float)FactoryMultiplier.DefaultValue;
            LabsMultiplier.Value = (float)LabsMultiplier.DefaultValue;
            InterchangeMultiplier.Value = (float)InterchangeMultiplier.DefaultValue;
            ReserveMultiplier.Value = (float)ReserveMultiplier.DefaultValue;
            CustomsMultiplier.Value = (float)CustomsMultiplier.DefaultValue;
            GroundZeroMultiplier.Value = (float)GroundZeroMultiplier.DefaultValue;
            StreetsMultiplier.Value = (float)StreetsMultiplier.DefaultValue;
            ShorelineMultiplier.Value = (float)ShorelineMultiplier.DefaultValue;
            LighthouseMultiplier.Value = (float)LighthouseMultiplier.DefaultValue;
            WoodsMultiplier.Value = (float)WoodsMultiplier.DefaultValue;
            NightTimeModifier.Value = (bool)NightTimeModifier.DefaultValue;
            NightRangeMultiplier.Value = (float)NightRangeMultiplier.DefaultValue;
            GUIKey.Value = (KeyCode)GUIKey.DefaultValue;

            Log.LogInfo("Vulture: All settings have been reset to tactical defaults.");
        }

        private void Update()
        {
            Luc1dShadow.Vulture.AI.Movement.VultureNavQueue.Update();
        }

        [HarmonyPatch(typeof(EFT.GameWorld), "OnDestroy")]
        public static class GameWorldDisposePatch
        {
            [HarmonyPostfix]
            public static void Postfix()
            {
                VultureLayer.ClearCache();
                CombatSoundListener.Clear();
            }
        }
    }
}
