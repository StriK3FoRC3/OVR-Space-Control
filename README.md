(Showcase Video soon)
# OVR Space Controll

This project started with a simple idea: creating a playspace teleporter that I could use in VRChat. However, it quickly grew into something much bigger!

Here are the features currently available:

> **Note:** Full-Body Tracking (FBT) is required for most features.<br>
> Use GOGO LOCOs Lock Position Feature to go through Walls with your body

---

### Space Drag

Offers a wide range of customization options, more than most other apps.

- Drag Multiplier
- Acceleration Strength
- Lock Height
- Gravity
- Movement Resistance (set to below `100%` to keep moving after releasing the drag key)
- Enable Space Rotation
- Floor Bounce
- Floor Offset
- Ceiling Bounce
- Ceiling Height
- Place Walls (to bounce off)
- Show Boundaries (displays walls, ceiling, and floor)

### 3D Space Drag

In my opinion, this is one of the most fun features!
It allows you to move and rotate freely in 3D space. The rotation anchor is your controller, making it possible to float around as if you were in space with zero gravity.
This feature requires the optional SteamVR driver, which can be installed from the Settings tab.<br>

- Drag Multiplier
- Acceleration Strength
- Gravity
- Movement Resistance (set to below `100%` to keep moving after releasing the drag key)
- Rotation Resistance
- Floor Bounce
- Floor Offset
- Ceiling Bounce
- Ceiling Height
- Place Walls (to bounce off)
- Show Boundaries (displays walls, ceiling, and floor)


### Flight Mode<br>
Lets you Fly around in two movement types.<br>

- Starting Speed
- Top speed
- Acceleration
- Gravity
- Movement resistance
- Rotation smoothing
- Fly direction (Right/left Controller or Headset)
- Direct flight
- Controller pitch offset
- Controller Yaw offset
- idle hover bobbing
- bobbing intensity<br>
  

### Teleport

Allows you to teleport to a selected location using a visible pointer.
Space Turn is enabled by default. This ensures that when teleporting over long distances, turning your body won't cause you to be reset outside of the world's boundaries.<br>
- Space Turn multiplier
- Spaceturn on/off
- Movement type teleport/smooth
    - Travel time

### Circle Teleport

Automatically teleports you around a circular path while always keeping you facing the center.
Available settings:

- Radius
- Teleport Points
- Teleport Interval
- Point Order (Sequential or Random)
- Movement type teleport/smooth
    - Travel time

### Sweep Teleport

Allows you to teleport across an area in a line or grid pattern. You can sweep horizontally, vertically, or in both directions.
Available settings:

- Pattern Width
- Pattern Rows
- Teleport Points per Row
- Row Gap
- Teleport Interval
- Point Order (Sequential or Random)
- Row Direction
- Movement type teleport/smooth
    - Travel time

### Custom Pattern
Create your own custom teleportation patterns to suit your needs. <br>
!!!**Currently a work in progress and may not work as expected.**!!!


### Bugs
if you have any issues let me know and i will try to fix them, i was only able to test with my quest pro over Virtual desktop and steam link, i use vive ultimate trackers but other trackers should also work.
i do not know if other headsets will have issues with 3D Space Drag

# Confirmed Working 

### Headsets:
**Quest Pro** with
- Virtual Desktop
- Steam link<br>

### Trackers:
**Vive Ultimate**

<br>
<br>

## Run

1. Start SteamVR with your headset and controllers connected.
2. Open `Build/OVR Space Control.exe` and keep its window open. The packaged version includes a `Build` folder. It requires the x64 .NET 10 Desktop Runtime.
3. Close the SteamVR dashboard. Hold **R3** (right stick click) and point at the floor.
4. Release when the purple laser points to a purple ring. Red indicates an invalid aim. A red ring marks a spot too close to your feet, and a red beam without a ring means there is no floor hit.


The app connects automatically when SteamVR is available and retries after a disconnect. Changes on desktop or in the SteamVR dashboard automatically save your selected mode, on/off state, Circle Teleport settings, Space Drag settings, Space Turn settings, and the custom editor height lock. Use **Show play space offset** in **Settings** to show or hide the X, Y and Z movement from the starting origin. Select a saved custom pattern in the sidebar to change its interval, repeat setting, and point order. A new pattern repeats sequentially every 500 ms. Random order visits every point once per pass. With repeat off, playback stops at the last point. Use **Save** to store the points of a custom pattern.

## Building the optional driver

From an x64 Visual Studio Developer Command Prompt with CMake and Ninja available, run these commands in the project root. The driver dependencies and their licenses are in NativeDriver/vendor.

~~~bat
cmake -S NativeDriver -B "%TEMP%\OVRSpaceControlNative" -G Ninja -DCMAKE_BUILD_TYPE=Release
cmake --build "%TEMP%\OVRSpaceControlNative"
"%TEMP%\OVRSpaceControlNative\pose_tests.exe"
if not exist "SteamVR\driver\bin\win64" mkdir "SteamVR\driver\bin\win64"
copy /Y "%TEMP%\OVRSpaceControlNative\driver_OVRSpaceControl.dll" "SteamVR\driver\bin\win64\"
~~~

## Build and check

With the .NET 10 SDK installed, run these commands from the source directory.

```powershell
dotnet publish .\OVRSpaceControl.csproj -c Release --no-self-contained -o .\Build
$exe = (Resolve-Path '.\Build\OVR Space Control.exe').Path
$report = Join-Path $env:TEMP 'space-teleporter-test.txt'
Start-Process $exe -ArgumentList @('--self-test', ('"' + $report + '"')) -Wait
Get-Content -LiteralPath $report
```


The self-test checks the floor intersection, movement direction, physical boundary coordinates, recovery data, button state transitions, and dashboard control hit areas. Use `--preview-ui <output-folder>` to render the desktop and dashboard previews without connecting to SteamVR. Run `--probe <output-file>` from your normal Windows desktop session to check SteamVR, action setup, and calibration without teleporting. Close OVR Space Control before running the probe.


https://github.com/ValveSoftware/openvr/wiki/IVROverlay::SetOverlayTexture

https://learn.microsoft.com/en-us/windows/win32/api/d3d11/ne-d3d11-d3d11_query

## References

https://github.com/ValveSoftware/openvr/blob/master/headers/openvr_capi.h

https://github.com/ValveSoftware/openvr/wiki/SteamVR-Input

https://github.com/OpenVR-Advanced-Settings/OpenVR-AdvancedSettings/blob/master/Readme.md

In **Settings**, enable **Start with SteamVR** while connected to register automatic dashboard startup. **Close to tray** and **Minimize to tray** independently keep OVR Space Control running in the notification area. Double-click its tray icon to reopen the window or right-click and choose **Quit** to exit. The desktop window remembers its position between launches.

**Start minimized** opens to the taskbar, or to the notification area when **Minimize to tray** is on. Once SteamVR has started, OVR Space Control quits when SteamVR exits, including while hidden.
