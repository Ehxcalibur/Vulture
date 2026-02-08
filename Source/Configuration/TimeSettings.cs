using System;
using Comfort.Common;
using EFT;

namespace Luc1dShadow.Vulture
{
    /// <summary>
    /// Provides time-of-day detection range modifiers.
    /// Night raids reduce detection range since sounds are harder to pinpoint in darkness.
    /// </summary>
    public static class TimeSettings
    {
        /// <summary>
        /// Gets the detection range modifier based on current time of day.
        /// </summary>
        public static float GetModifier()
        {
            if (!Plugin.NightTimeModifier.Value) return 1f;
            
            var gameWorld = Singleton<GameWorld>.Instance;
            if (gameWorld?.GameDateTime == null) return 1f;
            
            try
            {
                DateTime gameTime = gameWorld.GameDateTime.Calculate();
                int hour = gameTime.Hour;
                
                // Night: 22:00 - 05:00 → reduced range (harder to locate sounds)
                if (hour >= 22 || hour < 5)
                    return Plugin.NightRangeMultiplier.Value;
                
                // Dawn: 05:00 - 07:00 → transitional
                if (hour >= 5 && hour < 7)
                    return UnityEngine.Mathf.Lerp(Plugin.NightRangeMultiplier.Value, 1f, 0.5f);
                
                // Dusk: 19:00 - 22:00 → transitional
                if (hour >= 19 && hour < 22)
                    return UnityEngine.Mathf.Lerp(1f, Plugin.NightRangeMultiplier.Value, (hour - 19f) / 3f);
                
                // Day: 07:00 - 19:00 → full range
                return 1f;
            }
            catch
            {
                return 1f;
            }
        }
        
        /// <summary>
        /// Gets current time of day as a string for logging.
        /// </summary>
        public static string GetTimeOfDayString()
        {
            var gameWorld = Singleton<GameWorld>.Instance;
            if (gameWorld?.GameDateTime == null) return "Unknown";
            
            try
            {
                DateTime gameTime = gameWorld.GameDateTime.Calculate();
                int hour = gameTime.Hour;
                
                if (hour >= 22 || hour < 5) return "Night";
                if (hour >= 5 && hour < 7) return "Dawn";
                if (hour >= 19 && hour < 22) return "Dusk";
                return "Day";
            }
            catch
            {
                return "Unknown";
            }
        }
    }
}
