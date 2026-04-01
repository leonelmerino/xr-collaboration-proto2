# Eye Tracking Data Logging – XR Jenga Experiment

## Overview

This document describes the structure, semantics, and storage of the eye tracking data collected in the XR Jenga experiment using:

- Unity + OpenXR
- HTC Vive Focus Vision
- VIVE XR Eye Tracker

The system records **raw eye tracking data**, head pose, and gaze-based interaction with objects (AOIs), enabling full offline analysis.

---

## Data Storage Location

All CSV files are stored at:

```

Application.persistentDataPath/EyeTrackingLogs/<participant_id>/<session_id>/

```

### Example

```

.../EyeTrackingLogs/P001/S001/
task_01_trial_01_001_gaze.csv
task_01_trial_01_002_gaze.csv

```

---

## File Naming Convention

Each recording generates a new file using an incremental index:

```

<task_id>*<trial_id>*<index>_gaze.csv

```

### Example

```

task_01_trial_01_001_gaze.csv

```

### Rationale

- Prevents overwriting previous recordings
- Allows multiple trials per session
- Ensures reproducibility and traceability

---

## Temporal Information

Each row represents one sample (frame) and includes:

- `timestamp_rel_s`: seconds since application start (high precision)
- `timestamp_utc_iso`: absolute time in ISO 8601 format
- `sample_index`: sequential frame counter

### Why both timestamps?

- `timestamp_rel_s` → precise temporal analysis (latency, fixations)
- `timestamp_utc_iso` → synchronization across systems/logs

---

## CSV Schema

### 1. Metadata

| Field | Description |
|------|------------|
| sample_index | Sequential sample number |
| timestamp_rel_s | Time since app start (seconds) |
| timestamp_utc_iso | Absolute timestamp |
| participant_id | Participant identifier |
| session_id | Session identifier |
| task_id | Task identifier |
| trial_id | Trial identifier |
| condition | Experimental condition |

---

### 2. Combined Gaze (Primary Signal)

| Field | Description |
|------|------------|
| combined_valid | 1 if valid |
| combined_origin_* | Ray origin |
| combined_dir_* | Ray direction |

---

### 3. Depth-Related Metrics

| Field | Description |
|------|------------|
| vergence_angle_deg | Angle between both gaze rays |
| interocular_distance | Distance between eye origins |

---

### 4. Per-Eye Gaze

#### Left Eye

| Field | Description |
|------|------------|
| left_valid | Validity flag |
| left_origin_* | Origin |
| left_dir_* | Direction |

#### Right Eye

| Field | Description |
|------|------------|
| right_valid | Validity flag |
| right_origin_* | Origin |
| right_dir_* | Direction |

---

### 5. Pupil Data

| Field | Description |
|------|------------|
| left_pupil_diameter | Diameter (mm) |
| right_pupil_diameter | Diameter (mm) |
| left_pupil_pos_x/y | Normalized position |
| right_pupil_pos_x/y | Normalized position |

---

### 6. Eye Openness

| Field | Description |
|------|------------|
| left_eye_openness | Eye openness (0–1) |
| right_eye_openness | Eye openness (0–1) |

Used as a proxy for:
- blinks
- tracking reliability

---

### 7. Head Pose

| Field | Description |
|------|------------|
| head_x/y/z | Position |
| head_qx/qy/qz/qw | Rotation (quaternion) |

---

### 8. Gaze-Based Interaction (AOI)

| Field | Description |
|------|------------|
| hit_valid | 1 if raycast hit |
| hit_object_name | Unity object name |
| hit_aoi | AOI identifier |
| hit_aoi_type | AOI category |
| hit_x/y/z | Intersection point |

---

## AOI Definition (Jenga Blocks)

Each block is assigned an `AOITag`:

```

jenga_l<level>*<side>*<orientation>

```

### Example

```

jenga_l02_left_z

```

### Components

| Part | Meaning |
|------|--------|
| level | Tower level |
| side | left / center / right |
| orientation | x or z axis |

---

## Data Validity Rules

- Numeric values are **empty when invalid** (not zero)
- Validity flags (`*_valid`) must be checked before use
- Eye-specific data may be partially available

---

## Important Notes

### 1. Raw Data Philosophy

This system records **raw signals only**:

- No fixation detection
- No saccade classification
- No dwell time computation

All higher-level metrics should be computed offline.

---

### 2. Sampling Rate

Dependent on:

- headset tracking frequency
- Unity frame rate

Use `timestamp_rel_s` for accurate timing.

---

### 3. Tracking Loss

Possible causes:

- blinking
- occlusion
- hardware limitations

Handled via:
- validity flags
- missing (empty) values

---

## Recommended Offline Analysis

From this dataset you can compute:

### Gaze Metrics
- fixations (I-VT / I-DT)
- saccades
- scanpaths

### AOI Metrics
- dwell time per block
- transition matrices
- visual strategies

### Depth Analysis
- vergence-based distance
- near vs far attention

### Pupil Analysis
- cognitive load (relative changes)
- task difficulty

---

## Optional Extensions (Future Work)

Consider adding:

- interaction events (grab, pinch)
- controller data
- physiological signals (EDA, HRV)
- scene state snapshots

---

## Summary

This logging system provides:

- high-fidelity raw eye tracking data
- spatial context (AOIs)
- temporal precision
- reproducible file structure

It is designed for:

- XR research
- behavioral analysis
- human-computer interaction studies
```