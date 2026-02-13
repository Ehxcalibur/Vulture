using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace Luc1dShadow.Vulture.AI.Movement
{
    /// <summary>
    /// Handles asynchronous NavMesh pathfinding requests to prevent frame spikes.
    /// Processing is limited to a fixed number of requests per frame.
    /// </summary>
    public static class VultureNavQueue
    {
        private struct PathRequest
        {
            public Vector3 Start;
            public Vector3 End;
            public Action<NavMeshPath, bool> Callback;
        }

        private static readonly Queue<PathRequest> _queue = new Queue<PathRequest>();
        private static readonly Stack<NavMeshPath> _pathPool = new Stack<NavMeshPath>();
        private static readonly int MAX_REQUESTS_PER_FRAME = 3; // Tune based on performance

        private static NavMeshPath GetPath()
        {
            if (_pathPool.Count > 0)
            {
                var path = _pathPool.Pop();
                path.ClearCorners(); // Ensure it's empty
                return path;
            }
            return new NavMeshPath();
        }

        private static void ReturnPath(NavMeshPath path)
        {
            if (path == null) return;
            path.ClearCorners();
            _pathPool.Push(path);
        }

        public static void SubmitRequest(Vector3 start, Vector3 end, Action<NavMeshPath, bool> callback)
        {
            _queue.Enqueue(new PathRequest
            {
                Start = start,
                End = end,
                Callback = callback
            });
        }

        public static void Update()
        {
            int processed = 0;
            while (_queue.Count > 0 && processed < MAX_REQUESTS_PER_FRAME)
            {
                var request = _queue.Dequeue();
                ProcessRequest(request);
                processed++;
            }
        }

        private static void ProcessRequest(PathRequest request)
        {
            NavMeshPath path = GetPath();
            
            // SNAPPING: Ensure start and end points are actually on the NavMesh
            // This prevents failures if the bot is standing on a prop or debris.
            Vector3 snappedStart = request.Start;
            Vector3 snappedEnd = request.End;

            if (NavMesh.SamplePosition(request.Start, out NavMeshHit startHit, 2.0f, NavMesh.AllAreas))
                snappedStart = startHit.position;

            if (NavMesh.SamplePosition(request.End, out NavMeshHit endHit, 2.0f, NavMesh.AllAreas))
                snappedEnd = endHit.position;

            bool success = NavMesh.CalculatePath(snappedStart, snappedEnd, NavMesh.AllAreas, path);
            
            // Validate if the path actually reaches the target (or close enough)
            if (success && path.corners.Length > 0)
            {
                float distToTarget = Vector3.Distance(path.corners[path.corners.Length - 1], snappedEnd);
                if (distToTarget > 1.5f) success = false; // Path is "partial" or blocked
            }
            else
            {
                success = false;
            }

            try
            {
                request.Callback?.Invoke(path, success);
            }
            catch (Exception ex)
            {
                if (Plugin.DebugLogging.Value)
                    Plugin.Log.LogError($"[VultureNavQueue] Callback error: {ex}");
            }
            finally
            {
                ReturnPath(path);
            }
        }

        public static void Clear()
        {
            _queue.Clear();
        }
    }
}
