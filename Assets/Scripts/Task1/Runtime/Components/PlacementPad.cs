using UnityEngine;

namespace XRStudy.Task1
{
    /// <summary>
    /// Common placement pad for the pick & place task: a flat virtual marker on the real table with no collider
    /// and no snapping. Evaluates containment (base reference inside the pad bounds and vertical tolerance),
    /// tilt and speed for the stability check.
    /// </summary>
    public class PlacementPad : MonoBehaviour
    {
        [SerializeField] Transform m_Visual;
        [SerializeField] Renderer m_Renderer;
        Vector2 m_Size = new Vector2(0.14f, 0.14f);

        public Vector2 Size => m_Size;
        public Pose Pose => new Pose(transform.position, transform.rotation);

        public void Configure(Transform visual, Renderer renderer) { m_Visual = visual; m_Renderer = renderer; }

        public void Place(Pose pose, Vector2 size)
        {
            m_Size = size;
            transform.SetPositionAndRotation(pose.position, pose.rotation);
            if (m_Visual) m_Visual.localScale = new Vector3(size.x, 1f, size.y);
        }

        public void SetVisible(bool v) { if (m_Renderer) m_Renderer.enabled = v; foreach (var r in GetComponentsInChildren<Renderer>(true)) r.enabled = v; }

        public PlacementSample Evaluate(ProtocolConfig cfg, StudyMug mug, Pose table)
        {
            if (!mug || cfg == null) return default;
            return PlacementEvaluator.Evaluate(cfg, Pose, m_Size, mug.transform.position, mug.transform.up,
                mug.Speed, mug.AngularSpeedDeg, mug.IsSelected, table);
        }
    }
}
