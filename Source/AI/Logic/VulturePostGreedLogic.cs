using DrakiaXYZ.BigBrain.Brains;
using EFT;
using UnityEngine;
using Luc1dShadow.Vulture.AI;

namespace Luc1dShadow.Vulture
{
    /// <summary>
    /// Handles post-greed hold behavior. Bot moves to cover near a body and holds position.
    /// EnterBy: "Post-Greed Hold"
    /// </summary>
    public class VulturePostGreedLogic : CustomLogic
    {
        private VultureBotState _state;

        public VulturePostGreedLogic(BotOwner botOwner) : base(botOwner) { }

        public override void Start()
        {
            _state = VultureLayer.GetOrCreateState(BotOwner);
            if (Plugin.DebugLogging.Value) Plugin.Log.LogInfo($"[VulturePostGreedLogic] Start for {BotOwner.Profile.Nickname}");
        }

        public override void Stop() { }

        public override void Update(CustomLayer.ActionData data)
        {
            if (_state == null) return;

            try
            {
                // If still moving to cover position
                if (_state.IsMoving)
                {
                    _state.Mover.Update(true);

                    // Advanced Steering: Look-Ahead Smoothing
                    if (_state.Mover.IsSprinting || _state.Mover.IsMoving)
                    {
                        // Dynamic Look-Ahead with Horizon Alignment
                        Vector3 lookPoint = _state.Mover.GetLookAheadPoint(1.5f);
                        
                        // Origin should be the bot's head
                        Vector3 headPos = BotOwner.GetPlayer.MainParts[BodyPartType.head].Position;
                        
                        // Vector from head to target
                        Vector3 lookDir = (lookPoint - headPos).normalized;
                        
                        // Force gaze to be horizontal during movement
                        lookDir.y = 0;
                        
                        BotOwner.Steering.LookToDirection(lookDir, 540f);
                    }

                    if (_state.Mover.HasArrived)
                    {
                        _state.IsMoving = false;
                        _state.Mover.SetSprint(false);
                        float arrivalPose = 0.1f;
                        if (_state.AmbushCoverHeight == Luc1dShadow.Vulture.AI.VultureCoverValidator.CoverheightType.High)
                        {
                           arrivalPose = 1.0f;
                        }
                        BotOwner.SetPose(arrivalPose);
                        try
                        {
                            BotOwner.Mover.Sprint(false);
                            BotOwner.Mover.SetTargetMoveSpeed(0f);
                            BotOwner.GetPlayer.EnableSprint(false);
                            BotOwner.GetPlayer.Move(Vector2.zero);
                        }
                        catch { }

                        // Look at body
                        try { BotOwner.Steering.LookToPoint(_state.PostGreedBodyPosition); } catch { }
                        return; // Exit update after arrival
                    }
                    
                    // Movement Check
                    if (!_state.Mover.IsMoving && !_state.Mover.HasArrived)
                    {
                         if (Time.time - _state.MoveStartTime > 1.5f) 
                         {
                            if (Plugin.DebugLogging.Value) Plugin.Log.LogWarning($"[VulturePostGreedLogic] {BotOwner.Profile.Nickname} movement stopped prematurely. Aborting.");
                            _state.ShouldStop = true;
                            return;
                         }
                    }

                    // Movement timeout
                    if (Time.time - _state.MoveStartTime > 60f)
                    {
                        _state.IsMoving = false;
                        _state.Mover.Stop();
                        _state.ShouldStop = true;
                    }
                    return;
                }

                // Holding at cover position — watch for threats
                float holdPose = 0.1f;
                if (_state.AmbushCoverHeight == Luc1dShadow.Vulture.AI.VultureCoverValidator.CoverheightType.High)
                {
                   holdPose = 1.0f;
                }
                BotOwner.SetPose(holdPose);
                try
                {
                    BotOwner.Mover.Sprint(false);
                    BotOwner.Mover.SetTargetMoveSpeed(0f);
                    BotOwner.GetPlayer.EnableSprint(false);
                    BotOwner.GetPlayer.Move(Vector2.zero);
                }
                catch { }

                // Look at body periodically
                try { BotOwner.Steering.LookToPoint(_state.PostGreedBodyPosition); } catch { }

                // Hold duration
                if (Time.time - _state.PostGreedHoldStartTime > 60f)
                {
                    _state.ShouldStop = true;
                }
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError($"[VulturePostGreedLogic] CRASH in Update: {ex}");
                _state.ShouldStop = true;
            }
        }
    }
}
