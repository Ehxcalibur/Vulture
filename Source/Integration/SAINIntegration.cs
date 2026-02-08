using BepInEx.Bootstrap;
using EFT;
using HarmonyLib;
using System;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Luc1dShadow.Vulture.Integration
{
    public static class SAINIntegration
    {
        private static bool? _isSAINLoaded;
        private static Type _botComponentType;
        private static PropertyInfo _infoProperty;
        private static PropertyInfo _personalityProperty;
        private static MethodInfo _getComponentMethod;

        public static bool IsSAINLoaded
        {
            get
            {
                // If already confirmed loaded and initialized, return true
                if (_isSAINLoaded == true && _botComponentType != null)
                {
                    return true;
                }
                
                // Re-check each time until we find SAIN (handles load order issues)
                bool found = Chainloader.PluginInfos.ContainsKey("me.sol.sain") 
                          || Chainloader.PluginInfos.ContainsKey("com.solarint.sain")
                          || Chainloader.PluginInfos.ContainsKey("SAIN")
                          || Chainloader.PluginInfos.Keys.Any(k => k.ToLower().Contains("sain"));
                
                // Fallback: Check if SAIN assembly is loaded
                if (!found)
                {
                    foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (assembly.GetName().Name.ToLower().Contains("sain"))
                        {
                            found = true;
                            break;
                        }
                    }
                }
                
                if (found && _isSAINLoaded != true)
                {
                    _isSAINLoaded = true;
                    InitializeReflection();
                    Plugin.Log.LogInfo("[Vulture] SAIN detected! Integration enabled.");
                }
                else if (!found && _isSAINLoaded == null)
                {
                    // Only log once when we first check and don't find it
                    // Don't set to false yet - we'll keep checking
                }
                
                return _isSAINLoaded == true;
            }
        }

        private static void InitializeReflection()
        {
            try
            {
                // SAIN.Components.BotComponent
                _botComponentType = Type.GetType("SAIN.Components.BotComponent, SAIN");
                if (_botComponentType == null)
                {
                    // Try looking through all loaded assemblies
                    foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        _botComponentType = assembly.GetType("SAIN.Components.BotComponent");
                        if (_botComponentType != null) break;
                    }
                }

                if (_botComponentType != null)
                {
                    // BotComponent.Info -> SAINBotInfoClass
                    _infoProperty = _botComponentType.GetProperty("Info");
                    
                    // SAINBotInfoClass.Personality -> EPersonality (enum)
                    if (_infoProperty != null)
                    {
                        _personalityProperty = _infoProperty.PropertyType.GetProperty("Personality");
                    }
                    
                    // GameObject.GetComponent<BotComponent>()
                    _getComponentMethod = typeof(GameObject).GetMethod("GetComponent", new Type[] { }).MakeGenericMethod(_botComponentType);
                }
                
                if (_botComponentType == null || _infoProperty == null || _personalityProperty == null)
                {
                    Plugin.Log.LogError("Vulture: SAIN detected but failed to reflect types. Integration disabled.");
                    _isSAINLoaded = false;
                }
                else
                {
                    Plugin.Log.LogInfo("Vulture: SAIN Integration Initialized Successfully.");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Vulture: Error initializing SAIN reflection: {ex}");
                _isSAINLoaded = false;
            }
        }

        public static string GetPersonality(BotOwner botOwner)
        {
            if (!IsSAINLoaded || botOwner == null) return null;

            try
            {
                // Get BotComponent from BotOwner.GetPlayer.gameObject
                // OR directly from botOwner.gameObject if SAIN attaches it there (it attaches to Player object usually)
                
                var player = botOwner.GetPlayer;
                if (player == null) return null;
                
                // Invoke GetComponent<BotComponent>()
                var botComponent = _getComponentMethod.Invoke(player.gameObject, null);
                if (botComponent == null) return null;
                
                // Get Info
                var info = _infoProperty.GetValue(botComponent);
                if (info == null) return null;
                
                // Get Personality Enum
                var personalityEnum = _personalityProperty.GetValue(info);
                
                return personalityEnum.ToString();
            }
            catch (Exception)
            {
                // Suppress errors during runtime to avoid log spam if something breaks
                return null;
            }
        }
        
        public static bool IsAggressivePersonality(string personality)
        {
            if (string.IsNullOrEmpty(personality)) return false;
            return personality == "GigaChad" || personality == "Chad" || personality == "Wreckless";
        }

        public static bool IsCautiousPersonality(string personality)
        {
            if (string.IsNullOrEmpty(personality)) return false;
            return personality == "Rat" || personality == "Timmy" || personality == "Coward";
        }
    }
}
