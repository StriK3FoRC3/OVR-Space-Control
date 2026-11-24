# OVR Space Control

Keep the extracted app folder writable. Settings, custom patterns, and any play-space recovery file live beside the executable and travel with that folder. The optional driver is registered separately with SteamVR.

OVR Space Control is a Windows utility for SteamVR play-space movement. It includes Space Drag, 3D Space Drag, directional flight, and teleport modes with recordable custom patterns.

## Run

1. Start SteamVR with your headset and controllers connected.
2. Download the portable Windows x64 ZIP from **Releases**, extract it, and open `OVR Space Control.exe` from the extracted folder. It requires the x64 .NET 10 Desktop Runtime.
3. To teleport, select **Teleport** and close the SteamVR dashboard. Hold **R3** (right stick click) and point at the floor.
4. Release when the purple laser points to a purple ring. Red indicates an invalid aim. A red ring marks a spot too close to your feet, and a red beam without a ring means there is no floor hit.

Use **Controller bindings** to change the controls in SteamVR. Teleports have no app-imposed distance limit. The laser displays the first 30 m of the aim line and the ring marks the actual landing point. Press **L3** (left stick click) or use **Reset play space** to reset. Closing the utility also clears the temporary offset. Jumps use a SteamVR preview and leave the saved room calibration intact.

With **Space Turn** enabled in Teleport mode, the right stick turns your play space around your headset after a jump. **Turn multiplier** adjusts the speed from 0.25× to 10×. Space Turn starts enabled at 1×, which turns at 180° per second at full stick deflection. Hold and release **R3** to teleport again. Click **L3** to reset and return the stick to the game, or switch **Space Turn** off to give back the stick at your current position. SteamVR uses **Experimental overlay input overrides** in its Developer settings to give the overlay priority.

Use **Open in VR** on the desktop or select **OVR Space Control** from the SteamVR dashboard while the app is running. Both surfaces share the teleport toggle, mode, reset, and circle settings. Close the dashboard before aiming a jump.

## Circle pattern

Select **Circle Teleport** in the sidebar on desktop or in VR. Set the radius, point count, and interval with the **−** and **+** buttons. Hold a button to adjust repeatedly. The interval starts at 10 ms and adjusts in 10 ms steps. The defaults are 3 m, ten points, and 200 ms. Switch **Point order** to **Random** to shuffle the evenly spaced points each lap.

Hold **R3** and aim the laser at the desired center. The purple circumference shows the set radius and the green rings mark the landing points. Release to make the first jump. The circle repeats until you press **R3** again, which stops at the current point. **L3** stops the circle and restores the original play space. The view turns toward the center at each jump, with height preserved. Switching modes, opening the dashboard, or disabling teleport also stops a running circle. Circle settings apply when starting a new circle.

## Sweep Teleport

Select **Sweep Teleport** and adjust the width, number of rows, points per row, row gap, and interval between jumps. **Row gap (m)** sets the distance between adjacent rows or their height difference in Vertical mode. The gap defaults to 1.5 m and adjusts in 0.25 m steps. The default interval is 200 ms. Hold **R3** and aim at the center. The floor preview shows cyan rows, blue row transitions, and green landing points. The filled green point marks the start. Release **R3** to start. Sequential playback starts on the row closest to you, alternates direction each row, and repeats until you press **R3** to stop. Choose **Random** to visit the same points in shuffled order. Set **Row direction** to **Vertical** for rows stacked at different heights. Aim at the floor below the stack. The bottom row keeps your current height and each higher row raises it. **Vertical + Horizontal** stacks horizontal sweeps. **Rows** sets both the rows per layer and the number of layers, starting at one. **Row gap (m)** also sets the distance between layers.

Press **L3** while holding **R3** to cancel the current action. Release **R3** before aiming again. Press **L3** on its own to reset the play space outside custom recording.

## Space Drag

Select **Space Drag** in the sidebar. Hold **R3** and pull with the controller to move your play space horizontally or vertically. Release to stay at the new position. Press **L3** to reset. **Move multiplier** adjusts how far a pull moves you. **Acceleration** adds more movement to faster controller pulls. With it on, **Acceleration strength** scales that extra movement in 0.25x steps, starting at 0.25x. The default 1x matches the original acceleration.

The X, Y and Z values beside the title show the app's live play-space offset from the starting origin, in meters.

## 3D Space Drag

For pitch and roll, choose **Settings > Install VR driver**, then restart SteamVR. The bundled Windows x64 driver is independent of Tracker Smoothing. Choose **Remove VR driver** in the same menu and restart SteamVR to unload it.

Select **3D Space Drag** below **Space Drag**. Hold **R3** and rotate the controller to turn, tilt or roll the view 1:1 around the headset. Release **R3** to keep the orientation. Press **L3** during the hold to undo that turn, or press **L3** on its own to reset the play space.

## Custom patterns

