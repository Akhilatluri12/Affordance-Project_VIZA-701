using System.Linq;
using System.IO;
using NUnit.Framework;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.TestTools;

namespace XRStudy.Task1.Tests
{
    public class SessionWriterTests
    {
        string m_Root;
        ProtocolConfig m_Cfg;
        const string Responses = "P01_test.csv";

        [SetUp]
        public void SetUp()
        {
            m_Root = Path.Combine(Path.GetTempPath(), "task1_tests_" + System.Guid.NewGuid().ToString("N"));
            m_Cfg = TestFixtures.Config();
        }

        [TearDown]
        public void TearDown()
        {
            TestFixtures.Destroy(m_Cfg);
            if (Directory.Exists(m_Root)) Directory.Delete(m_Root, true);
        }

        static string Read(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            return sr.ReadToEnd();
        }

        TrialRecord MakeRecord()
        {
            var sch = ScheduleGenerator.Generate(m_Cfg, 11, "S1", "P01", ConditionOrderMode.Auto, MugOrderMode.Auto, 12);
            return new TrialRecord
            {
                attemptId = "B1-T01-a1", attemptNumber = 1, planned = sch.blocks[0].trials[0],
                cueTime = 1.5, movementOnsetTime = 1.8, contactTime = 2.3, outcome = TrialOutcome.WrongTarget,
                contactMugId = "N", contactOwner = "Neutral", contactEffector = "RightHand"
            };
        }

        [Test] // Responses CSV: requested columns, one row per attempt, blank (not 0) for missing values
        public void ResponsesRowHasRequestedSchema()
        {
            var w = SessionWriter.Create(m_Root, "S0", "P01", "2.0", Responses);
            var r = MakeRecord();
            w.Append(w.ResponsesFile, ResponsesCsv.Row(r, "P01", "2026-11-19T10:00:00Z", "AbsentFirst", 11, "P1-N-NPC", "S0"));
            var timeout = MakeRecord(); timeout.outcome = TrialOutcome.TimeoutBeforeContact; timeout.contactTime = null; timeout.contactMugId = "";
            w.Append(w.ResponsesFile, ResponsesCsv.Row(timeout, "P01", "2026-11-19T10:00:00Z", "AbsentFirst", 11, "P1-N-NPC", "S0"));
            Assert.IsTrue(w.Flush());
            var lines = Read(Path.Combine(w.Directory, Responses)).TrimEnd('\n').Split('\n');
            var header = lines[0].Split(',');
            CollectionAssert.AreEqual(new[] { "participant_id", "session_timestamp", "condition_order", "block", "agent_present", "trial_index",
                "is_training", "target_ownership", "target_slot", "mug_touched", "correct", "cue_onset_time", "response_time_s", "hand_used",
                "rng_seed", "layout_name" }, header.Take(16).ToArray());
            var row = lines[1].Split(',');
            Assert.AreEqual("N", row[9]);
            Assert.AreEqual("0", row[10]);
            Assert.AreEqual("0.800000", row[12]);
            Assert.AreEqual("RightHand", row[13]);
            var row2 = lines[2].Split(',');
            Assert.AreEqual("", row2[10], "no correctness without a response");
            Assert.AreEqual("", row2[12], "missing response time is blank, never 0");
            w.Dispose();
        }

        [Test] // AC-09: committed rows are on disk before the session closes (survive forced termination)
        public void FlushedRowsSurviveWithoutClose()
        {
            var w = SessionWriter.Create(m_Root, "S1", "P01", "2.0", Responses);
            var r = MakeRecord();
            w.Append(SessionWriter.TrialsFile, TrialCsv.Row(r, "2.0", "S1", "P01", "Task1", 11, "P1-N-NPC", StudyHand.Right));
            Assert.IsTrue(w.Flush());
            // Simulate a kill: no Close(), no Dispose().
            var lines = Read(Path.Combine(w.Directory, SessionWriter.TrialsFile)).TrimEnd('\n').Split('\n');
            Assert.AreEqual(2, lines.Length);
            StringAssert.Contains("WrongTarget", lines[1]);
            StringAssert.StartsWith("schemaVersion,", lines[0]);
            Assert.IsTrue(File.Exists(Path.Combine(w.Directory, Responses)));
            w.Dispose();
        }

        [Test] // Reliability: an InProgress session is marked Interrupted on next launch, never resumed
        public void UnclosedSessionIsMarkedInterrupted()
        {
            var w = SessionWriter.Create(m_Root, "S2", "P02", "1.0");
            w.Dispose();
            var marked = SessionWriter.MarkInterruptedSessions(m_Root);
            CollectionAssert.AreEqual(new[] { "S2" }, marked);
            var m = JsonUtility.FromJson<SessionManifest>(File.ReadAllText(Path.Combine(m_Root, "S2", "manifest.json")));
            Assert.AreEqual("Interrupted", m.status);
        }

        [Test] // Export: unique session directory, no overwrites
        public void ExistingSessionIsNeverOverwritten()
        {
            SessionWriter.Create(m_Root, "S3", "P03", "1.0").Close("Completed");
            Assert.Throws<IOException>(() => SessionWriter.Create(m_Root, "S3", "P03", "1.0"));
        }

        [Test]
        public void CloseWritesManifestWithFilesAndRows()
        {
            var w = SessionWriter.Create(m_Root, "S4", "P04", "1.0");
            Assert.IsTrue(w.WriteJsonOnce("schedule.json", "{}", "schedule"));
            w.Append(SessionWriter.EventsFile, "x");
            w.Close("Completed");
            var m = JsonUtility.FromJson<SessionManifest>(File.ReadAllText(Path.Combine(m_Root, "S4", "manifest.json")));
            Assert.AreEqual("Completed", m.status);
            Assert.IsTrue(m.files.Exists(f => f.name == SessionWriter.EventsFile && f.rows == 1));
            Assert.IsTrue(m.files.Exists(f => f.name == "schedule.json"));
        }

        [Test] // Logging failure latches: no further rows are accepted and the session cannot close as Completed
        public void WriteFailureLatchesFault()
        {
            var w = SessionWriter.Create(m_Root, "S5", "P05", "1.0");
            Assert.IsTrue(w.WriteJsonOnce("schedule.json", "{}", "schedule"));
            LogAssert.Expect(LogType.Error, new Regex("Logging failure"));
            Assert.IsFalse(w.WriteJsonOnce("schedule.json", "{}", "schedule"), "JSON files are write-once");
            Assert.IsTrue(w.Failed);
            Assert.IsFalse(w.Flush());
            w.Close("Completed");
            var m = JsonUtility.FromJson<SessionManifest>(File.ReadAllText(Path.Combine(m_Root, "S5", "manifest.json")));
            Assert.AreEqual("Faulted", m.status);
            Assert.IsNotEmpty(m.lastError);
        }

        [Test]
        public void CsvEscapingAndInvariantNumbers()
        {
            Assert.AreEqual("\"a,b\"", Csv.Escape("a,b"));
            Assert.AreEqual("\"say \"\"hi\"\"\"", Csv.Escape("say \"hi\""));
            Assert.AreEqual("\"line\nbreak\"", Csv.Escape("line\nbreak"));
            Assert.AreEqual("", Csv.T(null));
            var prev = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
                Assert.AreEqual("1.500000", Csv.T(1.5));
                Assert.AreEqual("0.25", Csv.F(0.25f));
            }
            finally { System.Threading.Thread.CurrentThread.CurrentCulture = prev; }
        }
    }
}
