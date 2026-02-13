using System.Collections.Generic;
using EFT;
using UnityEngine;
using UnityEngine.AI;

namespace Luc1dShadow.Vulture.AI.Movement
{
    /// <summary>
    /// Monitors bot movement and detects stuck states.
    /// Split into "Soft" (Velocity-based) and "Hard" (Position-based) detection tiers.
    /// </summary>
    public class VultureStuckDetector
    {
        public enum RemediationAction
        {
            None,
            Vault,
            Jump,
            Repath,
            Teleport,
            ForceArrival
        }

        private readonly VelocityStuckHandler _velocityHandler;
        private readonly PositionStuckHandler _positionHandler;
        
        // Exposed for Mover to query current "Tier" of stuckness
        public int CurrentTier => Mathf.Max(_velocityHandler.Tier, _positionHandler.Tier);
        public bool IsStuck => CurrentTier > 0;
        public float StuckDuration => Mathf.Max(_velocityHandler.StuckDuration, _positionHandler.StuckDuration);

        public VultureStuckDetector()
        {
            _velocityHandler = new VelocityStuckHandler();
            _positionHandler = new PositionStuckHandler();
        }

        public void Reset()
        {
            _velocityHandler.Reset();
            _positionHandler.Reset();
        }

        public RemediationAction Update(Vector3 currentPos, float intendedSpeed, float distToDest, float pathSegmentLength = 0f)
        {
            // Update both detection systems
            var velocityAction = _velocityHandler.Update(currentPos, intendedSpeed);
            var positionAction = _positionHandler.Update(currentPos, intendedSpeed, distToDest, pathSegmentLength);

            // Prioritize position-based detection (handles long-term stucks)
            if (positionAction != RemediationAction.None)
                return positionAction;

            return velocityAction;
        }

        // =========================================================================================
        // VELOCITY-BASED STUCK DETECTION (Soft Stuck)
        // Detects when bot *should* be moving but isn't (stuck on small prop).
        // =========================================================================================
        private class VelocityStuckHandler
        {
            // Tuning Constants
            private const float SPEED_THRESHOLD_RATIO = 0.25f; // If actual speed < 25% of intended
            private const float VAULT_DELAY = 1.5f;
            private const float JUMP_DELAY = 3.0f; // 1.5 + 1.5
            
            private float _stuckTimer;
            private Vector3 _lastPos;
            private float _lastUpdateTime;
            private float _smoothedSpeed;
            
            public int Tier { get; private set; }
            public float StuckDuration => _stuckTimer;

            public void Reset()
            {
                _stuckTimer = 0f;
                _smoothedSpeed = 0f;
                Tier = 0;
                _lastUpdateTime = Time.time;
            }

            public RemediationAction Update(Vector3 currentPos, float intendedSpeed)
            {
                float now = Time.time;
                float dt = now - _lastUpdateTime;
                _lastUpdateTime = now;

                if (dt <= 0.0001f) return RemediationAction.None;

                // Calculate actual speed (Horizontal only)
                Vector3 moveDelta = currentPos - _lastPos;
                moveDelta.y = 0;
                float actualSpeed = moveDelta.magnitude / dt;
                _lastPos = currentPos;

                // ASYMMETRIC SPEED BUFFERING
                if (actualSpeed < _smoothedSpeed)
                {
                    _smoothedSpeed = actualSpeed; // Instant decrease
                }
                else
                {
                    _smoothedSpeed = Mathf.Lerp(_smoothedSpeed, actualSpeed, dt * 1.0f); // Slow increase
                }

                // Check intersection of Intention vs Reality
                float threshold = intendedSpeed * 0.4f; 
                
                // Only consider stuck if we INTEND to move reasonably fast (> 0.1 speed)
                // And are actually moving very slow.
                if (intendedSpeed > 0.1f && _smoothedSpeed < threshold)
                {
                    _stuckTimer += dt;
                }
                else
                {
                    // If we are moving fine, reset.
                    if (actualSpeed > threshold)
                    {
                        Reset();
                        return RemediationAction.None;
                    }
                }

                // Remediation Logic
                if (_stuckTimer > VAULT_DELAY && Tier == 0)
                {
                    Tier = 1;
                    return RemediationAction.Vault;
                }
                
                if (_stuckTimer > JUMP_DELAY && Tier == 1)
                {
                    Tier = 2; // Checkmate
                    return RemediationAction.Jump;
                }

                return RemediationAction.None;
            }
        }

