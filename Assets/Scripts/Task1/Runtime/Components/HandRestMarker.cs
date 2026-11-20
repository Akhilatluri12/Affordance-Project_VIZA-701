using System.Collections.Generic;
using UnityEngine;

namespace XRStudy.Task1
{
    /// <summary>
    /// Dark-blue oval on the table's near edge where the response hand rests before each trial. Measures the
    /// rest point's horizontal distance/height relative to the marker and its windowed speed, and shows its
    /// state: idle (dark blue), hand detected (light), ready/holding (green ring), disabled (dim).
    /// </summary>
    public class HandRestMarker : MonoBehaviour
    {
        public enum Visual { Hidden, Idle, HandDetected, Ready, Waiting }

        [SerializeField] Transform m_Oval;
        [SerializeField] Renderer m_OvalRenderer;
        [SerializeField] Renderer m_Ring;
        [SerializeField] Color m_IdleColor = new Color(0.12f, 0.30f, 0.45f, 1f);
        [SerializeField] Color m_DetectedColor = new Color(0.25f, 0.55f, 0.85f, 1f);
        [SerializeField] Color m_ReadyColor = new Color(0.30f, 0.85f, 0.45f, 1f);
        [SerializeField] Color m_WaitingColor = new Color(0.10f, 0.18f, 0.25f, 1f);

        readonly Queue<(double t, Vector3 p)> m_History = new Queue<(double, Vector3)>();
        float m_Window = 0.1f;
        MaterialPropertyBlock m_Mpb;
        static readonly int k_BaseColor = Shader.PropertyToID("_BaseColor");
        Visual m_Visual = (Visual)(-1);

        public float Speed { get; private set; }

        public void Configure(Transform oval, Renderer ovalRenderer, Renderer ring) { m_Oval = oval; m_OvalRenderer = ovalRenderer; m_Ring = ring; }
        public void SetWindow(float s) => m_Window = Mathf.Max(0.02f, s);

        public void SetSize(Vector2 size_m)
        {
            if (m_Oval) m_Oval.localScale = new Vector3(size_m.x, m_Oval.localScale.y, size_m.y);
            if (m_Ring) m_Ring.transform.localScale = new Vector3(size_m.x + 0.03f, m_Ring.transform.localScale.y, size_m.y + 0.03f);
        }

        /// <summary>Horizontal distance from the marker centre and height above the table surface.</summary>
        public void Measure(Vector3 worldPoint, out float horizontal, out float height)
        {
            Vector3 local = transform.InverseTransformPoint(worldPoint);
            horizontal = new Vector2(local.x, local.z).magnitude;
            height = local.y;
        }

        public void Sample(double now, Vector3 p)
        {
            m_History.Enqueue((now, p));
            while (m_History.Count > 2 && now - m_History.Peek().t > m_Window) m_History.Dequeue();
            var oldest = m_History.Peek();
            double dt = now - oldest.t;
            Speed = dt > 1e-4 ? (float)((p - oldest.p).magnitude / dt) : 0f;
        }

        public void ClearHistory() { m_History.Clear(); Speed = 0f; }

        public void Show(Visual v)
        {
            if (v == m_Visual) return;
            m_Visual = v;
            bool visible = v != Visual.Hidden;
            if (m_OvalRenderer) m_OvalRenderer.enabled = visible;
            if (m_Ring) m_Ring.enabled = v == Visual.Ready || v == Visual.HandDetected;
            Paint(m_OvalRenderer, v == Visual.HandDetected ? m_DetectedColor : v == Visual.Ready ? m_DetectedColor : v == Visual.Waiting ? m_WaitingColor : m_IdleColor);
            Paint(m_Ring, v == Visual.Ready ? m_ReadyColor : m_DetectedColor);
        }

        void Paint(Renderer r, Color c)
        {
            if (!r) return;
            m_Mpb ??= new MaterialPropertyBlock();
            r.GetPropertyBlock(m_Mpb);
            m_Mpb.SetColor(k_BaseColor, c);
            r.SetPropertyBlock(m_Mpb);
        }
    }
}
