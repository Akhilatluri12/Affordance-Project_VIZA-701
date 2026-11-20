using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace XRStudy.Task1
{
    [Serializable]
    public class ManifestFile
    {
        public string name;
        public string description;
        public long rows;
        public long bytes;
    }

    [Serializable]
    public class SessionManifest
    {
        public string schemaVersion;
        public string sessionId;
        public string participantId;
        public string status;               // InProgress | Completed | Aborted | Interrupted | Faulted
        public string createdUtc;
        public string updatedUtc;
        public string endedUtc;
        public string appVersion;
        public string unityVersion;
        public string protocolId;
        public string protocolVersion;
        public string protocolHash;
        public long seed;
        public int plannedTrials;
        public int attempts;
        public int validTrials;
        public int invalidAttempts;
        public int replacementAttempts;
        public string lastError = "";
        public string exportRoute = "adb pull /sdcard/Android/data/<bundle id>/files/Task1Sessions/<sessionId>";
        public List<ManifestFile> files = new List<ManifestFile>();
    }

    /// <summary>
    /// File layer of the DataLogger: unique session directory, incremental CSVs, flushes only when asked
    /// (trial boundaries), never overwrites a session. Any IO failure latches <see cref="Failed"/>.
    /// </summary>
    public sealed class SessionWriter : IDisposable
    {
        public const string RootFolderName = "Task1Sessions";

        public string Directory { get; }
        public bool Failed { get; private set; }
        public string LastError { get; private set; } = "";
        public SessionManifest Manifest { get; }

        sealed class CsvFile
        {
            public string name;
            public string description;
            public StreamWriter writer;
            public readonly StringBuilder buffer = new StringBuilder(1 << 16);
            public long rows;
        }

        readonly Dictionary<string, CsvFile> m_Files = new Dictionary<string, CsvFile>();
        readonly List<(string name, string description)> m_JsonFiles = new List<(string, string)>();

        public const string TrialsFile = "trials.csv";
        public const string EventsFile = "events.csv";
        public const string PosesFile = "poses.csv";
        public const string RecallFile = "recall_checks.csv";
        /// <summary>Name of the per-session responses CSV (the requested one-row-per-trial file).</summary>
        public string ResponsesFile { get; private set; } = "responses.csv";

        public static string SessionsRoot(string persistentDataPath) => Path.Combine(persistentDataPath, RootFolderName);

        SessionWriter(string dir, SessionManifest manifest)
        {
            Directory = dir;
            Manifest = manifest;
        }

        /// <summary>Creates a brand-new session directory. Throws if it already exists (no overwrites).</summary>
        public static SessionWriter Create(string root, string sessionId, string participantId, string schemaVersion, string responsesFileName = "responses.csv")
        {
            System.IO.Directory.CreateDirectory(root);
            string dir = Path.Combine(root, sessionId);
            if (System.IO.Directory.Exists(dir)) throw new IOException("Session directory already exists: " + dir);
            System.IO.Directory.CreateDirectory(dir);

            var m = new SessionManifest
            {
                schemaVersion = schemaVersion,
                sessionId = sessionId,
                participantId = participantId,
                status = "InProgress",
                createdUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                appVersion = Application.version,
                unityVersion = Application.unityVersion,
            };
            var w = new SessionWriter(dir, m) { ResponsesFile = responsesFileName };
            w.OpenCsv(w.ResponsesFile, "One row per trial attempt (training flagged); requested study schema.", ResponsesCsv.Header);
            w.OpenCsv(TrialsFile, "Detailed per-attempt timing record (all timestamps, flags, diagnostics).", TrialCsv.Header);
            w.OpenCsv(EventsFile, "All timestamped application events in order.",
                Csv.Row("schemaVersion", "sessionId", "eventId", "time_s", "frame", "blockId", "plannedTrialId", "attemptId", "type",
                    "mugId", "interactor", "state", "detail"));
            w.OpenCsv(PosesFile, "Per-rendered-frame poses (world space). Position m, rotation quaternion.",
                Csv.Row("sessionId", "attemptId", "time_s", "frame", "frameDt_s", "objectId", "pos_x_m", "pos_y_m", "pos_z_m",
                    "rot_x", "rot_y", "rot_z", "rot_w", "trackingValid"));
            w.OpenCsv(RecallFile, "Ownership recall quiz answered by the participant during training.",
                Csv.Row("sessionId", "participantId", "attempt", "mugLabel", "trueOwner", "answeredOwner", "correct", "time_s"));
            w.WriteManifest();
            return w;
        }

        void OpenCsv(string name, string description, string header)
        {
            var path = Path.Combine(Directory, name);
            var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            var sw = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = false, NewLine = "\n" };
            sw.WriteLine(header);
            sw.Flush();
            fs.Flush(true);
            m_Files[name] = new CsvFile { name = name, description = description, writer = sw };
        }

        /// <summary>Buffers one CSV line in memory; no disk IO until <see cref="Flush"/>.</summary>
        public void Append(string file, string line)
        {
            if (Failed) return;
            var f = m_Files[file];
            f.buffer.Append(line).Append('\n');
            f.rows++;
        }

        public int BufferedChars
        {
            get { int n = 0; foreach (var f in m_Files.Values) n += f.buffer.Length; return n; }
        }

        /// <summary>Writes buffered rows and forces them to storage. Call only outside timed states.</summary>
        public bool Flush()
        {
            if (Failed) return false;
            try
            {
                foreach (var f in m_Files.Values)
                {
                    if (f.buffer.Length == 0) continue;
                    f.writer.Write(f.buffer.ToString());
                    f.buffer.Clear();
                    f.writer.Flush();
                    ((FileStream)f.writer.BaseStream).Flush(true);
                }
                return true;
            }
            catch (Exception e) { Fail(e); return false; }
        }

        /// <summary>Writes a JSON document once. Refuses to overwrite.</summary>
        public bool WriteJsonOnce(string name, string json, string description)
        {
            if (Failed) return false;
            try
            {
                var path = Path.Combine(Directory, name);
                using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                using (var sw = new StreamWriter(fs, new UTF8Encoding(false)))
                {
                    sw.Write(json);
                    sw.Flush();
                    fs.Flush(true);
                }
                m_JsonFiles.Add((name, description));
                return true;
            }
            catch (Exception e) { Fail(e); return false; }
        }

        public bool WriteManifest()
        {
            try
            {
                Manifest.updatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                Manifest.lastError = LastError;
                Manifest.files.Clear();
                foreach (var (name, description) in m_JsonFiles) Manifest.files.Add(Describe(name, description, -1));
                foreach (var f in m_Files.Values) Manifest.files.Add(Describe(f.name, f.description, f.rows - Pending(f)));
                var path = Path.Combine(Directory, "manifest.json");
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonUtility.ToJson(Manifest, true), new UTF8Encoding(false));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                return true;
            }
            catch (Exception e) { Fail(e); return false; }
        }

        static long Pending(CsvFile f)
        {
            if (f.buffer.Length == 0) return 0;
            long n = 0;
            for (int i = 0; i < f.buffer.Length; i++) if (f.buffer[i] == '\n') n++;
            return n;
        }

        ManifestFile Describe(string name, string description, long rows)
        {
            var path = Path.Combine(Directory, name);
            long bytes = File.Exists(path) ? new FileInfo(path).Length : 0;
            return new ManifestFile { name = name, description = description, rows = rows, bytes = bytes };
        }

        public void Close(string status)
        {
            Flush();
            Manifest.status = Failed && status == "Completed" ? "Faulted" : status;
            Manifest.endedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            WriteManifest();
            Dispose();
        }

        void Fail(Exception e)
        {
            Failed = true;
            LastError = e.GetType().Name + ": " + e.Message;
            Debug.LogError("[Task1] Logging failure: " + LastError);
        }

        public void Dispose()
        {
            foreach (var f in m_Files.Values)
            {
                try { f.writer?.Dispose(); } catch { /* already reported */ }
                f.writer = null;
            }
        }

        /// <summary>Sessions left InProgress by a crash/kill are marked Interrupted and never resumed.</summary>
        public static List<string> MarkInterruptedSessions(string root)
        {
            var marked = new List<string>();
            if (!System.IO.Directory.Exists(root)) return marked;
            foreach (var dir in System.IO.Directory.GetDirectories(root))
            {
                var path = Path.Combine(dir, "manifest.json");
                if (!File.Exists(path)) continue;
                try
                {
                    var m = JsonUtility.FromJson<SessionManifest>(File.ReadAllText(path));
                    if (m == null || m.status != "InProgress") continue;
                    m.status = "Interrupted";
                    m.endedUtc = m.updatedUtc;
                    m.lastError = "Application ended before the session closed; detected at next launch " +
                                  DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                    File.WriteAllText(path, JsonUtility.ToJson(m, true));
                    marked.Add(m.sessionId);
                }
                catch (Exception e) { Debug.LogWarning("[Task1] Could not inspect " + path + ": " + e.Message); }
            }
            return marked;
        }
    }

    /// <summary>The requested per-session CSV: one row per attempt, written as each attempt ends.</summary>
    public static class ResponsesCsv
    {
        public static readonly string Header = Csv.Row(
            "participant_id", "session_timestamp", "condition_order", "block", "agent_present", "trial_index", "is_training",
            "target_ownership", "target_slot", "mug_touched", "correct", "cue_onset_time", "response_time_s", "hand_used",
            "rng_seed", "layout_name",
            // extra columns (appended so the requested ones keep their positions)
            "mug_order", "planned_trial_id", "attempt", "repeats_attempt", "outcome", "valid", "movement_onset_time",
            "initiation_time_s", "movement_time_s", "contact_time", "foreperiod_s", "slow_frames", "session_id",
            // pick & place (blank for the touch task)
            "task", "release_time", "confirmation_time", "placement_duration_s", "confirmation_latency_s");

        public static string Row(TrialRecord r, string participantId, string sessionTimestamp, string conditionOrder, long seed,
            string mugOrder, string sessionId)
        {
            var p = r.planned;
            return Csv.Row(
                participantId, sessionTimestamp, conditionOrder, p.blockId, Csv.B(p.presence == PresenceCondition.AgentPresent),
                Csv.I(p.trialIndexInBlock + 1), Csv.B(p.training), p.targetOwner.ToString(), p.targetSlotName, r.contactMugId,
                HasResponse(r) ? Csv.B(r.Correct) : "", Csv.T(r.cueTime), Csv.T(r.ResponseTime), r.contactEffector,
                Csv.L(seed), p.layoutName,
                mugOrder, p.plannedTrialId, Csv.I(r.attemptNumber), r.replacesAttemptId ?? "", r.outcome.ToString(), Csv.B(r.Valid),
                Csv.T(r.movementOnsetTime), Csv.T(r.InitiationTime), Csv.T(r.MovementTime), Csv.T(r.contactTime),
                Csv.T(r.foreperiodPlanned), Csv.I(r.slowFrames), sessionId,
                p.task.ToString(), Csv.T(r.releaseTime), Csv.T(r.confirmationTime), Csv.T(r.PlacementDuration), Csv.T(r.ConfirmationLatency));
        }

        /// <summary>A mug was touched/grabbed after the cue, so "correct" is meaningful.</summary>
        public static bool HasResponse(TrialRecord r) => r.contactMugId.Length > 0 && r.ResponseTime.HasValue;
    }

    public static class TrialCsv
    {
        public static readonly string Header = Csv.Row(
            "schemaVersion", "appVersion", "sessionId", "participantId", "taskId", "blockId", "plannedTrialId", "attemptId",
            "attemptNumber", "replacesAttemptId", "trainingFlag", "task", "seed", "layoutName", "mugOrder", "targetOwner", "targetSlot",
            "agentPresent", "responseHand", "restEffector",
            "tableOrigin_x_m", "tableOrigin_y_m", "tableOrigin_z_m", "tableYaw_deg", "handRest_x_m", "handRest_y_m", "handRest_z_m",
            "pad_x_m", "pad_y_m", "pad_z_m",
            "slot0_x_m", "slot0_y_m", "slot0_z_m", "slot1_x_m", "slot1_y_m", "slot1_z_m", "slot2_x_m", "slot2_y_m", "slot2_z_m",
            "attemptStart_s", "ready_s", "foreperiodPlanned_s", "cue_s", "cueFrame", "cueOff_s", "cueOffFrame",
            "movementOnset_s", "movementOnsetFrame", "movementConfirm_s", "contact_s", "contactFrame",
            "firstRelease_s", "release_s", "releaseFrame", "confirmation_s", "confirmationFrame", "end_s", "endFrame",
            "responseTime_s", "initiationTime_s", "movementTime_s", "placementDuration_s", "confirmationLatency_s",
            "contactMug", "contactOwner", "contactEffector", "correct",
            "outcome", "outcomeStage", "valid", "technicalInvalid", "errorFlags", "duplicateEvents",
            "framesInAttempt", "slowFrames", "maxFrameDt_s", "trackingDropouts", "outcomeDetail");

        public static string Row(TrialRecord r, string schemaVersion, string sessionId, string participantId, string taskId,
            long seed, string mugOrder, StudyHand hand)
        {
            var p = r.planned;
            return Csv.Row(
                schemaVersion, Application.version, sessionId, participantId, taskId, p.blockId, p.plannedTrialId, r.attemptId,
                Csv.I(r.attemptNumber), r.replacesAttemptId ?? "", Csv.B(p.training), p.task.ToString(), Csv.L(seed), p.layoutName, mugOrder,
                p.targetOwner.ToString(), p.targetSlotName, Csv.B(p.presence == PresenceCondition.AgentPresent), hand.ToString(), r.restEffector,
                Csv.F(r.tableOrigin.x), Csv.F(r.tableOrigin.y), Csv.F(r.tableOrigin.z), Csv.F(r.tableYaw_deg),
                Csv.F(r.handRest.x), Csv.F(r.handRest.y), Csv.F(r.handRest.z),
                Csv.F(r.padCentre.x), Csv.F(r.padCentre.y), Csv.F(r.padCentre.z),
                Csv.F(r.mugSlot0.x), Csv.F(r.mugSlot0.y), Csv.F(r.mugSlot0.z), Csv.F(r.mugSlot1.x), Csv.F(r.mugSlot1.y), Csv.F(r.mugSlot1.z),
                Csv.F(r.mugSlot2.x), Csv.F(r.mugSlot2.y), Csv.F(r.mugSlot2.z),
                Csv.T(r.attemptStart), Csv.T(r.readyTime), Csv.T(r.foreperiodPlanned), Csv.T(r.cueTime), Csv.I(r.cueFrame),
                Csv.T(r.cueOffTime), Csv.I(r.cueOffFrame), Csv.T(r.movementOnsetTime), Csv.I(r.movementOnsetFrame),
                Csv.T(r.movementConfirmTime), Csv.T(r.contactTime), Csv.I(r.contactFrame),
                Csv.T(r.firstReleaseTime), Csv.T(r.releaseTime), Csv.I(r.releaseFrame), Csv.T(r.confirmationTime), Csv.I(r.confirmationFrame),
                Csv.T(r.endTime), Csv.I(r.endFrame),
                Csv.T(r.ResponseTime), Csv.T(r.InitiationTime), Csv.T(r.MovementTime), Csv.T(r.PlacementDuration), Csv.T(r.ConfirmationLatency),
                r.contactMugId, r.contactOwner, r.contactEffector,
                ResponsesCsv.HasResponse(r) ? Csv.B(r.Correct) : "",
                r.outcome.ToString(), r.outcomeStage, Csv.B(r.Valid), Csv.B(OutcomeInfo.IsTechnical(r.outcome)),
                r.flags == TrialFlags.None ? "" : r.flags.ToString().Replace(", ", "|"), Csv.I(r.duplicateEvents),
                Csv.I(r.framesInAttempt), Csv.I(r.slowFrames), Csv.F(r.maxFrameDt_s), Csv.I(r.trackingDropouts), r.outcomeDetail);
        }

        public static string EventRow(StudyEvent e, string schemaVersion, string sessionId) =>
            Csv.Row(schemaVersion, sessionId, Csv.L(e.eventId), Csv.T(e.time_s), Csv.I(e.frame), e.blockId, e.plannedTrialId,
                e.attemptId, e.type, e.mugId, e.interactor, e.state, e.detail);
    }
}
