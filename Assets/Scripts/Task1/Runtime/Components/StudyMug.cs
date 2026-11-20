using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace XRStudy.Task1
{
    /// <summary>
    /// One matched virtual mug. Pivot at the base centre; handle along local +x; identity sticker on local −z
    /// (faces the participant at yaw 0). All mugs share geometry, colliders, physics, attach settings, contact
    /// volume, hover and cue; only the colour, label and shape differ (spec FR-04).
    /// Touch task: kinematic and not grabbable (contact is geometric, so a touch never moves a mug).
    /// Pick & place: grabbable with the designated hand (near only) and physically resting on the table.
    /// </summary>
    [RequireComponent(typeof(OwnershipTag))]
    public class StudyMug : MonoBehaviour
    {
        [Header("Geometry (metres, local)")]
        [SerializeField] float m_BodyRadius = 0.04f;
        [SerializeField] float m_BodyHeight = 0.10f;
        [SerializeField] Vector3 m_HandleCenter = new Vector3(0.056f, 0.05f, 0f);
        [SerializeField] Vector3 m_HandleHalfSize = new Vector3(0.016f, 0.026f, 0.007f);

        [Header("Visuals")]
        [SerializeField] Renderer[] m_BodyRenderers;
        [SerializeField] Renderer[] m_CueRenderers;
        [SerializeField] TextMeshPro m_Label;
        [SerializeField] Renderer m_ShapeDiamond;
        [SerializeField] Renderer m_ShapeTriangle;
        [SerializeField] Renderer m_ShapeSquare;

        OwnershipTag m_Tag;
        XRGrabInteractable m_Grab;
        Rigidbody m_Body;
        Vector3 m_LastPos;
        Quaternion m_LastRot;
        bool m_HasLast;
        MaterialPropertyBlock m_Mpb;
        Color m_BaseColor = Color.white;
        bool m_Hover, m_Cue;
        Color m_CueColor = Color.yellow;
        float m_CueIntensity = 2f;
        Coroutine m_Flash;
        static readonly int k_BaseColor = Shader.PropertyToID("_BaseColor");
        static readonly int k_EmissionColor = Shader.PropertyToID("_EmissionColor");

        public OwnershipTag Tag => m_Tag ? m_Tag : m_Tag = GetComponent<OwnershipTag>();
        public Owner Owner => Tag.Owner;
        public string Label => Tag.Label;
        public float BodyRadius => m_BodyRadius;
        public float BodyHeight => m_BodyHeight;
        public bool Hovered => m_Hover;
        public XRGrabInteractable Grab => m_Grab ? m_Grab : m_Grab = GetComponent<XRGrabInteractable>();
        public Rigidbody Body => m_Body ? m_Body : m_Body = GetComponent<Rigidbody>();
        public bool IsSelected => Grab && Grab.isSelected;
        public bool Grabbable { get; private set; }
        public bool IsResetting { get; private set; }
        /// <summary>Linear speed (m/s) and angular speed (deg/s) from pose deltas, valid for kinematic and dynamic mugs.</summary>
        public float Speed { get; private set; }
        public float AngularSpeedDeg { get; private set; }

        /// <summary>Switches between the touch task (fixed, not grabbable) and pick & place (grabbable, physical).</summary>
        public void SetGrabbable(bool on)
        {
            Grabbable = on;
            if (Grab) Grab.enabled = on;
            if (Body)
            {
                Body.isKinematic = !on;
                Body.useGravity = on;
            }
        }

        void Update()
        {
            float dt = Time.deltaTime;
            transform.GetPositionAndRotation(out var p, out var r);
            if (m_HasLast && dt > 1e-5f)
            {
                Speed = (p - m_LastPos).magnitude / dt;
                AngularSpeedDeg = Quaternion.Angle(m_LastRot, r) / dt;
            }
            m_LastPos = p; m_LastRot = r; m_HasLast = true;
        }

        /// <summary>
        /// Safe reset (spec section 10): cancel any selection through the interaction system, freeze, zero the
        /// velocities, move to the slot, then restore normal interaction. Never teleports a selected mug.
        /// </summary>
        public IEnumerator SafeReset(XRInteractionManager manager, Pose slot)
        {
            IsResetting = true;
            if (IsSelected && manager) manager.CancelInteractableSelection((IXRSelectInteractable)Grab);
            int guard = 0;
            while (IsSelected && guard++ < 30) yield return null;
            if (IsSelected) { IsResetting = false; yield break; }
            bool wasGrabbable = Grabbable;
            if (Grab) Grab.enabled = false;
            if (Body)
            {
                if (!Body.isKinematic) { Body.linearVelocity = Vector3.zero; Body.angularVelocity = Vector3.zero; }
                Body.isKinematic = true;
            }
            transform.SetPositionAndRotation(slot.position, slot.rotation);
            if (Body) { Body.position = slot.position; Body.rotation = slot.rotation; }
            m_HasLast = false;
            SetHover(false);
            yield return new WaitForFixedUpdate();
            SetGrabbable(wasGrabbable);
            if (Body && !Body.isKinematic) Body.Sleep();
            IsResetting = false;
        }

        public void ConfigureVisuals(Renderer[] body, Renderer[] cue, TextMeshPro label, Renderer diamond, Renderer triangle, Renderer square)
        {
            m_BodyRenderers = body; m_CueRenderers = cue; m_Label = label;
            m_ShapeDiamond = diamond; m_ShapeTriangle = triangle; m_ShapeSquare = square;
        }

        public void ApplyIdentity(OwnershipVisual v)
        {
            Tag.Owner = v.owner;
            Tag.Label = v.label;
            gameObject.name = "Mug_" + v.label;
            m_BaseColor = v.color;
            if (m_Label) m_Label.text = v.label;
            Color shapeColor = Color.Lerp(v.color, Color.black, 0.25f);
            SetShape(m_ShapeDiamond, v.shape == MugShape.Diamond, shapeColor);
            SetShape(m_ShapeTriangle, v.shape == MugShape.Triangle, shapeColor);
            SetShape(m_ShapeSquare, v.shape == MugShape.Square, shapeColor);
            RefreshBody();
            SetCue(false, m_CueColor, m_CueIntensity);
        }

        void SetShape(Renderer r, bool on, Color c)
        {
            if (!r) return;
            r.enabled = on;
            m_Mpb ??= new MaterialPropertyBlock();
            r.GetPropertyBlock(m_Mpb);
            m_Mpb.SetColor(k_BaseColor, c);
            r.SetPropertyBlock(m_Mpb);
        }

        /// <summary>Same cue effect, size and intensity for every mug; timing is owned by the state machine.</summary>
        public void SetCue(bool on, Color color, float intensity)
        {
            m_Cue = on; m_CueColor = color; m_CueIntensity = intensity;
            if (m_Flash != null) { StopCoroutine(m_Flash); m_Flash = null; }
            PaintCue(on, color, intensity);
        }

        void PaintCue(bool on, Color color, float intensity)
        {
            if (m_CueRenderers == null) return;
            m_Mpb ??= new MaterialPropertyBlock();
            foreach (var r in m_CueRenderers)
            {
                if (!r) continue;
                r.GetPropertyBlock(m_Mpb);
                // Halo renderers use a transparent material: keep them translucent; rings stay opaque.
                float alpha = r.gameObject.name.Contains("Halo") ? 0.35f : 1f;
                m_Mpb.SetColor(k_BaseColor, new Color(color.r, color.g, color.b, alpha));
                m_Mpb.SetColor(k_EmissionColor, color * intensity);
                r.SetPropertyBlock(m_Mpb);
                r.enabled = on;
            }
        }

        /// <summary>Hover highlight: identical brightening for all mugs.</summary>
        public void SetHover(bool hover)
        {
            if (m_Hover == hover) return;
            m_Hover = hover;
            RefreshBody();
        }

        void RefreshBody()
        {
            if (m_BodyRenderers == null) return;
            m_Mpb ??= new MaterialPropertyBlock();
            Color c = m_Hover ? Color.Lerp(m_BaseColor, Color.white, 0.35f) : m_BaseColor;
            foreach (var r in m_BodyRenderers)
            {
                if (!r) continue;
                r.GetPropertyBlock(m_Mpb);
                m_Mpb.SetColor(k_BaseColor, c);
                m_Mpb.SetColor(k_EmissionColor, m_Hover ? m_BaseColor * 0.25f : Color.black);
                r.SetPropertyBlock(m_Mpb);
            }
        }

        /// <summary>Short coloured ring flash (training feedback). Restores the cue state afterwards.</summary>
        public void Flash(Color color, float seconds)
        {
            if (!isActiveAndEnabled) return;
            if (m_Flash != null) StopCoroutine(m_Flash);
            m_Flash = StartCoroutine(FlashRoutine(color, seconds));
        }

        IEnumerator FlashRoutine(Color color, float seconds)
        {
            PaintCue(true, color, m_CueIntensity);
            yield return new WaitForSecondsRealtime(seconds);
            PaintCue(m_Cue, m_CueColor, m_CueIntensity);
            m_Flash = null;
        }

        /// <summary>Reset-to-slot: snaps the mug back to its slot pose (base on the table plane).</summary>
        public void ResetToSlot(Pose slot)
        {
            if (Body && !Body.isKinematic) { Body.linearVelocity = Vector3.zero; Body.angularVelocity = Vector3.zero; }
            transform.SetPositionAndRotation(slot.position, slot.rotation);
            if (Body) { Body.position = slot.position; Body.rotation = slot.rotation; }
            m_HasLast = false;
            SetHover(false);
        }

        /// <summary>
        /// Signed-free distance (≥ 0) from a world point to the mug's solid geometry (body cylinder + handle box).
        /// Contact = distance ≤ padding, so the contact volume is slightly larger than the visible mug.
        /// </summary>
        public float DistanceTo(Vector3 worldPoint)
        {
            Vector3 p = transform.InverseTransformPoint(worldPoint);
            float radial = Mathf.Max(0f, new Vector2(p.x, p.z).magnitude - m_BodyRadius);
            float vertical = Mathf.Max(0f, Mathf.Max(-p.y, p.y - m_BodyHeight));
            float body = Mathf.Sqrt(radial * radial + vertical * vertical);
            Vector3 d = p - m_HandleCenter;
            Vector3 q = new Vector3(Mathf.Max(0f, Mathf.Abs(d.x) - m_HandleHalfSize.x), Mathf.Max(0f, Mathf.Abs(d.y) - m_HandleHalfSize.y),
                Mathf.Max(0f, Mathf.Abs(d.z) - m_HandleHalfSize.z));
            return Mathf.Min(body, q.magnitude);
        }

        void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(1f, 0.85f, 0.2f, 0.6f);
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.DrawWireCube(new Vector3(0, m_BodyHeight * 0.5f, 0), new Vector3(m_BodyRadius * 2f, m_BodyHeight, m_BodyRadius * 2f));
            Gizmos.DrawWireCube(m_HandleCenter, m_HandleHalfSize * 2f);
        }
    }
}
