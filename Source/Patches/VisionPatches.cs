using EFT;
using HarmonyLib;
using UnityEngine;
using Luc1dShadow.Vulture.AI;
using System.Reflection;

namespace Luc1dShadow.Vulture.Patches
{
    public class VisionPatches
    {
        public static void Enable()
        {
             Harmony.CreateAndPatchAll(typeof(BushVisionPatch));
        }
    }

    /// <summary>
    /// Patch to allow bots to see through foliage within a certain range while in Vulture mode.
    /// This triggers the 'HaveEnemy' state which causes Vulture to yield to combat.
    /// </summary>
    [HarmonyPatch(typeof(EnemyInfo), nameof(EnemyInfo.IsVisible), MethodType.Getter)]
    public static class BushVisionPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(EnemyInfo __instance, ref bool __result)
        {
            // 1. Basic Checks (Configuration & Active State)
            BotOwner bot = __instance.Owner;
            if (!Plugin.EnableBushVision.Value || bot == null || __instance.Person == null) return true;

            // 2. Range Check
            float dist = __instance.Distance;
            if (dist > Plugin.BushVisionRange.Value) return true;

            // 3. Vulture State Check
            bool isVulture = VultureLayer.IsVulture(bot);
            bool isPMC = bot.Profile.Side == EPlayerSide.Usec || bot.Profile.Side == EPlayerSide.Bear;
            
            if (!isVulture && !isPMC) return true;

            // 4. Custom Line-of-Sight Check (Excluding Foliage)
            Vector3 origin = bot.MainParts[BodyPartType.head].Position;
            Vector3 targetPoint = __instance.Person.MainParts[BodyPartType.body].Position;
            
            int mask = LayerMaskClass.HighPolyWithTerrainMask;
            mask &= ~(1 << 13 | 1 << 17); // Remove Bushes (13) and Leaves (17)

            if (!Physics.Raycast(origin, targetPoint - origin, out RaycastHit hit, dist, mask))
            {
                __result = true;
                return false; // Skip original method
            }

            return true;
        }
    }
}
