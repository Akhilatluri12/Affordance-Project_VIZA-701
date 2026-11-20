# Task 1 – Mug ownership study (Quest 3 mixed reality)

Two tasks share one scene (`Task1_MugStudy_MR`). On setup **step 2 "Session plan"** the experimenter chooses
**Task**: Touch / Pick & place / **Both tasks** and **Alex**: **With & without Alex** (counterbalanced) / **Without Alex
only** / **With Alex only**. The default is the full design: both tasks, with and without Alex. The step-4 summary
lists the exact run order, e.g. *Touch practice → Touch without Alex → Touch with Alex → Pick & place practice →
Pick & place without Alex → Pick & place with Alex*. With both tasks, each task gets its own practice; the task order
is counterbalanced by participant number (touch first for 0–11, 24–35, …; pick & place first for 12–23, …). The
agent order inside each task follows the condition order (Auto: even = without Alex first, odd = with Alex first).
Alex is always introduced during the ownership explanation (so participants learn which mug is his), even in a
"Without Alex only" session; he is absent for all of that session's practice and trials.

* **Touch** – touch the cued mug. Response time = first contact − cue onset.
* **Pick & place** (Product Spec Task 1, cued reach–grab–place) – pick up the cued mug with the designated hand and
  put it down on the white square. Response time = first grab − cue onset (cue-to-grab); also logged: initiation
  time, reach-to-grasp, placement duration (release − grab) and confirmation latency (stable − release).

Participants sit at a **real table** and see it through **colour passthrough**. Only the three mugs, the hand-rest
oval, the agent (Layout 2) and the instruction panel are virtual. On each trial one mug lights up and the
participant touches it as quickly as possible.

**Response time = first contact with any mug − cue onset (seconds).**

Design (from the slides in `Docs/Reference/`): within-subject. Agent absent (Layout 1) vs present (Layout 2) ×
mug ownership: Owned (P1, blue diamond), Neutral (N, green triangle), AgentOwned (NPC, red square).

## Decisions applied (defaults you approved)

| Question | Implemented |
|---|---|
| Trial task / timer | Rest hand → random foreperiod (0.8–1.5 s) → cue → **first touch of any mug** stops the timer; mug touched + correct logged |
| Dark-blue oval | Hand rest. The cue only appears after the hand has rested on it for 0.5 s |
| IRI | Outside the app; the app records only the participant ID |
| Mug order | Each mug keeps its colour/label/shape; the **left-to-right order rotates across participants** (6 orders, from the participant number). "Fixed" (P1-N-NPC) is selectable on the setup screen |
| Ownership | Taught in training (each mug highlighted with its owner), then checked by **touching** "your mug / Alex's mug / the mug that belongs to no one". A wrong answer repeats the introduction |
| Input | Hand tracking primary; controllers work too (logged as `hand_used`). Hover highlight on, identical for all mugs |
| Agent | Realistic **virtual human** from the team's virtual-humans project (Reallusion `Young_kev` male by default, `Young_cami` female optional) seated on that project's black chair, playing its seated-idle loop, with head gaze toward the participant. The primitive mannequin remains as a fallback |
| Pick & place | Re-implemented for MR from the Product Spec (sections 5, 7–10): designated hand only, near grab only (no rays), common pad with no snapping, release + full stability hold, distinct outcome codes, safe reset |

## What's in the project

| What | Path |
|---|---|
| Scene (build index 0) | `Assets/Scenes/Task1_MugStudy_MR.unity` |
| Protocol (timing, counts, visuals, table size) | `Assets/Settings/Task1/Task1ProtocolConfig.asset` |
| Layouts | `Assets/Settings/Task1/Layouts/Layout1_AgentAbsent`, `Layout2_AgentPresent`, `FutureLayout_DepthZones` |
| Mug prefab | `Assets/Prefabs/Task1/StudyMug.prefab` |
| Runtime code | `Assets/Scripts/Task1/Runtime/` (Core = pure logic, Components = scene scripts) |
| Scene builder, build stamp | `Assets/Scripts/Task1/Editor/` |
| Tests (50) | `Assets/Scripts/Task1/Tests/Editor/` |

Rebuild the scene with **XR Study → Task 1 → Build Study Scene**. Protocol and layout assets are never overwritten.

### Scripts (one job each)

