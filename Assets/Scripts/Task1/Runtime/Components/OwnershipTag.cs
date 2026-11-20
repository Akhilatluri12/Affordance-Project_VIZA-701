using UnityEngine;

namespace XRStudy.Task1
{
    /// <summary>
    /// Mug ownership only (Owned / Neutral / AgentOwned). Carries no access restriction: every mug has the
    /// same contact volume, hover and feedback whatever its owner.
    /// </summary>
    public class OwnershipTag : MonoBehaviour
    {
        [SerializeField] Owner m_Owner = Owner.Neutral;
        [SerializeField] string m_Label = "N";
        public Owner Owner { get => m_Owner; set => m_Owner = value; }
        public string Label { get => m_Label; set => m_Label = value; }
    }
}
