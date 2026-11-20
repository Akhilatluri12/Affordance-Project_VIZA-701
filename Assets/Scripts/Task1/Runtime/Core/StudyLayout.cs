using System;
using System.Collections.Generic;
using UnityEngine;

namespace XRStudy.Task1
{
    [Serializable]
    public class MugSlot
    {
        public string slotName = "Left";
        [Tooltip("Owner placed here in the canonical (un-counterbalanced) order.")]
        public Owner canonicalOwner = Owner.Owned;
        [Tooltip("Table-local position in metres: x = participant's right, y = away from the participant (depth).")]
        public Vector2 position_m = new Vector2(-0.25f, 0.35f);
        [Tooltip("Mug yaw relative to the table's forward direction. 0 = handle to the participant's right.")]
        public float yaw_deg = 0f;
    }

    /// <summary>
    /// A study layout as data: mug slots, hand-rest marker, agent placement and panels, all relative to the
    /// calibrated table origin (centre of the table's near edge, on the table surface, +z across the table).
    /// New layouts (e.g. the Future Layout with depth zones) are new assets; no code changes needed.
    /// </summary>
    [CreateAssetMenu(menuName = "XR Study/Task 1 Study Layout", fileName = "StudyLayout")]
    public class StudyLayout : ScriptableObject
    {
        public string layoutName = "Layout1";
        [TextArea(2, 4)] public string description = "";

        [Header("Mugs (exactly three, one per ownership category)")]
        public List<MugSlot> slots = new List<MugSlot>();
        [Tooltip("If false, the canonical order is always used (e.g. ownership is tied to a depth zone).")]
        public bool allowOrderCounterbalancing = true;

        [Header("Hand-rest marker (dark-blue oval on the near edge)")]
        public Vector2 handRestPosition_m = new Vector2(0f, 0.06f);
        public Vector2 handRestSize_m = new Vector2(0.13f, 0.09f);

        [Header("Placement pad (pick & place task)")]
        [Tooltip("Table-local centre of the common placement pad (designated-hand side by default).")]
        public Vector2 padPosition_m = new Vector2(0.33f, 0.13f);
        [Tooltip("Full width (x) and depth (y) of the pad. Set from real mug/table geometry during piloting.")]
        public Vector2 padSize_m = new Vector2(0.14f, 0.14f);

        [Header("Agent")]
        public bool showAgent;
        public Vector2 agentPosition_m = new Vector2(0f, 1.05f);
        public float agentYaw_deg = 180f;

        [Header("Panels (table-local, metres; y = height above the table)")]
        public Vector3 instructionPanelPosition_m = new Vector3(0f, 0.80f, 0.95f);
        public float instructionPanelTilt_deg = 12f;
        public Vector3 experimenterPanelPosition_m = new Vector3(-0.6f, 0.3f, 0.3f);

        /// <summary>
        /// All six orders. Entry k lists, for each slot i, the index of the canonical slot whose owner is
        /// placed there. Index 0 is the canonical order (P1, N, NPC for Layout 1).
        /// </summary>
        public static readonly int[][] Orders =
        {
            new[] { 0, 1, 2 }, new[] { 0, 2, 1 }, new[] { 1, 0, 2 },
            new[] { 1, 2, 0 }, new[] { 2, 0, 1 }, new[] { 2, 1, 0 },
        };

        public int EffectiveOrder(int orderIndex) => allowOrderCounterbalancing ? ((orderIndex % 6) + 6) % 6 : 0;

        public Owner OwnerAtSlot(int slot, int orderIndex) => slots[Orders[EffectiveOrder(orderIndex)][slot]].canonicalOwner;

        public int SlotOf(Owner owner, int orderIndex)
        {
            for (int i = 0; i < slots.Count; i++) if (OwnerAtSlot(i, orderIndex) == owner) return i;
            throw new ArgumentException("No slot for " + owner);
        }

        public string OrderLabel(int orderIndex, Func<Owner, string> label)
        {
            var parts = new string[slots.Count];
            for (int i = 0; i < slots.Count; i++) parts[i] = label(OwnerAtSlot(i, orderIndex));
            return string.Join("-", parts);
        }

        public List<string> Validate()
        {
            var p = new List<string>();
            if (slots == null || slots.Count != 3) { p.Add($"{name}: exactly three mug slots are required."); return p; }
            foreach (Owner o in Enum.GetValues(typeof(Owner)))
                if (slots.FindAll(s => s.canonicalOwner == o).Count != 1) p.Add($"{name}: each ownership must appear in exactly one slot.");
            return p;
        }

        /// <summary>True when both layouts put the same mugs in the same places (only the agent may differ).</summary>
        public bool SameMugGeometry(StudyLayout other, float tolerance = 1e-4f)
        {
            if (!other || other.slots.Count != slots.Count) return false;
            for (int i = 0; i < slots.Count; i++)
            {
                var a = slots[i]; var b = other.slots[i];
                if (a.canonicalOwner != b.canonicalOwner || (a.position_m - b.position_m).sqrMagnitude > tolerance * tolerance ||
                    Mathf.Abs(Mathf.DeltaAngle(a.yaw_deg, b.yaw_deg)) > 0.01f) return false;
            }
            return (handRestPosition_m - other.handRestPosition_m).sqrMagnitude <= tolerance * tolerance &&
                   (padPosition_m - other.padPosition_m).sqrMagnitude <= tolerance * tolerance &&
                   (padSize_m - other.padSize_m).sqrMagnitude <= tolerance * tolerance &&
                   allowOrderCounterbalancing == other.allowOrderCounterbalancing;
        }

        // ------------------------------------------------------------ table-local -> world

        public static Vector3 TableToWorld(Pose table, Vector2 local, float height = 0f) =>
            table.position + table.rotation * new Vector3(local.x, height, local.y);

        public static Quaternion YawToWorld(Pose table, float yawDeg) => table.rotation * Quaternion.Euler(0f, yawDeg, 0f);

        public Pose SlotPose(Pose table, int slot) =>
            new Pose(TableToWorld(table, slots[slot].position_m), YawToWorld(table, slots[slot].yaw_deg));
    }
}