        // =========================================================================================
        // POSITION-BASED STUCK DETECTION (Hard Stuck)
        // Detects being trapped in an area despite "moving" (sliding against wall).
        // =========================================================================================
        private class PositionStuckHandler
        {
            // Tuning Constants: 1.0m radius, 5s retry, 10s teleport
            private const float STUCK_RADIUS = 1.0f; 
            private const float REPATH_DELAY = 5.0f;  
            private const float TELEPORT_DELAY = 10.0f; // 5 + 5
            
            // Leniency for ramps/pipes
            private const float RAMP_VERTICAL_THRESHOLD = 1.0f; 
            private const float RAMP_LENIENCY_MULTIPLIER = 1.5f; 
            
            private Vector3 _anchorPos;
            private float _anchorTime;
            private float _anchorHeight;
            private int _repathCount;
            private float _lastRepathTime;
            public int Tier { get; private set; }
            public float StuckDuration { get; private set; }
            
            // Using fixed radius is safer for Vulture.

            public PositionStuckHandler()
            {
                Reset();
            }

            public void Reset()
            {
                _anchorPos = Vector3.zero;
                _anchorTime = 0f;
                _anchorHeight = 0f;
                Tier = 0;
                
                if (Time.time - _lastRepathTime > 15f)
                    _repathCount = 0;
            }

            public RemediationAction Update(Vector3 currentPos, float intendedSpeed, float distToDest, float pathSegmentLength = 0f)
            {
                // Init anchor if needed
                if (_anchorTime == 0f)
                {
                    _anchorPos = currentPos;
                    _anchorTime = Time.time;
                    _anchorHeight = currentPos.y;
                    return RemediationAction.None;
                }

                // Check distance from anchor
                Vector3 delta = currentPos - _anchorPos;
                float verticalDelta = Mathf.Abs(currentPos.y - _anchorHeight);
                delta.y = 0; 
                
                float distSqr = delta.sqrMagnitude;

                // If we moved outside radius, we are NOT stuck. Reset anchor.
                // VERTICAL TOLERANCE: Increased for stairs/climbing
                if (distSqr > STUCK_RADIUS * STUCK_RADIUS || verticalDelta > 1.8f)
                {
                    _anchorPos = currentPos;
                    _anchorTime = Time.time;
                    _anchorHeight = currentPos.y;
                    Tier = 0;
                    return RemediationAction.None;
                }

                // We are inside the stuck radius. How long?
                StuckDuration = Time.time - _anchorTime;

                // Only count time if we INTEND to move
                if (intendedSpeed < 0.1f)
                {
                    _anchorTime = Time.time; // Drag anchor forward
                    StuckDuration = 0f;
                    return RemediationAction.None;
                }

                // SAFE ARRIVAL CHECK
                if (distToDest < 1.5f && StuckDuration > 3.0f)
                {
                    return RemediationAction.ForceArrival;
                }

                // LENIENCY
                bool isRampOrPipe = verticalDelta > RAMP_VERTICAL_THRESHOLD || 
                                    (pathSegmentLength > 0f && pathSegmentLength < 3.0f && verticalDelta > 0.2f);
                
                float repathDelay = REPATH_DELAY;
                float teleportDelay = TELEPORT_DELAY;
                
                if (isRampOrPipe)
                {
                    repathDelay *= RAMP_LENIENCY_MULTIPLIER;
                    teleportDelay *= RAMP_LENIENCY_MULTIPLIER;
                }

                // Remediation
                if (StuckDuration > repathDelay && Tier == 0)
                {
                    if (_repathCount >= 2)
                    {
                        Tier = 2; 
                        _repathCount = 0;
                        Plugin.Log.LogWarning($"[VultureStuckDetector] Repath loop detected ({_repathCount+1}). Escalating to Teleport.");
                        return RemediationAction.Teleport;
                    }

                    Tier = 1;
                    _repathCount++;
                    _lastRepathTime = Time.time;
                    _anchorTime = Time.time; 
                    
                    return RemediationAction.Repath;
                }
                
                if (StuckDuration > teleportDelay)
                {
                    Tier = 2;
                    _anchorPos = currentPos;
                    _anchorTime = Time.time;
                    _anchorHeight = currentPos.y;
                    return RemediationAction.Teleport;
                }

                return RemediationAction.None;
            }
        }
    }
}
