using System;
using System.Collections.Generic;
using UnityEngine;

namespace XRStudy.Task1
{
    /// <summary>
    /// Detects the first frame an effector point enters a mug's contact volume (mug geometry + padding) and
    /// raises one event per entry; the same effector must move out by the re-arm distance before it can touch
    /// that mug again. Also drives the hover highlight.
    ///
    /// Timing: hand and controller poses are updated once per rendered frame, so contact is evaluated once per
    /// frame from those poses (physics callbacks would only run at the 50 Hz fixed step). Resolution is one
    /// frame: ~11.1 ms at 90 Hz, ~13.9 ms at 72 Hz, plus a tracking latency that is the same in every condition.
    /// </summary>
    [DefaultExecutionOrder(60)]
    public class ContactDetector : MonoBehaviour
    {
        [SerializeField] EffectorTracker m_Effectors;
        [SerializeField] StudyMug[] m_Mugs = new StudyMug[3];

        public float padding_m = 0.01f;
        public float hover_m = 0.05f;
        public float rearm_m = 0.02f;
        public bool hoverEnabled = true;

        readonly Dictionary<(string, int), bool> m_Inside = new Dictionary<(string, int), bool>();
        long m_NextKey;

        /// <summary>(mug, effector id, time, frame, unique key)</summary>
        public event Action<StudyMug, string, double, int, long> Contacted;

        public void Configure(EffectorTracker effectors, StudyMug[] mugs) { m_Effectors = effectors; m_Mugs = mugs; }

        public void Apply(ProtocolConfig cfg)
        {
            padding_m = cfg.contactPadding_m; hover_m = cfg.hoverDistance_m; rearm_m = cfg.contactRearm_m; hoverEnabled = cfg.hoverHighlight;
        }

        void Update()
        {
            if (!m_Effectors) return;
            var effectors = m_Effectors.Active;
            double now = MonotonicClock.Now;
            int frame = Time.frameCount;
            for (int m = 0; m < m_Mugs.Length; m++)
            {
                var mug = m_Mugs[m];
                if (!mug || !mug.isActiveAndEnabled) continue;
                bool hover = false;
                foreach (var e in effectors)
                {
                    float d = float.PositiveInfinity;
                    foreach (var p in e.contactPoints) d = Mathf.Min(d, mug.DistanceTo(p));
                    if (d <= hover_m) hover = true;
                    var key = (e.id, m);
                    m_Inside.TryGetValue(key, out bool inside);
                    if (!inside && d <= padding_m)
                    {
                        m_Inside[key] = true;
                        Contacted?.Invoke(mug, e.id, now, frame, ++m_NextKey);
                    }
                    else if (inside && d > padding_m + rearm_m) m_Inside[key] = false;
                }
                mug.SetHover(hoverEnabled && hover);
            }
            // An effector that stopped being tracked is no longer touching anything.
            m_Stale.Clear();
            foreach (var key in m_Inside.Keys)
            {
                bool active = false;
                foreach (var e in effectors) if (e.id == key.Item1) { active = true; break; }
                if (!active) m_Stale.Add(key);
            }
            foreach (var key in m_Stale) m_Inside.Remove(key);
        }

        readonly List<(string, int)> m_Stale = new List<(string, int)>();

        /// <summary>Forget contact state (e.g. after recalibration or between trials).</summary>
        public void ResetState() => m_Inside.Clear();

        /// <summary>True when any effector is currently touching a mug (used to wait before the next trial).</summary>
        public bool AnyContact()
        {
            foreach (var kv in m_Inside) if (kv.Value) return true;
            return false;
        }
    }
}
