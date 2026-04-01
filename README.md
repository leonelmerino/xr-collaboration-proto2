 # XR Collaboration Prototype



 ## Overview



This project implements a multi-user XR collaboration environment in Unity, including:



 * VR interaction using OpenXR

 * Hand tracking (pinch, poke, ray interactions)

 * Physics-based Jenga environment

 * Eye tracking integration (HTC Vive Focus Vision)

 * Gaze-based interaction logging (CSV for research)



The system is designed for research in immersive collaboration and human behavior analysis, and supports offline data collection pipelines.



 ---



 ## Requirements



 ### Hardware



 * HTC Vive Focus Vision

 * Wired Streaming Kit (USB-C)

 * VR-ready PC (recommended GPU: NVIDIA RTX series)



 ### Software



Install the following:



 * Unity Hub ( [https://unity.com/download](https://unity.com/download))

 * Unity Editor 2022.3 LTS

 * Visual Studio 2022 Community (with "Game development with Unity")

 * Git ( [https://git-scm.com](https://git-scm.com))

 * Steam

 * SteamVR

 * VIVE Streaming / VIVE Business Streaming



 ---



 ## Unity Installation



1 . Open Unity Hub

2 . Install Unity 2022.3 LTS

3 . Add module:



&#x20;   * Windows Build Support (IL2CPP)



No additional platforms are required.



 ---



 ## Clone the Repository



Clone the project into your working directory:



git clone  [https://github.com/leonelmerino/xr-collaboration-proto2.git](https://github.com/leonelmerino/xr-collaboration-proto2.git)



 ---



 ## Open the Project



1 . Open Unity Hub

2 . Click Open

3 . Select the project folder

4 . Ensure Unity version is 2022.3 LTS

5 . If prompted, select Rebuild Library

6 . Wait for compilation to finish



 ---



 ## Configure OpenXR



Go to:



Edit → Project Settings → XR Plug-in Management → PC



 * Enable OpenXR



Then in OpenXR settings:



 * Enable Khronos Simple Controller

 * Enable HTC Vive Controller (if available)



 ### Eye Tracking



Under OpenXR Features:



 * Enable VIVE XR Eye Tracker



 ---



 ## Set OpenXR Runtime



1 . Open SteamVR

2 . Go to Settings → Developer

3 . Click "Set SteamVR as OpenXR Runtime"



 ---



 ## Connect HTC Vive Focus Vision (Wired)



1 . Connect the headset via USB-C using the Wired Streaming Kit

2 . Open VIVE Streaming / VIVE Hub

3 . Start a streaming session

4 . Open SteamVR



Verify that the headset and controllers appear as ready (green).



 ---



 ## Run the Project



1 . Ensure SteamVR is running

2 . Press Play in Unity



The scene should appear in the headset.



 ---



 ## Eye Tracking Setup



Before running experiments:



1 . Put on the headset

2 . Open device settings

3 . Run eye tracking calibration



Eye tracking must be calibrated per user. Data may be invalid if calibration is not performed.



 ---



 ## Data Logging



The system automatically logs eye tracking data during runtime.



Data is stored at:



Application.persistentDataPath/EyeTrackingLogs/<participant>/<session>/



Each run generates a new CSV file (no overwrite).



The dataset includes:



 * Gaze (left, right, combined)

 * Head pose

 * Pupil data

 * AOI interactions (Jenga blocks)

 * Relative and absolute timestamps



 ---



 ## Project Structure



 * Assets/EyeTracking/ → eye tracking and logging

 * Assets/Scripts/ → interaction systems

 * Assets/Jenga/ → Jenga generator and AOIs

 * docs/ → documentation



 ---



 ## Expected Outcome



After setup, the system should:



 * Run the XR environment in the headset

 * Support hand interaction and physics

 * Capture eye tracking data

 * Generate structured CSV logs for analysis

