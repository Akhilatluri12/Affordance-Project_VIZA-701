using UnityEngine;
using UnityEngine.XR;

namespace XRStudy.Task1
{
    /// <summary>Audio + haptic feedback. The contact click/pulse is identical for every mug and condition.</summary>
    [RequireComponent(typeof(AudioSource))]
    public class FeedbackPlayer : MonoBehaviour
    {
        [SerializeField] AudioClip m_Contact;
        [SerializeField] AudioClip m_Correct;
        [SerializeField] AudioClip m_Incorrect;
        [SerializeField] AudioClip m_Neutral;
        [SerializeField] AudioClip m_Button;
        [Range(0f, 1f)] public float hapticAmplitude = 0.6f;
        public float hapticDuration_s = 0.05f;

        AudioSource m_Source;

        public void Configure(AudioClip contact, AudioClip correct, AudioClip button)
        {
            m_Contact = contact; m_Correct = correct; m_Button = button;
        }

        void Awake()
        {
            m_Source = GetComponent<AudioSource>();
            m_Source.playOnAwake = false;
            m_Source.spatialBlend = 0f;
            if (!m_Incorrect) m_Incorrect = Tone("incorrect", 220f, 0.25f, 0.35f);
            if (!m_Neutral) m_Neutral = Tone("neutral", 660f, 0.06f, 0.2f);
            if (!m_Contact) m_Contact = Tone("contact", 1200f, 0.03f, 0.3f);
            if (!m_Correct) m_Correct = Tone("correct", 880f, 0.18f, 0.3f);
            if (!m_Button) m_Button = m_Contact;
        }

        public void Contact(string effectorId)
        {
            Play(m_Contact);
            if (effectorId != null && effectorId.Contains("Controller"))
            {
                var node = effectorId.StartsWith("Left") ? XRNode.LeftHand : XRNode.RightHand;
                var device = InputDevices.GetDeviceAtXRNode(node);
                if (device.isValid) device.SendHapticImpulse(0u, hapticAmplitude, hapticDuration_s);
            }
        }

        public void Correct() => Play(m_Correct);
        public void Incorrect() => Play(m_Incorrect);
        public void Neutral() => Play(m_Neutral);
        public void Button() => Play(m_Button);

        void Play(AudioClip c)
        {
            if (c && m_Source) m_Source.PlayOneShot(c);
        }

        static AudioClip Tone(string name, float hz, float seconds, float volume)
        {
            int rate = 44100, n = Mathf.Max(1, (int)(rate * seconds));
            var data = new float[n];
            for (int i = 0; i < n; i++)
            {
                float env = Mathf.Min(1f, i / (rate * 0.005f)) * Mathf.Clamp01((n - i) / (rate * 0.03f));
                data[i] = Mathf.Sin(2f * Mathf.PI * hz * i / rate) * volume * env;
            }
            var clip = AudioClip.Create(name, n, 1, rate, false);
            clip.SetData(data, 0);
            return clip;
        }
    }
}
