using Comfort.Common;
using EFT;

namespace Luc1dShadow.Vulture
{
    /// <summary>
    /// Provides per-map detection range multipliers.
    /// </summary>
    public static class MapSettings
    {
        /// <summary>
        /// Gets the detection range multiplier for the current map.
        /// </summary>
        public static float GetMultiplier()
        {
            var gameWorld = Singleton<GameWorld>.Instance;
            if (gameWorld == null) return 1f;
            
            string locationId = gameWorld.LocationId?.ToLower();
            if (string.IsNullOrEmpty(locationId)) return 1f;
            
            return locationId switch
            {
                "factory4_day" or "factory4_night" => Plugin.FactoryMultiplier.Value,
                "laboratory" => Plugin.LabsMultiplier.Value,
                "interchange" => Plugin.InterchangeMultiplier.Value,
                "rezervbase" => Plugin.ReserveMultiplier.Value,
                "bigmap" => Plugin.CustomsMultiplier.Value,  // Customs
                "sandbox" or "sandbox_high" => Plugin.GroundZeroMultiplier.Value,
                "tarkovstreets" => Plugin.StreetsMultiplier.Value,
                "shoreline" => Plugin.ShorelineMultiplier.Value,
                "lighthouse" => Plugin.LighthouseMultiplier.Value,
                "woods" => Plugin.WoodsMultiplier.Value,
                _ => 1f  // Default for unknown maps
            };
        }
        
        /// <summary>
        /// Gets the effective detection range for the current map.
        /// </summary>
        public static float GetEffectiveRange()
        {
            return Plugin.DetectionRange.Value * GetMultiplier() * TimeSettings.GetModifier();
        }
        
        /// <summary>
        /// Gets the current map name for logging.
        /// </summary>
        public static string GetCurrentMapName()
        {
            var gameWorld = Singleton<GameWorld>.Instance;
            return gameWorld?.LocationId ?? "Unknown";
        }
    }
}
