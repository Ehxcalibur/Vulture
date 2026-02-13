using UnityEngine;
using UnityEngine.AI;

namespace Luc1dShadow.Vulture.AI.Movement
{
    /// <summary>
    /// Encapsulates a NavMeshPath with corner tracking and progress management.
    /// </summary>
    public class VulturePath
    {
        private NavMeshPath _navPath;
        private Vector3[] _corners;
        
        public int CurrentCornerIndex { get; private set; }
        public NavMeshPathStatus Status => _navPath?.status ?? NavMeshPathStatus.PathInvalid;
        public bool HasPath => _corners != null && _corners.Length > 0;
        public bool IsComplete => HasPath && CurrentCornerIndex >= _corners.Length;
        public bool IsLastCorner => HasPath && CurrentCornerIndex >= _corners.Length - 1;
        public Vector3[] Corners => _corners;
        
        public Vector3 CurrentCorner
        {
            get
            {
                if (!HasPath || CurrentCornerIndex >= _corners.Length) return Vector3.zero;
                return _corners[CurrentCornerIndex];
            }
        }

        public Vector3 FinalDestination
        {
            get
            {
                if (!HasPath) return Vector3.zero;
                return _corners[_corners.Length - 1];
            }
        }

        public VulturePath()
        {
            _navPath = new NavMeshPath();
        }

        /// <summary>
        /// Calculates a new path from start to target.
        /// </summary>
        public bool Calculate(Vector3 startPos, Vector3 targetPos)
        {
            _navPath.ClearCorners();
            CurrentCornerIndex = 0;
            
            if (!NavMesh.CalculatePath(startPos, targetPos, NavMesh.AllAreas, _navPath))
            {
                _corners = null;
                return false;
            }
            
            _corners = _navPath.corners;
            
            // Skip corner 0 if it's basically our start position
            if (_corners.Length > 1 && (startPos - _corners[0]).sqrMagnitude < 0.25f)
            {
                CurrentCornerIndex = 1;
            }
            
            return _navPath.status == NavMeshPathStatus.PathComplete;
        }

        /// <summary>
        /// Applies an externally calculated NavMeshPath (e.g. from async request).
        /// </summary>
        public void ApplyNavMeshPath(NavMeshPath path)
        {
            _navPath = path;
            _corners = path.corners;
            CurrentCornerIndex = 0;
        }

        /// <summary>
        /// Advances to the next corner in the path.
        /// </summary>
        public void AdvanceCorner()
        {
            if (HasPath && CurrentCornerIndex < _corners.Length - 1)
            {
                CurrentCornerIndex++;
            }
        }

        /// <summary>
        /// Clears the current path.
        /// </summary>
        public void Clear()
        {
            _navPath?.ClearCorners();
            _corners = null;
            CurrentCornerIndex = 0;
        }

        /// <summary>
        /// Checks if we've reached the current corner (within threshold).
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public bool HasReachedCurrentCorner(Vector3 botPos, float threshold = 0.5f)
        {
            if (!HasPath) return true;
            
            Vector3 corner = CurrentCorner;
            Vector3 toCorner = corner - botPos;
            
            // Vertical Safety: If the height difference is significant, don't consider reached 
            // unless we are really close. 
            float verticalDist = Mathf.Abs(toCorner.y);
            if (verticalDist > 1.2f) return false;

            // Horizontal check
            toCorner.y = 0; 
            
            // Calculate adaptive threshold based on path segment direction.
            if (!IsLastCorner)
            {
                Vector3 nextLeg = _corners[CurrentCornerIndex + 1] - corner;
                nextLeg.y = 0;
                
                if (toCorner.sqrMagnitude > 0.001f && nextLeg.sqrMagnitude > 0.001f)
                {
                    float dot = Vector3.Dot(toCorner.normalized, nextLeg.normalized);
                    // If it's a straight-ish path (dot < -0.8), we can skip early
                    if (dot > 0.8f) threshold *= 1.5f; 
                }
            }

            return toCorner.sqrMagnitude <= threshold * threshold;
        }

        /// <summary>
        /// Calculates the maximum angle change (jitter) in the path ahead.
        /// Used to prevent sprinting on jagged paths.
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public float CalculatePathAngleJitter(float sampleDist = 5f)
        {
            if (!HasPath || CurrentCornerIndex >= _corners.Length - 2)
                return 0f;

            float angleMax = 0f;
            float distanceAccumulated = 0f;
            int currentIndex = CurrentCornerIndex;

            // Accumulate distance and check angles until we exceed lookAheadDistance
            while (currentIndex < _corners.Length - 2 && distanceAccumulated < sampleDist)
            {
                var pointA = _corners[currentIndex];
                var pointB = _corners[currentIndex + 1];
                var pointC = _corners[currentIndex + 2];

                // Accumulate the segment distance
                distanceAccumulated += Vector3.Distance(pointA, pointB);

                // If we've exceeded the look-ahead distance, stop
                if (distanceAccumulated > sampleDist)
                    break;

                // Calculate direction vectors
                var directionAb = (pointB - pointA).normalized;
                var directionBc = (pointC - pointB).normalized;

                // Calculate angle between the two direction vectors
                float angle = Vector3.Angle(directionAb, directionBc);
                
                if (angle > angleMax)
                    angleMax = angle;

                currentIndex++;
            }
            
            return angleMax;
        }

        /// <summary>
        /// Calculates a point at a specific distance along the path from current position.
        /// Primarily used for smooth head tracking (look-ahead).
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public Vector3 CalcForwardPoint(Vector3 botPos, float lookAheadDistance)
        {
            if (!HasPath) return botPos;
            if (CurrentCornerIndex >= _corners.Length) return FinalDestination;

            float remainingDist = lookAheadDistance;
            Vector3 lastPoint = botPos;
            int cornerIdx = CurrentCornerIndex;

            while (remainingDist > 0 && cornerIdx < _corners.Length)
            {
                Vector3 toCorner = _corners[cornerIdx] - lastPoint;
                float distToCorner = toCorner.magnitude;

                if (distToCorner >= remainingDist)
                {
                    // Target point is on this segment
                    return lastPoint + toCorner.normalized * remainingDist;
                }

                // Consume this segment
                remainingDist -= distToCorner;
                lastPoint = _corners[cornerIdx];
                cornerIdx++;
            }

            // Ran out of path
            return FinalDestination;
        }
    }
}
