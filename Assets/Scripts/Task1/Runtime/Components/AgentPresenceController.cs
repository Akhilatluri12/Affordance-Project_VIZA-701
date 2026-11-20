using UnityEngine;

namespace XRStudy.Task1
{
    /// <summary>
    /// The virtual agent (Layout 2): seated across the table facing the participant, with a slow scripted idle
    /// (breathing + slight sway) and a smoothed head/gaze toward the participant's head. The behaviour is the
    /// same continuous loop in every trial and never reacts to trial events. Hidden completely when absent.
    /// The agent has no colliders, so it cannot affect contacts.
    /// </summary>
    public class AgentPresenceController : MonoBehaviour
    {
        [SerializeField] GameObject m_AgentRoot;      // chair + body; deactivated when absent
        [Tooltip("Bodies indexed by AgentAvatar: Placeholder, VirtualHumanMale, VirtualHumanFemale. Exactly one is active.")]
        [SerializeField] GameObject[] m_Bodies = new GameObject[3];
        [SerializeField] Transform m_Torso;
        [SerializeField] Transform m_Head;
        [SerializeField] Transform m_GazeTarget;      // participant camera

        [Header("Idle (identical in every trial)")]
        public float breathingRate_hz = 0.22f;
        public float breathingScale = 0.015f;
        public float swayDegrees = 1.2f;
        public float swayRate_hz = 0.09f;
        [Header("Gaze")]
        public float gazeSmoothing = 4f;
        public float maxHeadYaw_deg = 35f;
        public float maxHeadPitch_deg = 25f;

        Vector3 m_TorsoScale;
        Quaternion m_TorsoRotation;
        Quaternion m_HeadRestLocal;
        bool m_Cached;

        public bool Present { get; private set; }
        public AgentAvatar Avatar { get; private set; } = AgentAvatar.Placeholder;

        public void ConfigureBodies(GameObject placeholder, GameObject male, GameObject female) => m_Bodies = new[] { placeholder, male, female };

        /// <summary>Shows one agent body; falls back to the placeholder if that virtual human is missing.</summary>
        public void SetAvatar(AgentAvatar avatar)
        {
            int i = (int)avatar;
            if (m_Bodies == null || i >= m_Bodies.Length || !m_Bodies[i]) { avatar = AgentAvatar.Placeholder; i = 0; }
            Avatar = avatar;
            for (int k = 0; k < m_Bodies.Length; k++) if (m_Bodies[k]) m_Bodies[k].SetActive(k == i);
        }

        public void Configure(GameObject root, Transform torso, Transform head, Transform gazeTarget)
        {
            m_AgentRoot = root; m_Torso = torso; m_Head = head; m_GazeTarget = gazeTarget;
        }

        void Cache()
        {
            if (m_Cached) return;
            if (m_Torso) { m_TorsoScale = m_Torso.localScale; m_TorsoRotation = m_Torso.localRotation; }
            if (m_Head) m_HeadRestLocal = m_Head.localRotation;
            m_Cached = true;
        }

        /// <summary>Seats the agent at a table-plane position (chair on the real floor, y = 0 in floor tracking).</summary>
        public void Place(Vector3 positionOnTablePlane, Quaternion facing, float tableHeight)
        {
            if (!m_AgentRoot) return;
            m_AgentRoot.transform.SetPositionAndRotation(new Vector3(positionOnTablePlane.x, 0f, positionOnTablePlane.z), facing);
        }

        public void SetPresent(bool present)
        {
            Present = present;
            if (m_AgentRoot) m_AgentRoot.SetActive(present);
        }

        void LateUpdate()
        {
            // The scripted idle/gaze below is for the placeholder mannequin; virtual humans use their seated idle
            // animation and AgentLookAt (Animator IK) instead.
            if (!Present || !m_AgentRoot || Avatar != AgentAvatar.Placeholder) return;
            Cache();
            float t = Time.time;
            if (m_Torso)
            {
                float b = Mathf.Sin(t * Mathf.PI * 2f * breathingRate_hz);
                m_Torso.localScale = Vector3.Scale(m_TorsoScale, new Vector3(1f + b * breathingScale * 0.5f, 1f + b * breathingScale, 1f + b * breathingScale));
                m_Torso.localRotation = m_TorsoRotation * Quaternion.Euler(0f, 0f, Mathf.Sin(t * Mathf.PI * 2f * swayRate_hz) * swayDegrees);
            }
            if (m_Head)
            {
                Quaternion target = m_Head.parent ? m_Head.parent.rotation * m_HeadRestLocal : m_HeadRestLocal;
                var cam = m_GazeTarget ? m_GazeTarget : (Camera.main ? Camera.main.transform : null);
                if (cam)
                {
                    Vector3 local = m_AgentRoot.transform.InverseTransformDirection(cam.position - m_Head.position);
                    float yaw = Mathf.Clamp(Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg, -maxHeadYaw_deg, maxHeadYaw_deg);
                    float pitch = Mathf.Clamp(-Mathf.Atan2(local.y, new Vector2(local.x, local.z).magnitude) * Mathf.Rad2Deg, -maxHeadPitch_deg, maxHeadPitch_deg);
                    target = m_AgentRoot.transform.rotation * Quaternion.Euler(pitch, yaw, 0f);
                }
                m_Head.rotation = Quaternion.Slerp(m_Head.rotation, target, 1f - Mathf.Exp(-gazeSmoothing * Time.deltaTime));
            }
        }
    }
}