| Script | Job |
|---|---|
| `StudyFlowController` | Setup → Calibration → Training → Block → Break → Block → End; session files; experimenter actions |
| `TrialManager` | Runs a block: instructions, feedback, inter-trial interval, idle prompt, auto-resume, repeats |
| `TrialStateMachine` (pure C#) | Sole owner of trial state, cue timing and outcomes |
| `ScheduleGenerator` | Balanced, seeded target order per block; counterbalancing from the participant ID |
| `StudyLayout` (ScriptableObject) | Mug slots (x, z, yaw), hand-rest, agent, panel positions, all table-relative |
| `LayoutController` | Places everything from a layout; reset-to-slot; Editor gizmos |
| `TableCalibration` / `ARTableDetector` | Table origin (saved across restarts) / optional auto-detection from room setup |
| `TableOccluder` | Invisible depth-only box for the real table, so the agent's legs are hidden behind it |
| `EffectorTracker` | Fingertips + palm (XR Hands) or controller tip points, once per frame |
| `ContactDetector` | First-contact events against mug volume + 1 cm padding; hover |
| `HandRestMarker` | Oval measurement and idle / ready visuals |
| `StudyMug`, `OwnershipTag` | Mug visuals, cue, hover, flash, contact distance; ownership field |
| `AgentPresenceController` | Agent placement, show/hide, idle and gaze |
| `StudyPanel`, `ExperimenterControls`, `FeedbackPlayer` | World-space UI, hidden experimenter panel, audio/haptics |
| `DataLogger`, `SessionWriter` | Incremental CSV/JSON writing, manifest |
| `TrialSimulator` | Debug mode: plays participant + experimenter without a headset (touch or pick & place) |
| `PlacementPad`, `PlacementEvaluator` | Pick & place pad marker; containment (bounds + vertical tolerance), tilt and speed for the stability check |
| `DesignatedHandSelectFilter` | Only the designated hand (hand or controller) can grab; poke never grabs; identical on every mug |
| `GrabEventBridge` | Forwards XRI grab/release with interactor identity and duplicate-proof keys |

### Adding the Future Layout

The asset already exists (`FutureLayout_DepthZones`: P1 near-left, N middle-centre, NPC far-right; order fixed
because ownership is tied to depth). To use it, assign it to the protocol's **Agent Present Layout** (and/or make an
agent-absent twin by duplicating it and unticking *Show Agent*). No code changes are needed.

## Settings changed

* **Scene**: the old reach-grab-place scene was removed from `Assets/` (backed up). `Task1_MugStudy_MR` is build
  index 0; `SampleScene` stays in the list, disabled.
* **Protocol asset** recreated with the new defaults. The old one is backed up next to the old code.
* **Rig**: XRI *XR Origin Hands* (hands + controllers, poke + ray UI). Locomotion, turning, teleport, climb and gaze
  removed. Floor tracking. Camera background solid black, alpha 0. AR Camera Manager (passthrough), AR Session,
  AR Plane Manager (off until auto-detect), EventSystem + XR UI Input Module, world-space canvases with Tracked
  Device Graphic Raycasters.
* **Rendering**: no skybox or environment, no shadows, unlit/simple-lit materials, post-processing off on the
  camera. The URP asset already had HDR off and no post-processing, and Android uses Vulkan, as Meta recommends
  for passthrough.
* **Unchanged (already correct)**: Android, IL2CPP, ARM64, min/target API 34, OpenXR with the Meta Quest feature
  group (Quest 3 enabled), Camera (Passthrough), Hand Tracking, Display Utilities (90 Hz request).

### Set by hand (Editor)

1. **Measure the real table** and enter `Real Table Width/Depth` in the protocol (default 1.2 × 0.8 m). This sizes
   the occluder that hides the agent's legs.
2. **Measure `Controller On Table Height`**: the controller's tracking point above the table when it lies flat (default 3 cm).
   If the mugs float or sink after calibration, adjust it or use the ±5 mm buttons.
3. *(Optional)* **Package name**: still `com.DefaultCompany.MixedRealityTemplate` (Player Settings).
4. *(Optional)* **Agent avatar**: pick `VirtualHumanMale` / `VirtualHumanFemale` / `Placeholder` in the protocol.
   To use another rigged humanoid, add it like the two in `VirtualHumanSetup` (seated idle + `AgentLookAt` + `SeatedAvatarAligner`).
