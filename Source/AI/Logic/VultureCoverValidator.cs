using EFT;
using UnityEngine;
using UnityEngine.AI;

namespace Luc1dShadow.Vulture.AI
{
    public static class VultureCoverValidator
    {
        public enum CoverheightType
        {
            None,
            Low,  // Requires crouching
            High  // Standing is safe
        }

        public struct ValidationResult
        {
            public bool IsValid;
            public string Reason;
            public CoverheightType HeightType;

            public static ValidationResult Fail(string reason) => new ValidationResult { IsValid = false, Reason = reason, HeightType = CoverheightType.None };
            public static ValidationResult Success(CoverheightType height) => new ValidationResult { IsValid = true, Reason = "OK", HeightType = height };
        }

        public static ValidationResult Validate(Vector3 coverPos, Vector3 enemyPos)
        {
            // 1. PHYSICALITY CHECK: Search for valid physical colliders
            Collider[] colliders = Physics.OverlapSphere(coverPos, 1.2f, LayerMaskClass.HighPolyWithTerrainMask);
            if (colliders.Length == 0) return ValidationResult.Fail("No Physical Object");
            
            Collider bestCol = null;
            float bestHeight = 0f;
            float totalVolume = 0f;
            
            foreach (var col in colliders)
            {
                if (col.bounds.size.y < 0.5f) continue; // Minimum height for any cover
                
                // Volume heuristic: L * W * H
                Vector3 size = col.bounds.size;
                float volume = size.x * size.y * size.z;
                totalVolume += volume;

                if (size.y > bestHeight)
                {
                    bestHeight = size.y;
                    bestCol = col;
                }
            }
            
            // Ensure cover isn't a thin pole or lone piece of grass
            if (totalVolume < 0.4f && bestHeight < 1.0f) 
                return ValidationResult.Fail("Insufficent Volume");

            if (bestCol == null) return ValidationResult.Fail("Cover Too Low");
            
            // 2. MULTI-POINT GEOMETRIC SAFETY
            // Verify that multiple points at this location are actually occluded from the enemy
            Vector3 toEnemy = (enemyPos - coverPos).normalized;
            int occludedPoints = 0;

            // Check Head (1.5m), Chest (1.1m), Stomach (0.7m) with horizontal offsets
            Vector3[] checkOffsets = {
                Vector3.up * 1.5f, 
                Vector3.up * 1.1f, 
                Vector3.up * 0.7f,
                Vector3.up * 1.1f + Vector3.Cross(toEnemy, Vector3.up) * 0.3f,
                Vector3.up * 1.1f - Vector3.Cross(toEnemy, Vector3.up) * 0.3f
            };

            foreach (var offset in checkOffsets)
            {
                Vector3 point = coverPos + offset;
                // Raycast FROM COVER POINT toward enemy. If we hit the cover object immediately, it's occluded.
                if (Physics.Raycast(point, toEnemy, out RaycastHit hit, 2.5f, LayerMaskClass.HighPolyWithTerrainMask))
                {
                    occludedPoints++;
                }
            }

            // Require at least 3/5 points to be occluded to consider this "safe"
            if (occludedPoints < 3) 
                return ValidationResult.Fail("Partial Exposure");

            // 3. Determine Height Type
            CoverheightType heightType = (bestHeight >= 1.4f) ? CoverheightType.High : CoverheightType.Low;
            return ValidationResult.Success(heightType);
        }
    }
}
