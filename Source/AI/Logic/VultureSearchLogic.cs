using DrakiaXYZ.BigBrain.Brains;
using EFT;
using UnityEngine;
using UnityEngine.AI;
using Luc1dShadow.Vulture.AI;

namespace Luc1dShadow.Vulture
{
    /// <summary>
    /// Handles area clearing/searching when no body is found after greed push.
    /// EnterBy: "Tactical Clear"
    /// </summary>
    public class VultureSearchLogic : CustomLogic
    {
        private VultureBotState _state;
        private float _nextScanTime;

        public VultureSearchLogic(BotOwner botOwner) : base(botOwner) { }

        public override void Start()
        {
            _state = VultureLayer.GetOrCreateState(BotOwner);
            if (Plugin.DebugLogging.Value) Plugin.Log.LogInfo($"[VultureSearchLogic] Start for {BotOwner.Profile.Nickname}");
            
            MoveToNextSearchPoint();
        }

        public override void Stop() { }

        public override void Update(CustomLayer.ActionData data)
        {
            if (_state == null) return;

            try
            {
                // Safety yield to SAIN/Combat handled by layer

                if (_state.IsMoving)
                {
                    _state.Mover.Update(true);

                    // Look height normalization
                    if (_state.Mover.IsMoving)
                    {
                        // Look further ahead (7m) to keep gaze level/horizon-focused
                        Vector3 lookTarget = _state.Mover.GetLookAheadPoint(7.0f);
                        
                        // Origin from "throat/head" level rather than feet (Position)
                        Vector3 eyePos = BotOwner.Position + Vector3.up * 1.5f;
                        
                        // Force look target to be at the same vertical height as eyes (horizon level)
                        lookTarget.y = eyePos.y;
                        
                        Vector3 lookDir = (lookTarget - eyePos).normalized;
                        if (lookDir != Vector3.zero)
                        {
                            BotOwner.Steering.LookToDirection(lookDir, 540f);
                        }
                    }

                    if (_state.Mover.HasArrived)
                    {
                        _state.IsMoving = false;
                        _state.SearchWaitStartTime = Time.time;
                        BotOwner.SetPose(0.8f);
                        _nextScanTime = Time.time + 1f;
                    }

                    // Timeout
                    if (Time.time - _state.MoveStartTime > 30f)
                    {
                        _state.IsMoving = false;
                        _state.Mover.Stop();
                        MoveToNextSearchPoint();
                    }
                    return;
                }

                // Wait and Scan at point
                if (Time.time - _state.SearchWaitStartTime < 4f)
                {
                    _state.Mover.Stop();
                    if (Time.time > _nextScanTime)
                    {
                        _nextScanTime = Time.time + Random.Range(1.5f, 2.5f);
                        Vector3 scanDir = Random.insideUnitSphere * 10f;
                        scanDir.y = 1.2f;
                        BotOwner.Steering.LookToDirection(scanDir, 180f);
                    }
                }
                else
                {
                    MoveToNextSearchPoint();
                }
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError($"[VultureSearchLogic] CRASH in Update: {ex}");
                _state.ShouldStop = true;
            }
        }

        private void MoveToNextSearchPoint()
        {
            _state.CurrentSearchPointIndex++;
            if (_state.CurrentSearchPointIndex >= _state.SearchPoints.Count)
            {
                if (Plugin.DebugLogging.Value) Plugin.Log.LogInfo($"[VultureSearchLogic] {BotOwner.Profile.Nickname} finished search. Stopping.");
                _state.ShouldStop = true;
                return;
            }

            Vector3 nextPoint = _state.SearchPoints[_state.CurrentSearchPointIndex];
            _state.Mover.SetSprint(false); // Move cautiously while clearing
            if (_state.Mover.MoveTo(nextPoint, 0.6f))
            {
                _state.IsMoving = true;
                _state.MoveStartTime = Time.time;
                BotOwner.SetPose(1.0f);
                if (Plugin.DebugLogging.Value) Plugin.Log.LogInfo($"[VultureSearchLogic] {BotOwner.Profile.Nickname} moving to search point {nextPoint}");
            }
            else
            {
                MoveToNextSearchPoint();
            }
        }
    }
}
