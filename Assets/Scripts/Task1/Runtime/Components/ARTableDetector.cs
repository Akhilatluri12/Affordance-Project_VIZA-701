using System;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
#if UNITY_ANDROID
using UnityEngine.Android;
#endif

namespace XRStudy.Task1
{
    /// <summary>
    /// Optional alternative to controller calibration: uses Quest Space Setup planes classified as "Table"
    /// (AR Foundation plane detection via the Meta OpenXR provider). Picks the table plane under/in front of
    /// the participant and returns the centre of its edge nearest to the head. Manual calibration remains the
    /// reliable default; this needs the user's room setup to include the table and the USE_SCENE permission.
    /// </summary>
    public class ARTableDetector : MonoBehaviour
    {
        const string ScenePermission = "com.oculus.permission.USE_SCENE";

        [SerializeField] ARPlaneManager m_PlaneManager;
        [SerializeField] Transform m_Head;

        public string Status { get; private set; } = "Not started";
        public bool Running => m_PlaneManager && m_PlaneManager.enabled;

        public void Configure(ARPlaneManager planes, Transform head) { m_PlaneManager = planes; m_Head = head; }

        /// <summary>Starts plane detection (asks for the scene permission on device first).</summary>
        public void Begin()
        {
            if (!m_PlaneManager) { Status = "No ARPlaneManager in the scene."; return; }
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!Permission.HasUserAuthorizedPermission(ScenePermission))
            {
                Status = "Requesting room-scan permission...";
                var cb = new PermissionCallbacks();
                cb.PermissionGranted += _ => { m_PlaneManager.enabled = true; Status = "Looking for a table..."; };
                cb.PermissionDenied += _ => Status = "Permission denied. Use controller calibration.";
                Permission.RequestUserPermission(ScenePermission, cb);
                return;
            }
#endif
            m_PlaneManager.enabled = true;
            Status = "Looking for a table...";
        }

        public void Stop()
        {
            if (m_PlaneManager) m_PlaneManager.enabled = false;
        }

        /// <summary>Best table candidate as a calibration, or null when none is found yet.</summary>
        public TableCalibrationData TryGetTable()
        {
            if (!Running || !m_Head) return null;
            ARPlane best = null;
            float bestScore = float.PositiveInfinity;
            Vector3 head = m_Head.position;
            foreach (var plane in m_PlaneManager.trackables)
            {
                if ((plane.classifications & PlaneClassifications.Table) == 0) continue;
                if (plane.alignment != PlaneAlignment.HorizontalUp) continue;
                Vector3 c = plane.center;
                float score = new Vector2(c.x - head.x, c.z - head.z).magnitude + Mathf.Abs(head.y - c.y - 0.45f);
                if (score < bestScore) { bestScore = score; best = plane; }
            }
            if (!best) { Status = "No table found yet. Make sure the table is in the room setup."; return null; }

            // Nearest boundary point to the participant's head (horizontal) = middle of the near edge.
            var boundary = best.boundary;
            Vector3 nearest = best.center;
            float nearestDist = float.PositiveInfinity;
            for (int i = 0; i < boundary.Length; i++)
            {
                Vector2 a = boundary[i], b = boundary[(i + 1) % boundary.Length];
                Vector3 wa = best.transform.TransformPoint(new Vector3(a.x, 0f, a.y));
                Vector3 wb = best.transform.TransformPoint(new Vector3(b.x, 0f, b.y));
                Vector3 p = ClosestOnSegmentXZ(wa, wb, head);
                float d = new Vector2(p.x - head.x, p.z - head.z).magnitude;
                if (d < nearestDist) { nearestDist = d; nearest = p; }
            }
            Vector3 across = best.center - nearest; across.y = 0f;
            if (across.sqrMagnitude < 1e-4f) { across = m_Head.forward; across.y = 0f; }
            Status = "Table found.";
            return new TableCalibrationData
            {
                position = new Vector3(nearest.x, best.center.y, nearest.z),
                yaw_deg = Quaternion.LookRotation(across.normalized, Vector3.up).eulerAngles.y,
                method = "PlaneDetection",
            };
        }

        static Vector3 ClosestOnSegmentXZ(Vector3 a, Vector3 b, Vector3 p)
        {
            Vector2 a2 = new Vector2(a.x, a.z), b2 = new Vector2(b.x, b.z), p2 = new Vector2(p.x, p.z);
            Vector2 ab = b2 - a2;
            float t = ab.sqrMagnitude < 1e-8f ? 0f : Mathf.Clamp01(Vector2.Dot(p2 - a2, ab) / ab.sqrMagnitude);
            return Vector3.Lerp(a, b, t);
        }
    }
}
