using System.Collections.Generic;
using UnityEngine;

namespace XRStudy.Task1
{
    /// <summary>
    /// Places mugs, the hand-rest marker, the agent and panels from a <see cref="StudyLayout"/> asset relative
    /// to the calibrated table origin. Draws the active layout's slots as gizmos in the Editor.
    /// </summary>
    public class LayoutController : MonoBehaviour
    {
        [SerializeField] TableCalibration m_Table;
        [SerializeField] TableOccluder m_Occluder;
        [SerializeField] PlacementPad m_Pad;
        bool m_PadVisible;
        public float tableWidth_m = 1.2f, tableDepth_m = 0.8f;
        [SerializeField] StudyMug[] m_Mugs = new StudyMug[3];
        [SerializeField] HandRestMarker m_HandRest;
        [SerializeField] AgentPresenceController m_Agent;
        [SerializeField] Transform m_InstructionPanel;
        [SerializeField] Transform m_ExperimenterPanel;
        [Tooltip("Layout drawn as gizmos in the Editor (and applied at Start until a session sets one).")]
        [SerializeField] StudyLayout m_PreviewLayout;

        public StudyLayout Current { get; private set; }
        public int OrderIndex { get; private set; }
        public StudyMug[] Mugs => m_Mugs;
        public HandRestMarker HandRest => m_HandRest;
        public TableCalibration Table => m_Table;
        public PlacementPad Pad => m_Pad;

        public void SetPad(PlacementPad pad) => m_Pad = pad;

        /// <summary>Pick & place shows the pad and makes mugs grabbable; touch hides it and fixes the mugs.</summary>
        public void SetTask(StudyTask task)
        {
            m_PadVisible = task == StudyTask.PickPlace;
            if (m_Pad) m_Pad.SetVisible(m_PadVisible);
            foreach (var m in m_Mugs) if (m) m.SetGrabbable(task == StudyTask.PickPlace);
        }

        public void Configure(TableCalibration table, StudyMug[] mugs, HandRestMarker rest, AgentPresenceController agent,
            Transform instructionPanel, Transform experimenterPanel, StudyLayout preview)
        {
            m_Table = table; m_Mugs = mugs; m_HandRest = rest; m_Agent = agent;
            m_InstructionPanel = instructionPanel; m_ExperimenterPanel = experimenterPanel; m_PreviewLayout = preview;
        }

        public void SetOccluder(TableOccluder occluder, float width, float depth) { m_Occluder = occluder; tableWidth_m = width; tableDepth_m = depth; }

        public StudyMug MugFor(Owner o)
        {
            foreach (var m in m_Mugs) if (m && m.Owner == o) return m;
            return null;
        }

        public void Apply(StudyLayout layout, int orderIndex)
        {
            if (!layout) return;
            Current = layout;
            OrderIndex = orderIndex;
            ResetMugsToSlots();
            var table = m_Table.TablePose;
            if (m_Occluder) m_Occluder.Apply(table, tableWidth_m, tableDepth_m);
            if (m_Pad)
            {
                m_Pad.Place(new Pose(StudyLayout.TableToWorld(table, layout.padPosition_m, 0.0015f), table.rotation), layout.padSize_m);
                m_Pad.SetVisible(m_PadVisible);
            }
            if (m_HandRest)
            {
                m_HandRest.transform.SetPositionAndRotation(StudyLayout.TableToWorld(table, layout.handRestPosition_m, 0.001f), table.rotation);
                m_HandRest.SetSize(layout.handRestSize_m);
            }
            if (m_Agent)
            {
                m_Agent.Place(StudyLayout.TableToWorld(table, layout.agentPosition_m), StudyLayout.YawToWorld(table, layout.agentYaw_deg), table.position.y);
                m_Agent.SetPresent(layout.showAgent);
            }
            if (m_InstructionPanel)
            {
                Vector3 p = StudyLayout.TableToWorld(table, new Vector2(layout.instructionPanelPosition_m.x, layout.instructionPanelPosition_m.z), layout.instructionPanelPosition_m.y);
                m_InstructionPanel.SetPositionAndRotation(p, table.rotation * Quaternion.Euler(layout.instructionPanelTilt_deg, 0f, 0f));
            }
            if (m_ExperimenterPanel)
            {
                Vector3 p = StudyLayout.TableToWorld(table, new Vector2(layout.experimenterPanelPosition_m.x, layout.experimenterPanelPosition_m.z), layout.experimenterPanelPosition_m.y);
                Vector3 look = p - StudyLayout.TableToWorld(table, new Vector2(0f, -0.3f), 0.45f); look.y = 0f;
                m_ExperimenterPanel.SetPositionAndRotation(p, Quaternion.LookRotation(look.sqrMagnitude > 1e-4f ? look.normalized : table.rotation * Vector3.forward));
            }
        }