Open **Custom Pattern** in the sidebar. This feature is a work in progress and may not work as expected. Center your play space before recording. The creator shows your live headset X/Y/Z in meters and facing (yaw) in degrees, including physical movement. Select **Pattern name** and type with your physical keyboard, or leave it blank for an automatic name such as **Pattern 1**. Choose **Record** to begin. Close the dashboard and press **L3** at each position to capture headset position and horizontal facing. Enable **Space Drag** and hold **R3** to reposition between captures. **Lock Space Drag Height** keeps dragging horizontal. **Save rotation** and **Save height** default to off. Turn either off before finishing to keep your current facing or height during playback. The recorder remembers both choices.

Use **Undo last point** to remove the most recent capture. You can edit the name on the same page while recording. Choose **Finish recording** to save. The saved pattern appears in the left sidebar. Older numbered slots appear as **Pattern N** and retain their original playback behavior.

Select a saved pattern to adjust its interval, rename it, or play it. Choose **Rename**, edit the name with your keyboard in the desktop app, then choose **Save** or press **Enter**. **R3** starts or stops playback. **L3** stops playback and resets the play space. The trash icon opens a confirmation before deletion.

The ring is based on the tracking floor, so it can appear above or below a game's floor or beyond an in-game wall. Game support depends on whether the game follows SteamVR's standing tracking origin. Use one play-space mover at a time. The utility offsets the SteamVR collision bounds with the origin to preserve their physical placement.

The app connects automatically when SteamVR is available and retries after a disconnect. Changes on desktop or in the SteamVR dashboard automatically save your selected mode, on/off state, Circle Teleport settings, Space Drag settings, Space Turn settings, and the custom editor height lock. Use **Show play space offset** in **Settings** to show or hide the X, Y and Z movement from the starting origin. Select a saved custom pattern in the sidebar to change its interval, repeat setting, and point order. A new pattern repeats sequentially every 500 ms. Random order visits every point once per pass. With repeat off, playback stops at the last point. Use **Save** to store the points of a custom pattern.

## Fly and rotation pivots

Select **Flight Mode** and hold R3 to fly. Point left or right to turn, and up or down to climb or descend. Turn on **Direct flight** to fly wherever you point. Use the headset, left controller, or right controller to steer. Set **Top speed** to **Off** to fly without a speed cap. Turn on **Idle hover bobbing** to hover gently while you coast after releasing R3.

In **Settings**, select a controller, headset, or connected tracker as the rotation pivot. The custom pattern creator cycles between Space Drag, 3D Space Drag, and drag off. Install the optional driver to use pitch and roll while recording with 3D Space Drag.

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

## Reusable SteamVR dashboard rendering fix

In OVR Space Control, the VR dashboard flashed during value changes and sometimes showed the previous click's value until the next click. The combination below was confirmed working in-headset. Earlier attempts that changed texture submission or click handling alone did not resolve the reported behavior.

1. **Redraw when the pointer moves.** Read SteamVR mouse-move events along with button events. Draw the controller cursor and hover highlight into the VR frame, using the same button bounds for hover and hit testing. Clear the cursor when focus leaves or the overlay hides. Coalesce movement and input into one redraw per timer tick.
2. **Alternate two shared Direct3D 11 textures.** Use the adapter reported by SteamVR and create two persistent shared `DXGI_FORMAT_B8G8R8A8_UNORM` textures. Write the complete BGRA frame to the back texture with `UpdateSubresource`. End and flush a `D3D11_QUERY_EVENT`, then wait for completion before submitting that texture with `IVROverlay::SetOverlayTexture`. Alternate textures for subsequent frames so the currently displayed texture stays available while the next frame is written.
3. **Submit changed frames only.** Compare the composed pixels to the last successful submission and skip identical frames. Keep `SetOverlayRaw` as a compatibility fallback when shared-texture setup or submission fails.

Reuse [DashboardTexture.cs](DashboardTexture.cs) for texture allocation and synchronization, [OpenVr.cs](OpenVr.cs) for SteamVR events and submission, [InterfaceStyle.cs](InterfaceStyle.cs) for cursor and hover drawing, and [Program.cs](Program.cs) for the redraw loop.

When testing a new app, check in-headset that moving the pointer updates hover feedback, a single **+** click updates the displayed number with the pointer held still, the next **−** click updates it again, and holding a stepper does not flash the panel. Repeat after closing and reopening the dashboard.

https://github.com/ValveSoftware/openvr/wiki/IVROverlay::SetOverlayTexture

https://learn.microsoft.com/en-us/windows/win32/api/d3d11/ne-d3d11-d3d11_query

## References

https://github.com/ValveSoftware/openvr/blob/master/headers/openvr_capi.h

https://github.com/ValveSoftware/openvr/wiki/SteamVR-Input

https://github.com/OpenVR-Advanced-Settings/OpenVR-AdvancedSettings/blob/master/Readme.md

In **Settings**, enable **Start with SteamVR** while connected to register automatic dashboard startup. **Close to tray** and **Minimize to tray** independently keep OVR Space Control running in the notification area. Double-click its tray icon to reopen the window or right-click and choose **Quit** to exit. The desktop window remembers its position between launches.

**Start minimized** opens to the taskbar, or to the notification area when **Minimize to tray** is on. Once SteamVR has started, OVR Space Control quits when SteamVR exits, including while hidden.
