using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace XRStudy.Task1.Tests
{
    public class ScheduleAndLayoutTests
    {
        ProtocolConfig m_Cfg;
        [SetUp] public void SetUp() => m_Cfg = TestFixtures.Config();
        [TearDown] public void TearDown() => TestFixtures.Destroy(m_Cfg);

        StudySchedule Gen(string pid = "P7", long seed = 123, int perBlock = 12,
            ConditionOrderMode order = ConditionOrderMode.Auto, MugOrderMode mugs = MugOrderMode.Auto) =>
            ScheduleGenerator.Generate(m_Cfg, seed, "S", pid, order, mugs, perBlock);

        [Test]
        public void EveryBlockBalancesOwnership()
        {
            var s = Gen(perBlock: 12);
            Assert.AreEqual(2, s.blocks.Count);
            Assert.AreNotEqual(s.blocks[0].presence, s.blocks[1].presence, "one block per agent condition");
            foreach (var b in s.blocks.Concat(new[] { s.training }))
                foreach (Owner o in System.Enum.GetValues(typeof(Owner)))
                    Assert.AreEqual(b.trials.Count / 3, b.trials.Count(t => t.targetOwner == o), $"{b.blockId} {o}");
            Assert.IsTrue(s.training.trials.All(t => t.training));
            Assert.IsFalse(s.blocks.SelectMany(b => b.trials).Any(t => t.training));
        }

        [Test]
        public void TrialCountRoundsUpToMultipleOfThree()
        {
            Assert.AreEqual(12, Gen(perBlock: 11).blocks[0].trials.Count);
            Assert.AreEqual(3, Gen(perBlock: 1).blocks[1].trials.Count);
        }

        [Test]
        public void RunLengthIsBounded()
        {
            for (long seed = 1; seed < 40; seed++)
                foreach (var b in Gen(seed: seed, perBlock: 30).blocks)
                    Assert.LessOrEqual(ScheduleGenerator.LongestRun(b.trials.Select(t => t.targetOwner).ToList()), m_Cfg.maxSameTargetRun);
        }

        [Test]
        public void SameSeedReproducesSchedule()
        {
            Assert.AreEqual(Gen(seed: 99).ToCsv(), Gen(seed: 99).ToCsv());
            Assert.AreNotEqual(Gen(seed: 99).ToCsv(), Gen(seed: 100).ToCsv());
        }

        [Test]
        public void ConditionOrderCounterbalancesByParticipantId()
        {
            Assert.AreEqual("AbsentFirst", Gen("P2").conditionOrder);
            Assert.AreEqual("PresentFirst", Gen("P3").conditionOrder);
            Assert.AreEqual("PresentFirst", Gen("P2", order: ConditionOrderMode.PresentFirst).conditionOrder);
            Assert.AreEqual(PresenceCondition.AgentAbsent, Gen("P2").training.presence, "training uses the first block's condition");
        }

        [Test]
        public void MugOrderCyclesThroughAllSixOrders()
        {
            var labels = Enumerable.Range(0, 12).Select(i => Gen("P" + i).mugOrderLabel).Distinct().ToList();
            Assert.AreEqual(6, labels.Count);
            Assert.AreEqual("P1-N-NPC", Gen("P5", mugs: MugOrderMode.Fixed).mugOrderLabel);
        }

        [Test]
        public void TargetSlotMatchesLayoutAndOrder()
        {
            var s = Gen("P9");
            foreach (var t in s.blocks.SelectMany(b => b.trials))
            {
                var layout = ScheduleGenerator.LayoutFor(m_Cfg, t.presence);
                Assert.AreEqual(t.targetOwner, layout.OwnerAtSlot(t.targetSlot, s.mugOrderIndex));
                Assert.AreEqual(layout.slots[t.targetSlot].slotName, t.targetSlotName);
            }
        }

        [Test] // Layout 2 = Layout 1 + agent; nothing else differs
        public void Layout2DiffersOnlyByAgent()
        {
            Assert.IsTrue(m_Cfg.agentAbsentLayout.SameMugGeometry(m_Cfg.agentPresentLayout));
            Assert.IsFalse(m_Cfg.agentAbsentLayout.showAgent);
            Assert.IsTrue(m_Cfg.agentPresentLayout.showAgent);
        }

        [Test] // Mugs in one straight line parallel to the edge, canonical order P1, N, NPC left to right
        public void Layout1IsStraightLineInCanonicalOrder()
        {
            var l = m_Cfg.agentAbsentLayout;
            var table = new Pose(new Vector3(1f, 0.74f, 2f), Quaternion.Euler(0f, 30f, 0f));
            var p = Enumerable.Range(0, 3).Select(i => l.SlotPose(table, i).position).ToArray();
            Vector3 edge = table.rotation * Vector3.right;
            Vector3 across = table.rotation * Vector3.forward;
            Assert.AreEqual(Vector3.Dot(p[0] - table.position, across), Vector3.Dot(p[2] - table.position, across), 1e-5f, "same depth");
            Assert.Less(Vector3.Dot(p[0], edge), Vector3.Dot(p[1], edge));
            Assert.Less(Vector3.Dot(p[1], edge), Vector3.Dot(p[2], edge));
            Assert.AreEqual(0.74f, p[1].y, 1e-5f, "on the table surface");
            Assert.AreEqual(new[] { Owner.Owned, Owner.Neutral, Owner.AgentOwned }, Enumerable.Range(0, 3).Select(i => l.OwnerAtSlot(i, 0)).ToArray());
        }

        [Test] // Future layout: a new asset, order fixed because ownership is tied to depth zones
        public void FutureLayoutNeedsNoCodeChanges()
        {
            var f = TestFixtures.Future();
            try
            {
                Assert.IsEmpty(f.Validate());
                m_Cfg.agentPresentLayout = f;
                var s = Gen("P3");
                foreach (var t in s.blocks.First(b => b.presence == PresenceCondition.AgentPresent).trials)
                    Assert.AreEqual(t.targetOwner == Owner.Owned ? "NearLeft" : t.targetOwner == Owner.Neutral ? "MiddleCenter" : "FarRight", t.targetSlotName);
            }
            finally { Object.DestroyImmediate(f); }
        }

        [Test]
        public void RngIsStableAcrossRuntimes()
        {
            var r = new StudyRng(1);
            Assert.AreEqual(0x910A2DEC89025CC1UL, r.NextULong());
            Assert.AreEqual(0xBEEB8DA1658EEC67UL, r.NextULong());
        }

        [Test]
        public void ProtocolValidates() => Assert.IsEmpty(m_Cfg.Validate());

        StudySchedule Plan(TaskSelection t, ConditionSelection c, string pid = "P7") =>
            ScheduleGenerator.Generate(m_Cfg, 77, "S", pid, ConditionOrderMode.Auto, MugOrderMode.Auto, 12, t, c);

        [Test] // Full design: both tasks x both agent conditions, each task with its own practice
        public void BothTasksBothConditions()
        {
            var s = Plan(TaskSelection.Both, ConditionSelection.Both);
            Assert.AreEqual(6, s.sequence.Count);
            Assert.AreEqual(4, s.blocks.Count);
            var tasks = s.sequence.Select(b => b.trials[0].task).ToList();
            Assert.AreNotEqual(tasks[0], tasks[3], "second half is the other task");
            Assert.IsTrue(s.sequence[0].training && s.sequence[3].training);
            foreach (StudyTask t in new[] { StudyTask.Touch, StudyTask.PickPlace })
            {
                var tb = s.blocks.Where(b => b.trials[0].task == t).ToList();
                Assert.AreEqual(2, tb.Count);
                Assert.AreNotEqual(tb[0].presence, tb[1].presence, $"{t}: with and without Alex");
                Assert.IsTrue(tb.All(b => b.trials.All(x => x.task == t)));
            }
            Assert.AreEqual(Enumerable.Range(1, 4), s.blocks.Select(b => b.blockIndex));
            Assert.AreEqual(4, s.blocks.Select(b => b.blockId).Distinct().Count(), "block ids unique across tasks");
        }

        [Test] // Without Alex only: no agent-present block anywhere
        public void WithoutAlexOnly()
        {
            var s = Plan(TaskSelection.PickPlace, ConditionSelection.AgentAbsentOnly);
            Assert.AreEqual(1, s.blocks.Count);
            Assert.IsTrue(s.sequence.All(b => b.presence == PresenceCondition.AgentAbsent));
            Assert.IsTrue(s.sequence.SelectMany(b => b.trials).All(t => t.task == StudyTask.PickPlace));
            Assert.AreEqual("AbsentOnly", s.conditionOrder);
            Assert.AreEqual(m_Cfg.agentAbsentLayout.layoutName, s.blocks[0].layoutName);
        }

        [Test]
        public void WithAlexOnly()
        {
            var s = Plan(TaskSelection.Touch, ConditionSelection.AgentPresentOnly, "P2");
            Assert.IsTrue(s.sequence.All(b => b.presence == PresenceCondition.AgentPresent));
            Assert.AreEqual(m_Cfg.agentPresentLayout.layoutName, s.blocks.Single().layoutName);
        }

        [Test] // Task order is counterbalanced by participant number (blocks of 12)
        public void TaskOrderCounterbalanced()
        {
            Assert.AreEqual(StudyTask.Touch, Plan(TaskSelection.Both, ConditionSelection.Both, "P5").sequence[0].trials[0].task);
            Assert.AreEqual(StudyTask.PickPlace, Plan(TaskSelection.Both, ConditionSelection.Both, "P17").sequence[0].trials[0].task);
        }
    }
}