5. On the **headset**: Settings → Movement tracking → Hand tracking **on**, with automatic switching between hands
   and controllers. For auto-detect, the table must be part of the room setup (Space Setup).

## Virtual human (agent "Alex")

* **Choice**: protocol → *Agent Avatar* = `VirtualHumanMale` (default), `VirtualHumanFemale` or `Placeholder`. The same body is
  used for every participant and is saved in `session_metadata.json` (`agentAvatar`).
* **Behaviour (identical in every trial and condition)**: `SittingIdle` loop from the virtual-humans project, no root motion;
  `AgentLookAt` turns the head toward the participant's head with fixed weights via Animator IK; `SeatedAvatarAligner` puts
  the hips on the chair seat once per session. No speech, no gestures, no reactions to trials. No colliders.
* **What was taken from the attached project** (`Assets/ThirdParty/VirtualHumans/`, see `SOURCE.md` there): the two rigged
  character FBX files with their diffuse/normal/opacity textures, `SittingIdle.fbx` and `black_chair.fbx`, about 190 MB.
  Not taken: its CC Unity Tools shaders/materials, prefabs, scripts, speech (Whisper/Chatterbox), OpenAI/LLM and
  lipsync components. The original project was not modified.
* **Quest-friendly conversion** (**XR Study → Task 1 → Set Up Virtual Humans**, run automatically by Build Study
  Scene): humanoid import; textures ≤ 1024 px ASTC; URP Simple Lit materials (alpha-clipped hair, brows and lashes);
  no shadows; facial blend shapes dropped and meshes compressed (APK 138 MB). Reallusion's cornea, eye-occlusion and
  tear-line overlays need Reallusion's shaders, so they are hidden, and the eyeballs are drawn matte with sharp irises.
  The eye and jaw bones are left out of the humanoid mapping so the eyes look forward and the mouth stays closed
  (Unity's humanoid retargeting otherwise rolls CC eyes back and opens the jaw).
* **Licence**: the characters are Reallusion Character Creator content from the attached project; make sure your
  team may use them for this study.

## Pick & place details

* **Trial**: hand on the oval → foreperiod → cue (yellow ring + halo, stays on until the grab) → hand leaves the oval →
  first grab → carry → release inside the white square → mug must stay inside, upright (≤ 15°), and still
  (< 0.03 m/s, < 10 °/s) for 0.3 s → confirmed.
* **Grabbing**: hands (pinch/grab) or the controller grip, designated hand only. Far casting (ray grabs) is switched
  off on all interactors during pick & place blocks and switched back on for the UI between blocks.
* **Physics**: mugs are physical only in pick & place (same rigidbody, colliders, friction and attach settings for
  all three) and rest on an invisible table-top collider at the calibrated height. In the touch task they stay fixed.
* **Outcomes**: Success, WrongTarget (grabbed another mug), PrematureContact (grab before the cue),
  NonDesignatedSelection, TimeoutBeforeMovement / TimeoutBeforeGrab (5 s from cue) / TimeoutBeforePlacement (10 s
  from grab), ReleaseOutsidePad, PlacementLeftPad, PlacementUnstableTimeout (3 s settle), OutOfBoundsDrop, plus the
  technical codes. Placement failures and timeouts are repeated at block end; a wrong grab is a valid response (correct = 0).
* **Reset**: after each trial the app waits for the participant to let go (asks "Please put the mug down"), cancels
  any remaining grab through XRI after 4 s, then freezes, zeroes velocities and moves every mug back to its slot.
* **Settings**: pad position/size are in each `StudyLayout` (default 14 × 14 cm at 33 cm right, 13 cm in); timing and
  tolerances in the protocol under *Pick & place*. Pilot these from the real mug/table, as the spec requires.

## Running a session

1. **Setup (experimenter)**: step 1 participant number (P + digits); step 2 **session plan** – task(s) and Alex
   condition(s), plus the condition order when both conditions run; step 3 trials per block (multiple of 3,
   default 12) and mug order (Auto/Fixed); step 4 summary with the run order → *Start session*.
2. **Calibration**: lay the right controller flat on the table at the middle of the near edge, pointing across the
   table. The virtual mugs follow it live. Pull the trigger to capture, check the result, use ±5 mm if needed, then
   press *Looks right*. The calibration is saved. On the next launch choose *Use saved* or *Recalibrate*.
3. **Training**: welcome → ownership introduction (each mug lights up) → touch quiz → how-it-works → 6 practice
   trials with correct/incorrect feedback.
4. **Block 1 → break → Block 2**: neutral "Next" feedback only. Identical wording in both conditions.
5. **End**: files are closed and the manifest is marked `Completed`.

The participant continues every page by **resting the hand on the oval for 1 s** (always in reach) or by pressing
the button with a hand or controller ray.

**Experimenter panel** (hidden): left-controller **menu button**, the left-hand **menu pinch gesture**, or **F2/M**
in the Editor. Pause / Resume / Skip trial / Recalibrate / Abort (with confirmation).

**Robustness**: hand-tracking loss or app focus loss mid-trial invalidates the trial. The app resumes by itself when the
hand is back, and the trial is repeated at the end of the block (at most 2 repeats). Early movement, touching before the
cue and timeouts are also repeated. A wrong mug is a valid response (correct = 0) and is not repeated. If the hand
doesn't reach the oval for 6 s, a fuller instruction is shown.

## Timing

Hand and controller poses update once per rendered frame, so contact is checked once per frame against the mug
volume (+1 cm). The cue onset is the frame the cue is requested. Response-time resolution is **one frame: ~11 ms at
90 Hz** (requested) or ~14 ms at 72 Hz, plus a tracking latency that is the same in every condition. A `Stopwatch`
or physics callbacks can't do better: physics runs at 50 Hz here, and poses don't change between frames. Slow frames
are counted per trial (`slow_frames`). The clock is `Time.realtimeSinceStartupAsDouble`.

## Data (CSV schema)

Saved under `Application.persistentDataPath/Task1Sessions/<sessionId>/`, written as each trial ends (a crash loses at
most the current trial). Copy everything off the headset with:

```bash
adb pull /sdcard/Android/data/com.DefaultCompany.MixedRealityTemplate/files/Task1Sessions/
```

### `<participant>_<yyyyMMdd_HHmmss>.csv`: one row per trial attempt (requested schema)

| Column | Meaning |
|---|---|
| participant_id | e.g. `P12` |
| session_timestamp | UTC start time (ISO 8601) |
| condition_order | `AbsentFirst` / `PresentFirst` |
| block | `Training`, `B1`, `B2` |
| agent_present | 1 / 0 |
| trial_index | position in the block's planned order (1-based; repeats keep the original index) |
| is_training | 1 / 0 |
| target_ownership | `Owned` / `Neutral` / `AgentOwned` |
| target_slot | slot name in the layout (`Left` / `Center` / `Right`) |
| mug_touched | label of the first mug touched (`P1`/`N`/`NPC`), blank if none |
| correct | 1 / 0; blank when there was no response |
| cue_onset_time | s, monotonic clock |
| response_time_s | first contact − cue onset; blank when there was no response |
| hand_used | `RightHand`, `LeftHand`, `RightController`, `LeftController` |
| rng_seed | schedule seed (the schedule is reproducible from it) |
| layout_name | `Layout1` / `Layout2` |
| *extra:* mug_order, planned_trial_id, attempt, repeats_attempt, outcome, valid, movement_onset_time, initiation_time_s, movement_time_s (touch: contact − onset; pick & place: reach-to-grasp), contact_time, foreperiod_s, slow_frames, session_id | |
| *pick & place:* task, release_time, confirmation_time, placement_duration_s, confirmation_latency_s | blank for the touch task |

In pick & place, `mug_touched` is the mug that was **grabbed** and `response_time_s` is **cue-to-grab**. `correct`
is filled whenever a mug was touched/grabbed after the cue (so a correct grab that was then dropped shows correct = 1
with outcome ReleaseOutsidePad / OutOfBoundsDrop and valid = 0).

`outcome` is one of: Success, WrongTarget, PrematureDeparture, PrematureContact, TimeoutBeforeMovement,
TimeoutBeforeContact, TimeoutBeforeGrab, TimeoutBeforePlacement, ReleaseOutsidePad, PlacementLeftPad,
PlacementUnstableTimeout, OutOfBoundsDrop, NonDesignatedSelection, TrackingLoss, FocusLoss, ResearcherPause,
OperatorSkip, OperatorAbort, LoggingFailure.
Analyse rows with `valid = 1` and `is_training = 0`.

### Other files

`trials.csv` (full per-attempt timing with frame numbers, calibration snapshot, flags), `events.csv` (every event),
`poses.csv` (head, controllers, hand rest points, mugs per frame), `recall_checks.csv` (quiz answers),
`schedule.csv/json`, `session_metadata.json` (versions, layouts, counterbalancing, refresh rate),
`protocol_config.json`, `calibration_N.json`, `manifest.json` (status Completed / Aborted / Interrupted).

## Testing without a headset

* **Tests**: Window → General → Test Runner → EditMode → `XRStudy.Task1.Tests` (50 tests).
* **Debug mode**: select *Task 1 Study* → `TrialSimulator` → tick **Simulate On Start** (and pick its **Tasks** / **Conditions**) → Play. It fills in setup,
  calibrates in front of the camera, answers the quiz and does every trial with a virtual hand (90 % accuracy),
  writing real data files. Untick it before building for participants.
* `AgentScripts/Robustness.cs`: optional Pipeline script that tests tracking loss, pause and skip in Play Mode.

## On-headset test checklist (experimenter)

1. Build and Run to the Quest 3 (File → Build Profiles → Android → Run Device: Quest 3).
2. On launch: you see your **real room in colour**, with only the panel (and later the mugs and oval) virtual.
   No black background or skybox.
3. Setup: type a test ID (e.g. 901) with **controller ray** and again with **hand poke/pinch**. Every button reacts on
   the first press with a click sound.
4. Calibration: lay the right controller at the middle of the near edge. The mugs follow it. Pull the trigger.
   Check that the mugs stand **on** the table (±5 mm if needed), the oval sits at the near edge, and the mugs are in one straight
   line parallel to the edge (P1-N-NPC, or the rotated order shown on the setup screen).
5. Put the controllers down. Your hands should be tracked. Rest your right hand on the oval: it turns **green**, and
   the panel says "Hold still…".
6. Ownership intro: each mug lights up in turn. Answer the quiz by **touching** the mugs.
7. Practice: after 0.8–1.5 s a mug glows yellow. Touching it plays a click and shows "Correct!". Touching the wrong
   one shows "That was the … mug". Lift your hand before the cue: "Too early".
8. Hide your hand (behind your back) during a trial: the panel shows "We lost track of your hand", and the session
   resumes when the hand is visible again.
9. Open the experimenter panel (left menu button or left-hand menu pinch): test Pause → Resume, Skip trial, and
   Recalibrate (the study continues afterwards).
10. Block with the agent: the **virtual human** sits on a black chair across the table facing you, idles naturally
    and turns his head toward you as you move. Check that his hands/lap are hidden behind the real table edge and
    that eyes look forward with irises visible. **Nothing else changes**. Agent-absent block: no agent, same mugs.
11. Watch for stutter. If needed, check frame timing with OVR Metrics Tool (target ≥ 72 fps; 90 Hz requested).
12. After the end screen: `adb pull` the folder. Open the `P901_*.csv`: one row per attempt, training flagged,
    `response_time_s` filled for every Success/WrongTarget row, and a manifest status of `Completed`.
13. Restart the app: it should offer **Use saved** calibration, with the mugs in the same place on the table.

**Pick & place session** (on setup step 2 choose *Pick & place* or *Both tasks*):

13b. On setup step 2, try each plan; step 4 must list the matching run order (e.g. *Without Alex only* shows no
    "with Alex" block).
14. A white square (the pad) appears on the right of the table; the mugs stand on the real table surface.
15. Pointing a ray at a mug and pinching does nothing; grabbing it directly with the **right** hand works. The left
    hand (or left controller) can't pick a mug up.
16. Trial: wait for the glow, pick the glowing mug up, put it inside the square and let go. Practice says "Correct!"
    once it has stood still for a moment. Drop it outside the square: "Put the mug down inside the white square…".
17. After each trial all mugs jump back to their slots; while you still hold one the panel says "Please put the mug down".
18. CSV: `task = PickPlace`, and every Success row has release_time, confirmation_time, placement_duration_s and
    confirmation_latency_s.
