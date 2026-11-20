using UnityEngine;

namespace XRStudy.Task1
{
    /// <summary>
    /// Puts a humanoid avatar playing a seated idle onto the chair: after the first animated frames it shifts the
    /// avatar so the hips sit at a fixed point relative to the agent anchor (chair seat), then stops. The result
    /// is deterministic, so the agent occupies the same place in every block and session.
    /// </summary>
    [RequireComponent(typeof(Animator))]
    public class SeatedAvatarAligner : MonoBehaviour
    {
        [Tooltip("Target hips position in the agent anchor's space (anchor = chair footprint on the floor, +z toward the participant).")]
        public Vector3 hipsInAnchor = new Vector3(0f, 0.53f, -0.02f);
        public int alignAfterFrames = 2;

        Animator m_Animator;
        int m_Frames;
        bool m_Done;

        // Aligns once per session (not on every show/hide), so the seated position never drifts between blocks.
        void OnEnable() { m_Frames = 0; m_Animator = GetComponent<Animator>(); }

        void LateUpdate()
        {
            if (m_Done || !m_Animator || !m_Animator.isHuman) return;
            if (++m_Frames < alignAfterFrames) return;
            var hips = m_Animator.GetBoneTransform(HumanBodyBones.Hips);
            var anchor = transform.parent;
            if (!hips || !anchor) { m_Done = true; return; }
            Vector3 delta = anchor.TransformPoint(hipsInAnchor) - hips.position;
            transform.position += delta;
            m_Done = true;
        }
    }
}
