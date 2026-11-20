using System.Collections.Generic;
using UnityEngine;

namespace XRStudy.Task1.Tests
{
    static class TestFixtures
    {
        public static StudyLayout Layout1(bool agent = false, string name = "Layout1_AgentAbsent")
        {
            var l = ScriptableObject.CreateInstance<StudyLayout>();
            l.name = name;
            l.layoutName = name;
            l.showAgent = agent;
            l.slots = new List<MugSlot>
            {
                new MugSlot { slotName = "Left", canonicalOwner = Owner.Owned, position_m = new Vector2(-0.25f, 0.35f) },
                new MugSlot { slotName = "Center", canonicalOwner = Owner.Neutral, position_m = new Vector2(0f, 0.35f) },
                new MugSlot { slotName = "Right", canonicalOwner = Owner.AgentOwned, position_m = new Vector2(0.25f, 0.35f) },
            };
            return l;
        }

        public static StudyLayout Future()
        {
            var l = ScriptableObject.CreateInstance<StudyLayout>();
            l.layoutName = "FutureLayout_DepthZones";
            l.showAgent = true;
            l.allowOrderCounterbalancing = false;
            l.slots = new List<MugSlot>
            {
                new MugSlot { slotName = "NearLeft", canonicalOwner = Owner.Owned, position_m = new Vector2(-0.3f, 0.2f) },
                new MugSlot { slotName = "MiddleCenter", canonicalOwner = Owner.Neutral, position_m = new Vector2(0f, 0.4f) },
                new MugSlot { slotName = "FarRight", canonicalOwner = Owner.AgentOwned, position_m = new Vector2(0.3f, 0.6f) },
            };
            return l;
        }

        public static ProtocolConfig Config()
        {
            var c = ScriptableObject.CreateInstance<ProtocolConfig>();
            c.agentAbsentLayout = Layout1();
            c.agentPresentLayout = Layout1(true, "Layout2_AgentPresent");
            return c;
        }

        public static void Destroy(ProtocolConfig c)
        {
            if (!c) return;
            Object.DestroyImmediate(c.agentAbsentLayout);
            Object.DestroyImmediate(c.agentPresentLayout);
            Object.DestroyImmediate(c);
        }
    }
}