        /// <summary>Puts every mug back exactly on its slot (also used if a mug is ever displaced).</summary>
        public void ResetMugsToSlots()
        {
            if (!Current) return;
            var table = m_Table.TablePose;
            for (int i = 0; i < Current.slots.Count; i++)
            {
                var mug = MugFor(Current.OwnerAtSlot(i, OrderIndex));
                if (mug) mug.ResetToSlot(Current.SlotPose(table, i));
            }
        }

        /// <summary>Slot pose of this mug under the current layout and order.</summary>
        public Pose SlotPoseOf(StudyMug mug)
        {
            for (int i = 0; i < Current.slots.Count; i++)
                if (Current.OwnerAtSlot(i, OrderIndex) == mug.Owner) return Current.SlotPose(m_Table.TablePose, i);
            return new Pose(mug.transform.position, mug.transform.rotation);
        }

        public List<Vector3> SlotPositions()
        {
            var list = new List<Vector3>();
            if (!Current) return list;
            for (int i = 0; i < Current.slots.Count; i++) list.Add(Current.SlotPose(m_Table.TablePose, i).position);
            return list;
        }

        void OnDrawGizmos()
        {
            var layout = Current ? Current : m_PreviewLayout;
            if (!layout || layout.slots == null) return;
            var table = m_Table ? m_Table.TablePose : new Pose(transform.position, transform.rotation);
            Gizmos.color = Color.white;
            Gizmos.DrawLine(StudyLayout.TableToWorld(table, new Vector2(-0.6f, 0f)), StudyLayout.TableToWorld(table, new Vector2(0.6f, 0f)));
            for (int i = 0; i < layout.slots.Count; i++)
            {
                var s = layout.slots[i];
                Owner o = layout.OwnerAtSlot(i, Current ? OrderIndex : 0);
                Gizmos.color = o == Owner.Owned ? new Color(0.36f, 0.62f, 1f) : o == Owner.Neutral ? new Color(0.42f, 0.8f, 0.45f) : new Color(0.93f, 0.4f, 0.4f);
                var pose = layout.SlotPose(table, i);
                Gizmos.DrawWireSphere(pose.position + Vector3.up * 0.05f, 0.045f);
                Gizmos.DrawLine(pose.position + Vector3.up * 0.05f, pose.position + Vector3.up * 0.05f + pose.rotation * Vector3.right * 0.07f);
            }
            Gizmos.color = new Color(0.85f, 0.85f, 0.85f);
            Gizmos.DrawWireCube(StudyLayout.TableToWorld(table, layout.padPosition_m), new Vector3(layout.padSize_m.x, 0.002f, layout.padSize_m.y));
            Gizmos.color = new Color(0.18f, 0.37f, 0.5f);
            Gizmos.DrawWireCube(StudyLayout.TableToWorld(table, layout.handRestPosition_m), new Vector3(layout.handRestSize_m.x, 0.002f, layout.handRestSize_m.y));
            if (layout.showAgent)
            {
                Gizmos.color = new Color(0.93f, 0.4f, 0.4f, 0.8f);
                Vector3 a = StudyLayout.TableToWorld(table, layout.agentPosition_m);
                Gizmos.DrawWireCube(a + Vector3.up * 0.35f, new Vector3(0.45f, 0.7f, 0.4f));
            }
        }
    }
}
