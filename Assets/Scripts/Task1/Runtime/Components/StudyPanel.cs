using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace XRStudy.Task1
{
    public enum ButtonStyle { Normal, Primary, Selected, Danger }

    public struct PanelButton
    {
        public string id;
        public string label;
        public Action onClick;
        public ButtonStyle style;

        public PanelButton(string label, Action onClick, ButtonStyle style = ButtonStyle.Normal, string id = null)
        {
            this.label = label; this.onClick = onClick; this.style = style; this.id = id ?? label;
        }
    }

    /// <summary>
    /// World-space uGUI panel: title, one plain-language instruction, a progress line and up to a dozen large
    /// buttons (pokeable with a fingertip, clickable with a ray). Buttons are rebuilt per step; a short guard
    /// time after each change stops one poke from activating two screens in a row.
    /// </summary>
    public class StudyPanel : MonoBehaviour
    {
        [SerializeField] GameObject m_Root;
        [SerializeField] TextMeshProUGUI m_Title;
        [SerializeField] TextMeshProUGUI m_Body;
        [SerializeField] TextMeshProUGUI m_Progress;
        [SerializeField] RectTransform m_ButtonArea;
        [SerializeField] Button m_ButtonTemplate;
        [SerializeField] FeedbackPlayer m_Feedback;

        public float clickGuard_s = 0.35f;
        static readonly Color k_Normal = new Color(0.22f, 0.25f, 0.30f);
        static readonly Color k_Primary = new Color(0.16f, 0.45f, 0.85f);
        static readonly Color k_Selected = new Color(0.20f, 0.62f, 0.38f);
        static readonly Color k_Danger = new Color(0.70f, 0.22f, 0.22f);

        readonly List<Button> m_Pool = new List<Button>();
        readonly List<PanelButton> m_Current = new List<PanelButton>();
        float m_LastChange;

        public bool Visible => m_Root && m_Root.activeSelf;
        public IReadOnlyList<PanelButton> CurrentButtons => m_Current;
        public string BodyText => m_Body ? m_Body.text : "";
        public string TitleText => m_Title ? m_Title.text : "";

        public void Configure(GameObject root, TextMeshProUGUI title, TextMeshProUGUI body, TextMeshProUGUI progress,
            RectTransform buttonArea, Button template, FeedbackPlayer feedback)
        {
            m_Root = root; m_Title = title; m_Body = body; m_Progress = progress; m_ButtonArea = buttonArea;
            m_ButtonTemplate = template; m_Feedback = feedback;
        }

        public void Show(string title, string body, string progress = null, params PanelButton[] buttons)
        {
            if (m_Root && !m_Root.activeSelf) m_Root.SetActive(true);
            SetText(m_Title, title);
            SetText(m_Body, body);
            if (progress != null) SetText(m_Progress, progress);
            SetButtons(buttons);
        }

        public void SetBody(string body) => SetText(m_Body, body);
        public void SetProgress(string progress) => SetText(m_Progress, progress);
        public void Hide() { if (m_Root && m_Root.activeSelf) m_Root.SetActive(false); }

        static void SetText(TextMeshProUGUI t, string s)
        {
            if (!t) return;
            s ??= "";
            if (t.text != s) t.text = s;
            t.gameObject.SetActive(s.Length > 0);
        }

        public void SetButtons(params PanelButton[] buttons)
        {
            buttons ??= Array.Empty<PanelButton>();
            if (SameButtons(buttons)) { RefreshStyles(buttons); return; }
            m_Current.Clear();
            m_Current.AddRange(buttons);
            m_LastChange = Time.unscaledTime;
            while (m_Pool.Count < buttons.Length)
            {
                var b = Instantiate(m_ButtonTemplate, m_ButtonArea);
                int index = m_Pool.Count;
                b.onClick.AddListener(() => OnClicked(index));
                m_Pool.Add(b);
            }
            for (int i = 0; i < m_Pool.Count; i++)
            {
                bool on = i < buttons.Length;
                m_Pool[i].gameObject.SetActive(on);
                if (!on) continue;
                var label = m_Pool[i].GetComponentInChildren<TextMeshProUGUI>(true);
                if (label) label.text = buttons[i].label;
            }
            RefreshStyles(buttons);
            if (m_ButtonArea) m_ButtonArea.gameObject.SetActive(buttons.Length > 0);
        }

        bool SameButtons(PanelButton[] buttons)
        {
            if (buttons.Length != m_Current.Count) return false;
            for (int i = 0; i < buttons.Length; i++)
                if (buttons[i].id != m_Current[i].id || buttons[i].label != m_Current[i].label) return false;
            for (int i = 0; i < buttons.Length; i++) m_Current[i] = buttons[i]; // refresh callbacks
            return true;
        }

        void RefreshStyles(PanelButton[] buttons)
        {
            for (int i = 0; i < buttons.Length && i < m_Pool.Count; i++)
            {
                var img = m_Pool[i].targetGraphic as Image;
                if (!img) continue;
                img.color = buttons[i].style switch
                {
                    ButtonStyle.Primary => k_Primary,
                    ButtonStyle.Selected => k_Selected,
                    ButtonStyle.Danger => k_Danger,
                    _ => k_Normal
                };
            }
        }

        void OnClicked(int index)
        {
            if (index >= m_Current.Count) return;
            if (Time.unscaledTime - m_LastChange < clickGuard_s) return;
            m_LastChange = Time.unscaledTime;
            m_Feedback?.Button();
            m_Current[index].onClick?.Invoke();
        }

        /// <summary>Programmatic click (debug simulator / tests). Respects the guard time unless forced.</summary>
        public bool Click(string id, bool force = false)
        {
            for (int i = 0; i < m_Current.Count; i++)
            {
                if (m_Current[i].id != id) continue;
                if (force) m_LastChange = -10f;
                OnClicked(i);
                return true;
            }
            return false;
        }
    }
}
