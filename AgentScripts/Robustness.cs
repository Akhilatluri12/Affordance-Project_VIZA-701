// Dev-only: drives tracking loss + experimenter pause/resume/skip during a simulated session.
using System.Threading.Tasks;
using System.Collections.Generic;
using UnityEngine;
using XRStudy.Task1;

var flow = Object.FindFirstObjectByType<StudyFlowController>();
var tm = Object.FindFirstObjectByType<TrialManager>();
var eff = Object.FindFirstObjectByType<EffectorTracker>();
var log = new System.Text.StringBuilder();
async Task<bool> Until(System.Func<bool> c, float s) { float t = Time.realtimeSinceStartup + s; while (!c()) { if (Time.realtimeSinceStartup > t) return false; await Task.Yield(); } return true; }

log.AppendLine("wait practice: " + await Until(() => tm.Running && tm.State == TrialState.AwaitContact, 60f));
// 1) Hand-tracking dropout mid-trial (simulated hand disappears for 1 s).
var sim = eff.Override;
eff.Override = () => new List<Effector>();
await Until(() => false, 1.0f);
log.AppendLine($"after dropout: state={tm.State} paused={tm.Paused} reason={tm.PauseReason}");
eff.Override = sim;
log.AppendLine("auto-resumed: " + await Until(() => !tm.Paused, 5f));
// 2) Experimenter pause during the next trial, then resume.
await Until(() => tm.State == TrialState.AwaitStart || tm.State == TrialState.Foreperiod, 10f);
flow.PauseTrials();
await Until(() => false, 0.5f);
log.AppendLine($"pause: paused={tm.Paused} reason={tm.PauseReason}");
await Until(() => false, 1.0f);
log.AppendLine($"still paused (needs Resume): {tm.Paused}");
flow.ResumeTrials();
log.AppendLine("resumed: " + await Until(() => !tm.Paused, 3f));
// 3) Skip.
await Until(() => tm.State == TrialState.AwaitStart, 10f);
string before = tm.CurrentPlanned?.plannedTrialId;
flow.SkipTrial();
await Until(() => false, 0.3f);
log.AppendLine($"skipped {before}; last={(tm.BlockRecords.Count > 0 ? tm.BlockRecords[tm.BlockRecords.Count - 1].outcome.ToString() : "-")}");
return log.ToString();
