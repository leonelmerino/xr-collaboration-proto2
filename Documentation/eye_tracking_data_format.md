# Eye Tracking Data Logging – XR Jenga Experiment

## Overview

This document describes the structure, semantics, and storage of the eye tracking data collected in the XR Jenga experiment using:

* Unity (OpenXR)
* HTC Vive Focus Vision
* VIVE XR Eye Tracker

The dataset contains **raw eye tracking signals**, head pose, and gaze-based interaction with objects (AOIs). It is intended for offline analysis.

---

## Data Storage Location

All CSV files are stored at:

Application.persistentDataPath/EyeTrackingLogs/<participant_id>/<session_id>/

### Example

.../EyeTrackingLogs/P001/S001/
task_01_trial_01_001_gaze.csv
task_01_trial_01_002_gaze.csv

---

## File Naming Convention

Each recording generates a new file using an incremental index:

<task_id>*<trial_id>*<index>_gaze.csv

### Example

task_01_trial_01_001_gaze.csv

### Notes

* The index is automatically incremented per trial
* Files are never overwritten
* Multiple recordings can exist for the same task and trial

---

## Temporal Information

Each row corresponds to a single sample (frame).

The dataset includes:

* `sample_index`: sequential sample counter
* `timestamp_rel_s`: time in seconds since application start
* `timestamp_utc_iso`: absolute timestamp in ISO 8601 format

### Usage

* `timestamp_rel_s` should be used for temporal analysis
* `timestamp_utc_iso` enables synchronization with external systems

---

## CSV Schema

### 1. Metadata

| Field             | Description                            |
| ----------------- | -------------------------------------- |
| sample_index      | Sequential sample number               |
| timestamp_rel_s   | Time since application start (seconds) |
| timestamp_utc_iso | Absolute timestamp (ISO 8601)          |
| participant_id    | Participant identifier                 |
| session_id        | Session identifier                     |
| task_id           | Task identifier                        |
| trial_id          | Trial identifier                       |
| condition         | Experimental condition                 |

---

### 2. Combined Gaze

| Field                 | Description             |
| --------------------- | ----------------------- |
| combined_valid        | 1 if valid, 0 otherwise |
| combined_origin_x/y/z | Gaze ray origin         |
| combined_dir_x/y/z    | Gaze ray direction      |

---

### 3. Depth-Related Metrics

| Field                | Description                                  |
| -------------------- | -------------------------------------------- |
| vergence_angle_deg   | Angle between left and right gaze directions |
| interocular_distance | Distance between eye origins                 |

---

### 4. Per-Eye Gaze

#### Left Eye

| Field             | Description    |
| ----------------- | -------------- |
| left_valid        | Validity flag  |
| left_origin_x/y/z | Gaze origin    |
| left_dir_x/y/z    | Gaze direction |

#### Right Eye

| Field              | Description    |
| ------------------ | -------------- |
| right_valid        | Validity flag  |
| right_origin_x/y/z | Gaze origin    |
| right_dir_x/y/z    | Gaze direction |

---

### 5. Pupil Data

| Field                | Description               |
| -------------------- | ------------------------- |
| left_pupil_diameter  | Pupil diameter (mm)       |
| right_pupil_diameter | Pupil diameter (mm)       |
| left_pupil_pos_x/y   | Normalized pupil position |
| right_pupil_pos_x/y  | Normalized pupil position |

---

### 6. Eye Openness

| Field              | Description        |
| ------------------ | ------------------ |
| left_eye_openness  | Eye openness (0–1) |
| right_eye_openness | Eye openness (0–1) |

---

### 7. Head Pose

| Field            | Description                |
| ---------------- | -------------------------- |
| head_x/y/z       | Head position              |
| head_qx/qy/qz/qw | Head rotation (quaternion) |

---

### 8. Gaze-Based Interaction (AOI)

| Field           | Description                        |
| --------------- | ---------------------------------- |
| hit_valid       | 1 if gaze ray intersects an object |
| hit_object_name | Unity object name                  |
| hit_aoi         | AOI identifier                     |
| hit_aoi_type    | AOI category                       |
| hit_x/y/z       | Intersection point                 |

---

## AOI Definition (Jenga Blocks)

Each block in the scene is assigned an `AOITag` with the following format:

jenga_l<level>*<side>*<orientation>

### Example

jenga_l02_left_z

### Components

| Component   | Description                                     |
| ----------- | ----------------------------------------------- |
| level       | Vertical level of the tower                     |
| side        | Position within the level (left, center, right) |
| orientation | Block orientation (x or z axis)                 |

---

## Data Validity Rules

* Numeric fields are empty when data is invalid (not zero)
* Validity flags (`*_valid`) must be checked before using associated values
* Data may be partially available (e.g., only one eye valid)

---

## Sampling Characteristics

* Sampling frequency depends on:

  * headset tracking rate
  * Unity frame rate
* Sampling is not guaranteed to be uniform
* Use `timestamp_rel_s` for precise temporal analysis

---

## Tracking Limitations

Tracking data may be missing or invalid due to:

* eye blinks
* occlusions
* calibration issues
* hardware constraints

These cases are represented by:

* validity flags set to 0
* empty values in numeric fields
