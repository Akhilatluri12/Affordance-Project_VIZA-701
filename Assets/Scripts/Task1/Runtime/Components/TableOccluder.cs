using UnityEngine;

namespace XRStudy.Task1
{
    /// <summary>
    /// Invisible, depth-only box matching the real table (near edge at the table origin, extending across the
    /// table and down to the floor). Virtual content behind or under the real table is hidden, so passthrough
    /// shows the real table in front of the agent's legs and chair.
    /// </summary>
    public class TableOccluder : MonoBehaviour
    {
        [SerializeField] Transform m_Box;
        [SerializeField] BoxCollider m_Surface;

        public void Configure(Transform box, BoxCollider surface = null) { m_Box = box; m_Surface = surface; }

        /// <summary>Width/depth of the real table; height = table surface height above the floor.</summary>
        public void Apply(Pose tableOrigin, float width, float depth)
        {
            if (!m_Box) return;
            float height = Mathf.Max(0.05f, tableOrigin.position.y);
            m_Box.SetPositionAndRotation(tableOrigin.position + tableOrigin.rotation * new Vector3(0f, -height * 0.5f - 0.004f, depth * 0.5f), tableOrigin.rotation);
            m_Box.localScale = new Vector3(width, height, depth);
            // Physical stand-in for the real table top so released mugs rest on it (pick & place).
            if (m_Surface)
            {
                m_Surface.transform.SetPositionAndRotation(tableOrigin.position + tableOrigin.rotation * new Vector3(0f, -0.01f, depth * 0.5f), tableOrigin.rotation);
                m_Surface.size = new Vector3(width, 0.02f, depth);
                m_Surface.center = Vector3.zero;
            }
        }
    }
}
