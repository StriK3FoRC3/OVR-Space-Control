using System.Numerics;
using System.Runtime.InteropServices;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args is ["--self-test", var testOutput])
            return RunDiagnostic(testOutput, Test);
        if (args is ["--preview-ui", var folder])
        {
            ApplicationConfiguration.Initialize();
            Directory.CreateDirectory(folder);
            using var window = new MainWindow(connectOnShow: false);
            window.SelectMode(TeleportMode.Circle);
            window.Show();
            Application.DoEvents();
            using var desktop = new Bitmap(window.Width, window.Height);
            window.DrawToBitmap(desktop, new Rectangle(Point.Empty, desktop.Size));
            desktop.Save(Path.Combine(folder, "desktop.png"));
            window.SavePanelPreview(Path.Combine(folder, "dashboard.png"));
            window.SelectMode(TeleportMode.Sweep);
            window.SavePanelPreview(Path.Combine(folder, "sweep.png"));
            window.SelectMode(TeleportMode.SpaceDrag);
            window.SavePanelPreview(Path.Combine(folder, "space-drag.png"));
            window.SelectMode(TeleportMode.Normal);
            window.SavePanelPreview(Path.Combine(folder, "normal.png"));
            window.SelectMode(TeleportMode.Custom);
            window.SavePanelPreview(Path.Combine(folder, "custom.png"));
            using var settingsPreview = new DashboardPanel();
            settingsPreview.Render(new PanelState(true, TeleportMode.Normal, CircleControls.Default, "", false, SettingsOpen: true));
            settingsPreview.SavePreview(Path.Combine(folder, "settings.png"));
            using var guide = new Bitmap(CircleGuide.Size, CircleGuide.Size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            byte[] pixels = CircleGuide.MakeTexture(CircleControls.Default.Points);
            for (int i = 0; i < pixels.Length; i += 4) (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
            var bounds = new Rectangle(Point.Empty, guide.Size);
            var data = guide.LockBits(bounds, System.Drawing.Imaging.ImageLockMode.WriteOnly, guide.PixelFormat);
            try { Marshal.Copy(pixels, 0, data.Scan0, pixels.Length); }
            finally { guide.UnlockBits(data); }
            guide.Save(Path.Combine(folder, "circle-guide.png"));
            return 0;
        }
        if (args is ["--probe", var probeOutput])
            return RunDiagnostic(probeOutput, () =>
            {
                using var vr = new OpenVr();
                var calibration = vr.ReadCalibration();
                var (action, reset, _, _) = vr.ReadControls();
                var (head, _) = vr.Poses(action.Origin);
                return $"SteamVR connected. Calibration {vr.Calibration}, {calibration.Corners.Length / 4} boundary quads, " +
                    $"headset connected {head.Connected != 0}, headset tracking {head.Tracked}, " +
                    $"teleport action active {action.Active != 0}, reset action active {reset.Active != 0}.";
            });

        using var instance = new Mutex(true, "Local\\StriK3.OVRSpaceControl", out bool first);
        if (!first) return 0;
        ApplicationConfiguration.Initialize();
        Application.Run(new MainWindow());
        return 0;
    }

    private static int RunDiagnostic(string output, Func<string> run)
    {
        try
        {
            File.WriteAllText(output, run());
            return 0;
        }
        catch (Exception exception)
        {
            File.WriteAllText(output, exception.ToString());
            return 1;
        }
    }

    private static string Test()
    {
        static void Check(bool condition, string test)
        {
            if (!condition) throw new InvalidOperationException(test);
        }

        var head = new Matrix34(1, 0, 0, 1, 0, 1, 0, 1.7f, 0, 0, 1, 0);
        var controller = new Matrix34(1, 0, 0, 1, 0, 0, 1, 1, 0, -1, 1, 0);
        var aim = PlaySpace.Aim(head, controller);
        var target = aim?.FloorPoint;
        Check(target == new Vector3(1, 0, -1), "Floor ray intersection");
        Check(aim?.CanTeleport == true && aim.Value.Laser(head.Position) is { Length: > 1 },
            "A valid floor hit draws a laser to the target");
        var horizontal = PlaySpace.Aim(head, head);
        Check(horizontal is { CanTeleport: false, FloorPoint: null } && horizontal.Value.Laser(head.Position) is not null,
            "Horizontal aim still draws an invalid laser");
        var shallow = PlaySpace.Aim(head, controller with { YZ = .001f });
        Check(shallow is { CanTeleport: true, FloorPoint.Z: < -900 } &&
            shallow.Value.Laser(head.Position) is { Length: <= 30f }, "Teleport distance is independent of laser display length");
        var close = PlaySpace.Aim(head, controller with { ZZ = 0 });
        Check(close is { CanTeleport: false, FloorPoint: not null }, "Too-close targets retain a red marker");
        Check(PlaySpace.Movement(head, target!.Value) == new Vector3(0, 0, -1), "Head-relative movement");
        Check(PlaySpace.ReleaseMovement(head, target.Value) == new Vector3(0, 0, -1),
            "Release uses the displayed destination without a new controller pose");
        Check(PlaySpace.ReleaseMovement(head, new Vector3(1, 0, -5000)) == new Vector3(0, 0, -5000),
            "Release has no distance cap");

        var snapshot = new CalibrationSnapshot(
            new Matrix34(0, 0, 1, 4, 0, 1, 0, 0, -1, 0, 0, 2),
            [new Vector3(1, 0, -1)]);
        var shifted = snapshot.Shift(new Vector3(3, 0, -2));
        Vector3 oldCorner = snapshot.Origin.Position + snapshot.Origin.Rotate(snapshot.Corners[0]);
        Vector3 newCorner = shifted.Origin.Position + shifted.Origin.Rotate(shifted.Corners[0]);
        Check(oldCorner == newCorner, "Physical boundary placement after rotated-room shift");
        Check(shifted.Shift(new Vector3(-3, 0, 2)).Matches(snapshot), "Reversible translation");
        Check(snapshot.Shift(new Vector3(3, 50, -2)).Matches(shifted), "Ignore any jump height");
        var json = new System.Text.Json.JsonSerializerOptions { IncludeFields = true, IgnoreReadOnlyProperties = true };
        string serialized = System.Text.Json.JsonSerializer.Serialize(shifted, json);
        var recovered = System.Text.Json.JsonSerializer.Deserialize<CalibrationSnapshot>(serialized, json);
        Check(recovered is not null && recovered.Matches(shifted), "Recovery data round trip");

        var gesture = new AimGesture();
        var press = new DigitalAction { Active = 1, Pressed = 1, Origin = 5 };
        Check(press.Held && !press.JustPressed && (press with { Changed = 1 }).JustPressed,
            "Reset fires once per click");
        var release = new DigitalAction { Active = 1 };
        Check(gesture.Update(press).Origin == 0, "Ignore a button already held at startup");
        gesture.Update(release);
        Check(gesture.Update(press) == (5UL, false), "Start aiming on press");
        Check(gesture.Update(release) == (5UL, true), "Teleport once on release");
        Check(gesture.Update(release).Origin == 0, "No repeated teleport");
        gesture.Update(press);
        gesture.Update(default);
        Check(gesture.Update(release).Origin == 0, "Cancel on lost input");
        gesture.Update(press);
        Check(gesture.InProgress, "A held R3 gesture can be cancelled");
        gesture.Cancel();
        Check(gesture.Update(press).Origin == 0 && gesture.Update(release).Origin == 0 &&
            gesture.Update(press).Origin == 5, "A cancelled R3 hold stays cancelled until released");
        Check(Marshal.SizeOf<Matrix34>() == 48 && Marshal.SizeOf<TrackedPose>() == 80,
            "Native OpenVR pose layout");
        var panel = new PanelState(true, TeleportMode.Circle, CircleControls.Default, "", true);
        Check(DashboardPanel.HitTest(new PointF(760, 160), panel) == DashboardAction.Toggle &&
            DashboardPanel.HitTest(new PointF(950, 160), panel) == DashboardAction.Toggle &&
            DashboardPanel.HitTest(new PointF(700, 160), panel with { Mode = TeleportMode.Normal }) == DashboardAction.None &&
            DashboardPanel.HitTest(new PointF(760, 222), panel) == DashboardAction.Reset &&
            DashboardPanel.HitTest(new PointF(290, 420), panel) == DashboardAction.RadiusDown &&
            DashboardPanel.HitTest(new PointF(690, 420), panel) == DashboardAction.PointsUp &&
            DashboardPanel.HitTest(new PointF(920, 420), panel) == DashboardAction.IntervalUp &&
            DashboardPanel.HitTest(new PointF(350, 600), panel) == DashboardAction.Order &&
            DashboardPanel.HitTest(new PointF(90, 170), panel) == DashboardAction.SpaceDrag &&
            DashboardPanel.HitTest(new PointF(90, 230), panel) == DashboardAction.ControllerTurn &&
            DashboardPanel.HitTest(new PointF(90, 290), panel) == DashboardAction.Fly &&
            DashboardPanel.HitTest(new PointF(90, 410), panel) == DashboardAction.Circle &&
            DashboardPanel.HitTest(new PointF(90, 470), panel) == DashboardAction.Sweep &&
            DashboardPanel.HitTest(new PointF(290, 420), panel with { Mode = TeleportMode.Normal }) == DashboardAction.None,
            "Shared controls map to the displayed hit areas");
        var flyPanel = panel with { Mode = TeleportMode.Fly };
        Check(DashboardPanel.HitTest(new Point(310, 380), flyPanel) == DashboardAction.FlySpeedDown &&
            DashboardPanel.HitTest(new Point(350, 570), flyPanel) == DashboardAction.FlyDirection &&
            DashboardPanel.HitTest(new Point(305, 478), flyPanel) == DashboardAction.GravityDown &&
            DashboardPanel.HitTest(new Point(540, 380), flyPanel) == DashboardAction.FlyTopSpeedDown &&
            DashboardPanel.HitTest(new Point(820, 478), flyPanel) == DashboardAction.None &&
            DashboardPanel.HitTest(new Point(540, 380), flyPanel with { FlyTopSpeed = 0 }) == DashboardAction.FlyTopSpeedDown &&
            DashboardPanel.HitTest(new Point(775, 380), flyPanel) == DashboardAction.FlyAccelerationDown &&
            DashboardPanel.HitTest(new Point(935, 380), flyPanel) == DashboardAction.FlyAccelerationUp &&
            DashboardPanel.HitTest(new Point(600, 570), flyPanel) == DashboardAction.DirectFlight &&
            DashboardPanel.HitTest(new Point(305, 651), flyPanel) == DashboardAction.None &&
            DashboardPanel.HitTest(new Point(305, 651), flyPanel with { FlyDirection = DragPivot.RightController }) == DashboardAction.FlyPitchDown &&
            DashboardPanel.HitTest(new Point(545, 651), flyPanel with { FlyDirection = DragPivot.LeftController }) == DashboardAction.FlyYawDown &&
            DashboardPanel.HitTest(new Point(775, 478), flyPanel) == DashboardAction.FlySmoothingDown &&
            DashboardPanel.HitTest(new Point(820, 570), flyPanel) == DashboardAction.IdleHoverBobbing &&
            DashboardPanel.HitTest(new Point(775, 651), flyPanel) == DashboardAction.None &&
            DashboardPanel.HitTest(new Point(775, 651), flyPanel with { IdleHoverBobbing = true }) == DashboardAction.HoverIntensityDown,
            "Fly controls match their displayed positions");
        Check(FlightSteering.HoverOffset(0, 4) == 0 && Math.Abs(FlightSteering.HoverOffset(2, 4) - .04f) < .0001f &&
            Math.Abs(FlightSteering.HoverOffset(2, 12) - .12f) < .0001f && Math.Abs(FlightSteering.HoverOffset(4, 12)) < .0001f, "Hover bobbing returns to its starting altitude without drift");
        var forward = -Vector3.UnitZ;
        Check(Vector3.Distance(FlightSteering.ControllerDirection(Matrix34.FromBasis(Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, default), 90, 45), Vector3.UnitY) < .001f &&
            Vector3.Distance(FlightSteering.ControllerDirection(Matrix34.FromBasis(Vector3.UnitZ, Vector3.UnitY, -Vector3.UnitX, default), 0, -90), -Vector3.UnitZ) < .001f,
            "Flight offsets use controller-local pitch and yaw axes");
        float leftRate = FlightSteering.PlaneTurnRate(forward, Vector3.Normalize(forward - Vector3.UnitX));
        float rightRate = FlightSteering.PlaneTurnRate(forward, Vector3.Normalize(forward + Vector3.UnitX));
        Check(FlightSteering.AdjustTopSpeed(2, 2, false) == 0 && FlightSteering.AdjustTopSpeed(0, 2, true) == 2,
            "Top speed can be disabled and enabled with the same control");
        var flightOrigin = Matrix34.FromBasis(Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, default);
        var plane = new CalibrationSnapshot(flightOrigin, []);
        Vector3 firstLeg = plane.Origin.Unrotate(forward);
        for (int tick = 0; tick < 50; tick++)
            plane = plane.TurnAroundHead(Vector3.Zero, leftRate * .02f);
        Check(leftRate > 0 && rightRate == -leftRate &&
            FlightSteering.PlaneTurnRate(forward, forward) == 0 &&
            FlightSteering.PlaneTurnRate(forward, Vector3.UnitY) == 0 &&
            Vector3.Distance(firstLeg, forward) < .001f &&
            Vector3.Distance(plane.Origin.Unrotate(forward), -Vector3.UnitX) < .001f,
            "Holding left continues to turn the flight path while neutral and vertical aim do not steer");
        var halfwayUp = FlightSteering.SmoothDirection(forward, Vector3.UnitY, .5f);
        var halfwayDown = FlightSteering.SmoothDirection(forward, -Vector3.UnitY, .5f);
        var reversed = FlightSteering.SmoothDirection(forward, -forward, .5f);
        float halfStep = FlightSteering.Blend(35, .01f);
        float fullStep = FlightSteering.Blend(35, .02f);
        var firstStep = FlightSteering.SmoothDirection(forward, Vector3.UnitY, halfStep);
        Check(Vector3.Distance(halfwayUp, Vector3.Normalize(forward + Vector3.UnitY)) < .001f &&
            Vector3.Distance(halfwayDown, Vector3.Normalize(forward - Vector3.UnitY)) < .001f &&
            Math.Abs(reversed.Length() - 1) < .001f && Math.Abs(Vector3.Dot(reversed, forward)) < .001f &&
            Vector3.Distance(FlightSteering.SmoothDirection(firstStep, Vector3.UnitY, halfStep),
                FlightSteering.SmoothDirection(forward, Vector3.UnitY, fullStep)) < .001f &&
            FlightSteering.SmoothDirection(forward, Vector3.UnitY, FlightSteering.Blend(0, 0)) == Vector3.UnitY,
            "Flight smoothing handles pitch, reversal, frame timing and zero smoothing");
        var customPanel = panel with
        {
            Mode = TeleportMode.Custom,
            Custom = new CustomEditorState(false, false, false, 0, 1000, Names: ["Test pattern"], Selected: -1)
        };
        Check(DashboardPanel.HitTest(new PointF(850, 58), panel) == DashboardAction.Custom &&
            DashboardPanel.HitTest(new PointF(850, 58), customPanel) == DashboardAction.Custom &&
            DashboardPanel.HitTest(new PointF(102, 530), customPanel) == DashboardAction.PatternRow &&
            DashboardPanel.HitTest(new PointF(810, 160), customPanel) == DashboardAction.None,
            "Custom navigation and saved pattern buttons match their displayed positions");
        Check(DashboardPanel.HitTest(new Point(600, 315), customPanel) == DashboardAction.EditName &&
            DashboardPanel.HitTest(new Point(370, 380), customPanel with { Custom = customPanel.Custom with { Naming = true } }) == DashboardAction.CustomRecord,
            "The recording page supports naming before capture and recording while the name field is focused");
        var preferencesPanel = panel with { SettingsOpen = true };
        Check(DashboardPanel.HitTest(new Point(900, 792), panel) == DashboardAction.Settings &&
            DashboardPanel.HitTest(new Point(910, 320), preferencesPanel) == DashboardAction.Coordinates &&
            DashboardPanel.HitTest(new Point(910, 388), preferencesPanel) == DashboardAction.FlightDirectionLine &&
            DashboardPanel.HitTest(new Point(435, 420), preferencesPanel) == DashboardAction.None &&
            DashboardPanel.HitTest(new Point(830, 160), preferencesPanel) == DashboardAction.DriverInstall &&
            DashboardPanel.HitTest(new Point(760, 470), customPanel with
                { Custom = customPanel.Custom with { Recording = true, PointCount = 1 } }) == DashboardAction.UndoPoint,
            "Settings and recording controls share the same visible hit targets");
        using (var preview = new DashboardPanel())
        {
            preview.Render(panel);
            var cursor = new Point(455, 418);
            var hit = DashboardPanel.HitTarget(cursor, panel);
            Check(hit.Action == DashboardAction.RadiusUp && hit.Bounds.Contains(cursor),
                "VR hover uses the same button bounds as click hit testing");
            var baseline = preview.CopyBgra(default).ToArray();
            byte[] hovered = preview.CopyBgra(new DashboardPointerFrame(cursor, hit.Bounds, 220));
            int pixel = (cursor.Y * DashboardPanel.Width + cursor.X) * 4;
            Check(hovered[pixel] == 255 && hovered[pixel + 1] == 255 &&
                !hovered.AsSpan().SequenceEqual(baseline) && preview.CopyBgra(default).AsSpan().SequenceEqual(baseline),
                "VR hover changes only the dashboard upload and clears without modifying the desktop image");
        }
        Check((shifted with { SeatedOrigin = snapshot.Origin }).Shift(new Vector3(3, 0, -2)).SeatedOrigin == shifted.Origin,
            "Seated and standing receive the same raw translation");
        var start = snapshot with { SeatedOrigin = head };
        var submitted = new List<CalibrationSnapshot>();
        int reads = 0, hides = 0;
        var playSpace = new PlaySpace(() => { reads++; return start; }, submitted.Add, () => hides++,
            _ => throw new InvalidOperationException("A preview reset must leave saved calibration intact."));
        playSpace.Jump(new Vector3(3, 0, -2));
        playSpace.Jump(new Vector3(-1, 0, 5));
        playSpace.Jump(new Vector3(2, 0, 1));
        Check(submitted[^1].Matches(start.Shift(new Vector3(4, 0, 4))), "Multiple jumps accumulate from one starting origin");
        playSpace.Reset();
        Check(ReferenceEquals(submitted[^1], start) && hides == 0, "Reset reapplies the exact standing, seated and boundary snapshot");
        playSpace.Jump(new Vector3(0, 0, -2));
        Check(reads == 1 && submitted[^1].Matches(start.Shift(new Vector3(0, 0, -2))), "Jump after reset starts with zero accumulated offset");
        playSpace.EndPreview();
        Check(hides == 1, "Closing releases the temporary preview");
        var circle = new FloorPattern(start, Vector3.Zero, Vector3.UnitX,
            new CircleSettings(5, 4, false, TimeSpan.FromSeconds(1)));
        Vector3 first = circle.TakePoint(1000);
        Check(first == new Vector3(5, 0, 0) && !circle.Due(1999) && circle.Due(2000), "Radius and jump interval");
        Check(Vector3.Distance(circle.TakePoint(2000), new Vector3(0, 0, 5)) < .001f, "Sequential circle points");
        var randomCircle = new FloorPattern(start, Vector3.Zero, Vector3.UnitX, circle.Settings with { RandomOrder = true });
        var lap = Enumerable.Range(0, 4).Select(i => randomCircle.TakePoint(i * 1000)).ToArray();
        Check(lap.Distinct().Count() == 4 && lap[^1] != randomCircle.TakePoint(4000), "Random order has no consecutive repeats");
        Vector3 rawHead = start.Origin.Position + start.Origin.Rotate(head.Position);
        Vector3 rawForward = start.Origin.Rotate(head.Forward);
        Matrix34 rawPose = Matrix34.FromBasis(start.Origin.Rotate(Vector3.UnitX), start.Origin.Rotate(Vector3.UnitY),
            start.Origin.Rotate(Vector3.UnitZ), rawHead);
        var turned = start.MoveHead(rawPose, first, Vector3.Zero);
        Check(Vector3.Distance(turned.Origin.Unrotate(rawHead - turned.Origin.Position), first with { Y = head.TY }) < .001f,
            "Circle jump preserves height and reaches its point");
        Check(Vector3.Distance(turned.Origin.Unrotate(rawForward), -Vector3.UnitX) < .001f,
            "Circle jump faces the center");
        Check(Vector3.Distance(turned.Origin.Position + turned.Origin.Rotate(turned.Corners[0]), oldCorner) < .001f,
            "Turning keeps the physical boundaries fixed");
        playSpace.JumpInPattern(start, rawPose, first, Vector3.Zero);
        playSpace.Reset();
        Check(ReferenceEquals(submitted[^1], start), "Reset removes circle translation and rotation");
        playSpace.Drag(start, -Vector3.UnitY);
        Check(submitted[^1].Origin.Position == start.Origin.Position + Vector3.UnitY,
            "Space Drag moves opposite the physical pull, including vertically");
        Check(submitted[^1].Origin.Position + submitted[^1].Origin.Rotate(submitted[^1].Corners[0]) == oldCorner,
            "Space Drag keeps the physical boundary fixed");
        Check(playSpace.Offset == -Vector3.UnitY, "Live coordinates reflect the applied play-space offset");
        var accelerated = new DragControls(2) { AccelerationStrength = 1m };
        Check(accelerated.Scale(Vector3.UnitX, .5f).Length() > accelerated.Scale(Vector3.UnitX, 1f).Length() &&
            new DragControls(2).Scale(Vector3.UnitX, .5f) == 2 * Vector3.UnitX,
            "Drag multiplier and speed acceleration");
        Check(accelerated.Scale(Vector3.UnitX, .5f) == 5 * Vector3.UnitX &&
            (accelerated with { AccelerationStrength = 2 }).Scale(Vector3.UnitX, .5f) == 8 * Vector3.UnitX &&
            (accelerated with { AccelerationStrength = 0 }).Scale(Vector3.UnitX, .5f) == 2 * Vector3.UnitX,
            "Strength scales only the acceleration boost and 1x retains the original movement");
        var dragPanel = panel with { Mode = TeleportMode.SpaceDrag, Drag = accelerated };
        Check(DashboardPanel.HitTest(new PointF(530, 380), dragPanel) == DashboardAction.StrengthDown &&
            DashboardPanel.HitTest(new PointF(690, 380), dragPanel) == DashboardAction.StrengthUp &&
            DashboardPanel.HitTest(new PointF(690, 380), dragPanel with { Drag = DragControls.Default }) == DashboardAction.StrengthUp,
            "Acceleration strength is adjustable from 0x");
        var initialController = Matrix34.FromBasis(Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, Vector3.Zero);
        var controllerRotation = Quaternion.CreateFromYawPitchRoll(.55f, .6f, -.7f);
        var rotatedController = Matrix34.FromBasis(Vector3.Transform(Vector3.UnitX, controllerRotation),
            Vector3.Transform(Vector3.UnitY, controllerRotation), Vector3.Transform(Vector3.UnitZ, controllerRotation),
            new Vector3(14, 2, -5));
        var tilted = start.FollowControllerRotation(rawHead, initialController, rotatedController);
        Check(Vector3.Distance(tilted.Origin.Unrotate(rawHead - tilted.Origin.Position),
                start.Origin.Unrotate(rawHead - start.Origin.Position)) < .001f &&
            Vector3.Distance(tilted.Origin.Unrotate(Vector3.UnitY),
                start.Origin.Unrotate(Vector3.Transform(Vector3.UnitY, controllerRotation))) < .001f &&
            Vector3.Distance(tilted.Origin.Unrotate(Vector3.UnitZ),
                start.Origin.Unrotate(Vector3.Transform(Vector3.UnitZ, controllerRotation))) < .001f,
            "3D Space Drag follows controller yaw, pitch and roll around the headset");
        Check(start.FollowControllerRotation(rawHead, initialController, initialController.Translate(Vector3.One)).Matches(start),
            "Controller translation alone leaves the play space unchanged");
        var turnInPlace = start.TurnAroundHead(rawHead, -MathF.PI / 2);
        Check(Vector3.Distance(turnInPlace.Origin.Unrotate(rawHead - turnInPlace.Origin.Position), head.Position) < .001f &&
            Vector3.Distance(turnInPlace.Origin.Unrotate(rawForward), Vector3.UnitX) < .001f,
            "Right Space Turn pivots about the headset without shifting it");
        var turnOptions = TurnControls.Default;
        Check(Math.Abs(turnOptions.Angle(1, 1) + MathF.PI) < .0001f, "1× turn matches the former 2× speed");
        Check(Math.Abs((turnOptions with { Multiplier = 2 }).Angle(1, .05f) - turnOptions.Angle(1, .05f) * 2) < .0001f &&
            (turnOptions with { Enabled = false }).Angle(1, .05f) == 0 && turnOptions.Angle(.1f, .05f) == 0,
            "Space Turn toggle, speed multiplier and dead zone");
        var transitionPanel = panel with { Mode = TeleportMode.Normal, Turn = turnOptions };
        Check(DashboardPanel.HitTest(new PointF(650, 420), transitionPanel) == DashboardAction.ToggleTurn &&
            DashboardPanel.HitTest(new PointF(450, 420), transitionPanel) == DashboardAction.TurnUp &&
            DashboardPanel.HitTest(new PointF(450, 420), transitionPanel with { Turn = turnOptions with { Enabled = false } }) == DashboardAction.None,
            "Normal mode exposes Space Turn controls");
        var fast = new FloorPattern(start, Vector3.Zero, Vector3.UnitX,
            circle.Settings with { Interval = TimeSpan.FromMilliseconds(10) });
        fast.TakePoint(0);
        Check(!fast.Due(9) && fast.Due(10), "10 ms minimum interval");
        fast.TakePoint(13);
        Check(!fast.Due(19) && fast.Due(20), "Late timer ticks do not accumulate delay");
        var guidePose = CircleGuide.Pose(Vector3.Zero, FloorPattern.StartAngle(Vector3.Zero, Vector3.UnitX));
        Check(Vector3.Distance(guidePose.Rotate(Vector3.UnitX) * 5, first) < .001f, "Circle guide aligns with first landing point");
        Check(SweepControls.Default.EffectiveRowDistance == 1.5m &&
            SweepControls.SnapRowDistance(1.67m) == 1.75m && SweepControls.SnapRowDistance(.05m) == .25m,
            "Sweep row gap defaults to 1.5 m and snaps older gaps to quarter-meter steps");
        var sweepFixture = new SweepControls(10, 3, 500, PointsPerRow: 8);
        var sweepRun = new FloorPattern(start, Vector3.Zero, Vector3.UnitZ, sweepFixture);
        var sweepPoints = Enumerable.Range(0, 24).Select(i => sweepRun.TakePoint(i * 500)).ToArray();
        Check(sweepPoints[0].X < sweepPoints[7].X && sweepPoints[7].X == sweepPoints[8].X &&
            sweepPoints[8].Z < sweepPoints[7].Z && sweepPoints[8].X > sweepPoints[15].X &&
            sweepPoints[15].X == sweepPoints[16].X && sweepRun.TakePoint(12000) == sweepPoints[0],
            "Sweep alternates rows, crosses at the ends and repeats");
        var smoothSweep = new FloorPattern(start, Vector3.Zero, Vector3.UnitZ,
            sweepFixture with { SmoothTransition = true });
        Check(smoothSweep.SampleTransition(0, rawPose) == sweepPoints[0] &&
            Vector3.Distance(smoothSweep.SampleTransition(250, rawPose),
                Vector3.Lerp(sweepPoints[0], sweepPoints[1], .5f)) < .001f &&
            smoothSweep.SampleTransition(500, rawPose) == sweepPoints[1], "Smooth sweep follows the interval");
        var smoothCircle = new FloorPattern(start, Vector3.Zero, Vector3.UnitX,
            circle.Settings with { SmoothTransition = true });
        smoothCircle.SampleTransition(0, rawPose);
        Check(Vector3.Distance(smoothCircle.SampleTransition(500, rawPose),
            new Vector3(5 / MathF.Sqrt(2), 0, 5 / MathF.Sqrt(2))) < .001f,
            "Smooth circle follows the circumference");
        Check(DashboardPanel.HitTest(new Point(850, 600), panel with { Mode = TeleportMode.Circle }) ==
            DashboardAction.PatternTransition &&
            DashboardPanel.HitTest(new Point(850, 600), panel with { Mode = TeleportMode.Sweep }) ==
            DashboardAction.PatternTransition &&
            DashboardPanel.HitTest(new Point(850, 600), panel with { Mode = TeleportMode.Normal }) ==
            DashboardAction.PatternTransition, "All teleport modes expose the movement toggle");
        var travelTimePanel = panel with { Mode = TeleportMode.Normal, Turn = TurnControls.Default };
        Check(DashboardPanel.HitTest(new Point(540, 600), travelTimePanel) == DashboardAction.None &&
            DashboardPanel.HitTest(new Point(540, 600), travelTimePanel with
                { Turn = travelTimePanel.Turn with { SmoothTransition = true } }) == DashboardAction.IntervalDown &&
            DashboardPanel.HitTest(new Point(700, 600), travelTimePanel with
                { Turn = travelTimePanel.Turn with { SmoothTransition = true } }) == DashboardAction.IntervalUp,
            "Teleport travel time controls appear only in smooth mode");
        var verticalSweep = new FloorPattern(start, Vector3.Zero, Vector3.UnitZ, sweepFixture with { Vertical = true, RowDistance = 2.5m });
        var verticalPoints = Enumerable.Range(0, 9).Select(i => verticalSweep.TakePoint(i * 500)).ToArray();
        Check(MathF.Abs(verticalPoints[0].Y) < .001f && verticalPoints[0].Y == verticalPoints[7].Y &&
            MathF.Abs(verticalPoints[8].Y - verticalPoints[7].Y - 2.5f) < .001f && verticalPoints[7].X == verticalPoints[8].X &&
            MathF.Abs(verticalPoints[0].Z - verticalPoints[8].Z) < .001f &&
            DashboardPanel.HitTest(new Point(620, 600), panel with { Mode = TeleportMode.Sweep }) == DashboardAction.SweepDirection,
            "Vertical sweep starts at floor level and changes height between rows");
        playSpace.JumpInPattern(start, rawPose, verticalPoints[8], null, verticalSweep.MovesVertically);
        var elevated = submitted[^1].Origin.Unrotate(rawHead - submitted[^1].Origin.Position);
        Check(MathF.Abs(elevated.Y - head.TY - verticalPoints[8].Y) < .001f,
            "Vertical sweep adds the row height to the user's tracked height");
        var combinedSweep = new FloorPattern(start, Vector3.Zero, Vector3.UnitZ,
            SweepControls.Default with { Rows = 2, PointsPerRow = 2, Stacked = true, RowDistance = 2m });
        var stacked = Enumerable.Range(0, 8).Select(i => combinedSweep.TakePoint(i * 500)).ToArray();
        Check(stacked.Take(4).Select(p => p.Z).Distinct().Count() == 2 &&
            Vector3.Distance(stacked[4] - stacked[3], Vector3.UnitY * 2) < .001f &&
            Vector3.Distance(stacked[7] - stacked[0], Vector3.UnitY * 2) < .001f,
            "Stacked sweep covers two floor rows per height and climbs at the end of each layer");
        var oneRow = new FloorPattern(start, Vector3.Zero, Vector3.UnitZ,
            SweepControls.Default with { Rows = 1, PointsPerRow = 2, Stacked = true });
        var firstInRow = oneRow.TakePoint(0);
        Check(oneRow.TakePoint(500) != firstInRow && oneRow.TakePoint(1000) == firstInRow,
            "One-row stacked sweep plays and repeats a single row");
        var randomSweep = new FloorPattern(start, Vector3.Zero, Vector3.UnitZ,
            sweepFixture with { Rows = 2, PointsPerRow = 3, Random = true });
        var shuffled = Enumerable.Range(0, 6).Select(i => randomSweep.TakePoint(i * 500)).ToArray();
        Check(shuffled.Distinct().Count() == 6 && !randomSweep.Due(2999) && randomSweep.Due(3000) &&
            shuffled[^1] != randomSweep.TakePoint(3000), "Sweep random order visits each point per pass at 500 ms");
        Vector3 placed = new(6, 0, -4);
        playSpace.Drag(start, new Vector3(3, -1, 2));
        playSpace.JumpToRecordedPoint(placed, rawHead);
        var destination = submitted[^1].Origin.Unrotate(rawHead - submitted[^1].Origin.Position);
        Check(Vector3.Distance(destination, placed with { Y = head.TY - 1 }) < .001f,
            "A custom jump reaches the recorded point and preserves height");
        var firstVisit = submitted[^1];
        playSpace.JumpToRecordedPoint(placed + Vector3.UnitX, rawHead);
        playSpace.JumpToRecordedPoint(placed, rawHead);
        Check(submitted[^1].Matches(firstVisit), "Looping through custom points does not accumulate an offset");
        var recordedPosition = new Vector3(4, 2.25f, -3);
        playSpace.JumpToRecordedPose(recordedPosition, MathF.PI / 2, rawPose);
        var recordedOrigin = submitted[^1].Origin;
        Check(Vector3.Distance(recordedOrigin.Unrotate(rawHead - recordedOrigin.Position), recordedPosition) < .001f &&
            Vector3.Distance(recordedOrigin.Unrotate(rawPose.Forward), -Vector3.UnitX) < .001f,
            "Named custom patterns restore recorded height and facing");
        playSpace.JumpToRecordedPoint(placed, rawHead, allowHeight: true);
        var positionOnly = submitted[^1].Origin;
        Check(Vector3.Distance(positionOnly.Unrotate(rawHead - positionOnly.Position), placed) < .001f &&
            Vector3.Distance(positionOnly.Forward, recordedOrigin.Forward) < .001f,
            "Rotation off restores recorded height and preserves facing");
        playSpace.JumpToRecordedPose(recordedPosition, MathF.PI / 2, rawPose, allowHeight: false);
        var flatOrigin = submitted[^1].Origin;
        Check(Vector3.Distance(flatOrigin.Unrotate(rawHead - flatOrigin.Position), recordedPosition with { Y = placed.Y }) < .001f &&
            Vector3.Distance(flatOrigin.Unrotate(rawPose.Forward), -Vector3.UnitX) < .001f,
            "Height off preserves headset height and restores recorded facing");
        var draft = new SavedPattern([placed, placed + Vector3.UnitX], 70);
        var aimedStart = new Vector3(12, 0, -7);
        var aimedPlayback = new CustomPlayback(draft with { RandomOrder = true }, aimedStart);
        var (firstAimedPoint, firstAimedIndex) = aimedPlayback.TakePoint(0);
        Check(firstAimedPoint == new Vector3(aimedStart.X, draft.Points[0].Y, aimedStart.Z) && firstAimedIndex == 1,
            "Custom playback starts at the pointer and preserves the recorded height");
        var playback = new CustomPlayback(draft);
        Check(playback.TakePoint(0).Point == placed && !playback.Due(69) && playback.Due(70) &&
            playback.TakePoint(70).Index == 2 && playback.TakePoint(140).Index == 1,
            "Custom playback follows the recorded order and loops at the saved interval");
        string savedPath = Path.Combine(Path.GetTempPath(), $"space-teleporter-test-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(savedPath, """{"1":{"Points":[{"X":1,"Y":2,"Z":3}],"IntervalMs":110}}""");
            var library = new PatternLibrary(savedPath);
            var timed = new SavedPattern([Vector3.UnitX, Vector3.UnitX * 3], 70)
                { Repeat = false, SmoothTransition = true, TravelTimesMs = [100, 300] };
            library.Save(4, timed);
            Check(library.Load(4) is { SmoothTransition: true, TravelTimesMs: [100, 300] },
                "Custom travel times and movement mode persist");
            var timedRun = new CustomPlayback(timed);
            var origin = Matrix34.FromBasis(Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, Vector3.Zero);
            timedRun.Sample(0, origin, origin);
            Check(timedRun.Sample(50, origin, origin).Point == Vector3.UnitX * .5f &&
                !timedRun.Sample(100, origin, origin).Complete &&
                timedRun.Sample(250, origin, origin).Point == Vector3.UnitX * 2 &&
                timedRun.Sample(400, origin, origin).Complete,
                "Smooth custom playback uses the destination's travel time and finishes on arrival");
            var editedPanel = panel with { Mode = TeleportMode.Custom, Custom = panel.Custom with
                { Selected = 0, EditPoint = 0, Pattern = timed } };
            Check(DashboardPanel.HitTest(new Point(340, 300), editedPanel) == DashboardAction.PointCell &&
                DashboardPanel.HitTest(new Point(430, 344), editedPanel) ==
                    DashboardAction.PointCell + DashboardPanel.PointColumns + 1 &&
                timed.CellText(0, 1) == "-" && timed.CellText(0, 5) == "-",
                "Numbered point rows expose editable cells and show absent values as dashes");
            var onePass = new CustomPlayback(draft with { RandomOrder = true, Repeat = false });
            Check(onePass.TakePoint(0).Index != onePass.TakePoint(70).Index && onePass.Finished && !onePass.Due(140),
                "A shuffled single pass visits every point once and stops");
            var single = new CustomPlayback(new SavedPattern([placed], 10) { RandomOrder = true });
            Check(single.TakePoint(0).Index == 1 && !single.Due(9) && single.Due(10), "Single-point random repeat");
            Check(library.Load(1) is { Repeat: true, RandomOrder: false, IntervalMs: 110 }, "Legacy patterns repeat sequentially");
            library.Save(1, draft with { IntervalMs = 10, Repeat = false, RandomOrder = true });
            library.Save(2, new SavedPattern([Vector3.UnitZ], 100));
            SavedPattern? loaded = new PatternLibrary(savedPath).Read().Values.Single(p => p.IntervalMs == 10);
            Check(loaded is { Repeat: false, RandomOrder: true } && loaded.IntervalMs == 10 && loaded.Points.SequenceEqual(draft.Points),
                "Pattern slots persist the points and interval without overwriting other slots");
            library.Save(3, new SavedPattern([recordedPosition], 100, "Test pattern", [1.5f]));
            Check(library.Load(3) is { Name: "Test pattern", Yaws: [1.5f], UseRecordedHeight: true }, "Named patterns retain their heading");
            library.Save(3, library.Load(3)! with { UseRecordedHeight = false });
            Check(library.Load(3) is { UseRecordedHeight: false, Yaws: [1.5f] }, "Height off persists independently of rotation");
            library.Delete(3);
            Check(library.Load(3) is null && library.Load(1) is not null, "Deleting one pattern preserves other patterns");

            var preferences = new SettingsFile(savedPath + ".settings.json");
            Check(preferences.Load() == AppSettings.FreshDefaults, "Missing settings use the fresh-install defaults");
            var changed = new AppSettings
            {
                Mode = TeleportMode.Custom, Enabled = false, Circle = new(8.25m, 16, 10, true),
                Drag = new DragControls(20.75m) { AccelerationStrength = 8.25m },
                Turn = new(false, 15.25m), StartMinimized = true, CloseToTray = true, MinimizeToTray = true, ShowCoordinates = false, LockDragHeight = true, SaveRotation = false, SaveHeight = false,
                FlightGravity = 30, FlightResistance = 40, SpaceDragGravity = 5, SpaceDragResistance = 80,
                FlyRotationSmoothing = 70, DirectFlight = true, IdleHoverBobbing = true, HoverBobbingIntensity = 12, FlyPitchOffset = 12, FlyYawOffset = -8, ShowFlightDirectionLine = false, WindowPosition = new Point(-420, 135)
            };
            preferences.Save(changed);
            Check(new SettingsFile(savedPath + ".settings.json").Load() == changed,
                "Mode, switches and numeric preferences survive a reload");
            preferences.Save(changed with { Mode = TeleportMode.SpaceDrag, Drag = new(2) });
            Check(preferences.Load() == changed with { Mode = TeleportMode.SpaceDrag, Drag = new(2) },
                "Saving again replaces the stored settings");
            File.WriteAllText(savedPath + ".settings.json", """{"Drag":{"Multiplier":2.75,"Accelerate":true}}""");
            Check(preferences.Load() is { FlyRotationSmoothing: 35 } legacy && legacy.Drag == new DragControls(2.75m),
                "Settings without acceleration strength load at 0x");
        }
        finally
        {
            File.Delete(savedPath);
            File.Delete(savedPath + ".migrated");
            string testFolder = Path.ChangeExtension(savedPath, null);
            if (Directory.Exists(testFolder))
            {
                foreach (string file in Directory.EnumerateFiles(testFolder)) File.Delete(file);
                Directory.Delete(testFolder);
            }
            File.Delete(savedPath + ".settings.json");
        }
        return "All targeting, boundary, recovery, shared controls, Space Drag, Space Turn, custom pattern and settings checks passed.";
    }
}

internal sealed class MainWindow : Form
{
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 10 };
    private readonly AimGesture gesture = new();
    private bool routingDrag;
    private bool keepSpaceDragActive;
    private bool BackgroundDragEnabled => keepSpaceDragActive && mode is TeleportMode.Normal or TeleportMode.Circle or TeleportMode.Sweep;
    private readonly DashboardPanel panel = new();
    private readonly PictureBox screen = new() { SizeMode = PictureBoxSizeMode.StretchImage, Cursor = Cursors.Hand };
    private readonly SettingsFile? settingsFile;
    private long? saveSettingsAt;
    private Point? windowPosition;
    private string? settingsError;
    private bool teleportEnabled = true, closing, dashboardAvailable, dashboardDirty;
    private bool autoConnect, connecting;
    private long nextConnectAttempt;
    private TeleportMode mode;
    private CircleControls settings = CircleControls.Default;
    private int gravity3D, spaceDragGravity, ceilingCm = 300;
    private DragPivot rotationPivot = DragPivot.Headset;
    private Vector3 dragTranslationRemainder;
    private bool spaceDragRotate;
    private float dragHeading, dragYawVelocity;
    private Vector3 dragYawPivot;
    private bool spaceDragLockHeight, ceilingBounce, spaceDragFalling;
    private float verticalSpeed;
    private int spaceDragResistance = 100, flightResistance = 100, flightGravity;
    private Vector3 dragVelocity;
    private long gravityTick, ceilingPreviewUntil;
    private decimal turnStrength;
    private int turnDragSteps = 4;
    private int floorOffsetCm = 70, spaceDragFloorOffsetCm = 70, spaceDragCeilingCm = 300;
    private bool spaceDragFloorBounce, spaceDragCeilingBounce;
    private int ceilingPreviewCm;
    private readonly List<DragWall> walls = new(4);
    private bool placingWalls, showDragGrids;
    private bool showRegularDragCeiling;
    private long wallFadeUntil;
    private bool IsDragMode => mode is TeleportMode.SpaceDrag or TeleportMode.ControllerTurn or TeleportMode.Fly;
    private bool RegularDragSettings => mode == TeleportMode.SpaceDrag;
    private decimal flySpeed = 2m, flyTopSpeed = 15m, flyAcceleration;
    private DragPivot flyDirection = DragPivot.Headset;
    private long flyStarted, flySample, hoverTick;
    private float hoverPhase;
    private bool idleHoverBobbing;
    private int hoverBobbingIntensity = 4;
    private float flyHeading;
    private Vector3 flyForward;
    private bool flyTurnTracked, directFlight;
    private Vector3 flyNeutralForward;
    private float flyTurnRate, flyEntrySpeed;
    private int flyRotationSmoothing = 35, flyPitchOffset, flyYawOffset;
    private bool showFlightDirectionLine = true;
    private bool floorBounce;
    private bool resistanceLocked;
    private int spinResistance = 100;
    private int glideResistance = 100;
    private DragControls dragSettings = DragControls.Default;
    private (CalibrationSnapshot Anchor, Vector3 Movement, long Started, int DurationMs)? normalTransition;
    private bool normalTeleported;
    private TurnControls turnSettings = TurnControls.Default;
    private long lastTurnTick, nextOffsetSample;
    private Vector3 displayedOffset;
    private Vector3? displayedRotation;
    private PanelState? rendered;
    private Point? vrPointer;
    private DashboardPointerFrame uploadedPointer;
    private Rectangle hoverBounds;
    private long hoverStarted;
    private Point? desktopPointer;
    private Rectangle desktopHoverBounds;
    private long desktopHoverStarted;
    private byte desktopHoverOpacity;
    private OpenVr? vr;
    private PlaySpace? space;
    private Vector3? shownTarget;
    private Vector3 shownHeadPosition;
    private FloorPattern? pattern;
    private SweepControls sweepSettings = SweepControls.Default;
    private readonly PatternLibrary patternLibrary = PatternLibrary.ForAppFolder();
    private readonly List<Vector3> customPoints = [];
    private readonly List<Vector3> customAngles = [];
    private int[] patternIds = [];
    private string[] patternNames = [];
    private int patternPage, selectedPattern = -1;
    private SavedPattern? activePattern;
    private int editPoint = -1;
    private bool draggingPointScroll;
    private bool pointScrollInVr;
    private float pointThumbOffset;
    private (int Point, int Field)? editingCell;
    private readonly TextBox pointInput = new() { Visible = false, TextAlign = HorizontalAlignment.Center, MaxLength = 32 };
    private bool recording, lockDragHeight, naming, confirmDelete;
    private bool editorDrag = true, editorDrag3D;
    private string? headsetPose;
    private bool saveRotation = true, saveHeight;
    private string draftName = "", renameName = "";
    private readonly NotifyIcon tray = new();
    private readonly ContextMenuStrip trayMenu = new()
    {
        BackColor = InterfaceStyle.Card,
        ForeColor = InterfaceStyle.Text,
        Padding = new Padding(5),
        ShowImageMargin = false,
        Renderer = new TrayMenuRenderer()
    };
    private long nextRuntimeCheck;
    private bool runtimeSeen, startMinimized, closeToTray, minimizeToTray, autoLaunch, quitRequested;
    private bool settingsOpen, showCoordinates = true, showPlaySpaceCenter;
    private bool centerColorPickerOpen, colorDragInVr;
    private int colorDragChannel = -1;
    private int playSpaceCenterColor = 0xffffff;
    private int playSpaceCenterTransparency;
    private int playSpaceCenterSize = 100;
    private bool scalePlaySpaceCenterWithDistance;
    private CustomPlayback? customPlayback;
    private DashboardAction repeatedStep;
    private bool repeatingInVr;
    private long repeatAt, desktopBindingsNoticeUntil;
    private (CalibrationSnapshot Anchor, Vector3 Previous, Vector3 Movement, long At)? drag;
    private readonly RotationDriver rotationDriver = new();
    private string connectionStatus = "Waiting for SteamVR.";
    private string ready = "Waiting for SteamVR.";
    private string status = "Waiting for SteamVR.";

    public MainWindow(bool connectOnShow = true)
    {
        if (connectOnShow)
        {
            settingsFile = SettingsFile.ForAppFolder();
            try
            {
                var saved = settingsFile.Load();
                mode = saved.Mode;
                teleportEnabled = saved.Enabled;
                settings = saved.Circle;
                sweepSettings = saved.Sweep;
                gravity3D = Math.Clamp(saved.Gravity3D ?? 0, -100, 100);
                spaceDragResistance = Math.Clamp(saved.SpaceDragResistance, 0, 100);
                spaceDragGravity = Math.Clamp(saved.SpaceDragGravity, -100, 100);
                flightGravity = Math.Clamp(saved.FlightGravity ?? spaceDragGravity, -100, 100);
                flightResistance = Math.Clamp(saved.FlightResistance ?? spaceDragResistance, 0, 100);
                flySpeed = Math.Clamp(saved.FlySpeed, .25m, 30m);
                flyTopSpeed = saved.FlyTopSpeed > 0 && saved.FlyTopSpeedEnabled != false
                    ? Math.Clamp(saved.FlyTopSpeed, flySpeed, 100m) : 0m;
                flyRotationSmoothing = Math.Clamp(saved.FlyRotationSmoothing, 0, 100);
                flyPitchOffset = Math.Clamp(saved.FlyPitchOffset, -FlightSteering.MaxOffset, FlightSteering.MaxOffset);
                flyYawOffset = Math.Clamp(saved.FlyYawOffset, -FlightSteering.MaxOffset, FlightSteering.MaxOffset);
                showFlightDirectionLine = saved.ShowFlightDirectionLine;
                directFlight = saved.DirectFlight;
                idleHoverBobbing = saved.IdleHoverBobbing;
                hoverBobbingIntensity = Math.Clamp(saved.HoverBobbingIntensity, 1, 20);
                flyAcceleration = Math.Clamp(saved.FlyAcceleration, 0m, 30m);
                flyDirection = saved.FlyDirection is >= DragPivot.RightController and <= DragPivot.Headset ? saved.FlyDirection : DragPivot.Headset;
                editorDrag = saved.EditorDrag;
                editorDrag3D = saved.EditorDrag3D;
                spaceDragLockHeight = saved.SpaceDragLockHeight;
                spaceDragRotate = saved.SpaceDragRotate;
                rotationPivot = saved.RotationPivot is >= DragPivot.RightController and < DragPivot.FirstTracker + 63 ? saved.RotationPivot : DragPivot.Headset;
                keepSpaceDragActive = saved.KeepSpaceDragActive;
                ceilingBounce = saved.CeilingBounce;
                ceilingCm = Math.Clamp(saved.CeilingCm, 50, 2000);
                turnStrength = Math.Clamp(saved.TurnStrength, 0m, int.MaxValue / 4m);
                turnDragSteps = Math.Clamp(saved.TurnDragSteps, 1, 400);
                floorOffsetCm = Math.Clamp(saved.FloorOffsetCm, 0, 300);
                floorBounce = saved.FloorBounce;
                spaceDragFloorBounce = saved.SpaceDragFloorBounce;
                spaceDragFloorOffsetCm = Math.Clamp(saved.SpaceDragFloorOffsetCm, 0, 300);
                spaceDragCeilingBounce = saved.SpaceDragCeilingBounce;
                spaceDragCeilingCm = Math.Clamp(saved.SpaceDragCeilingCm, 50, 2000);
                resistanceLocked = saved.ResistanceLocked;
                spinResistance = Math.Clamp(resistanceLocked ? saved.GlideResistance : saved.SpinResistance ?? saved.GlideResistance, 0, 100);
                glideResistance = Math.Clamp(saved.GlideResistance, 0, 100);
                dragSettings = saved.Drag;
                turnSettings = saved.Turn;
                startMinimized = saved.StartMinimized;
                windowPosition = saved.WindowPosition;
                closeToTray = saved.CloseToTray;
                minimizeToTray = saved.MinimizeToTray;
                showCoordinates = saved.ShowCoordinates;
        showPlaySpaceCenter = saved.ShowPlaySpaceCenter;
        scalePlaySpaceCenterWithDistance = saved.ScalePlaySpaceCenterWithDistance;
        playSpaceCenterColor = saved.PlaySpaceCenterColor & 0xffffff;
        playSpaceCenterTransparency = Math.Clamp(saved.PlaySpaceCenterTransparency, 0, 100);
        playSpaceCenterSize = Math.Clamp(saved.PlaySpaceCenterSize, DashboardPanel.MinCenterSize, DashboardPanel.MaxCenterSize);
                lockDragHeight = saved.LockDragHeight;
                saveRotation = saved.SaveRotation;
                saveHeight = saved.SaveHeight;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                settingsError = "Could not load settings. Using defaults.";
            }
        }
        try { RefreshPatterns(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            ready = status = "Could not read saved patterns.";
        }
        KeyPreview = true;
        KeyPress += (_, e) =>
        {
            if (!naming || settingsOpen || mode != TeleportMode.Custom) return;
            e.Handled = true;
            if (e.KeyChar == '\r') Run(() => HandleAction(DashboardAction.FinishName));
            else if (e.KeyChar == '\b') Run(() => HandleAction(DashboardAction.Backspace));
            else if (!char.IsControl(e.KeyChar) && EditingName.Length < 32) { EditingName += e.KeyChar; RefreshPanel(); }
        };
        Text = "OVR Space Control";
        Font = new Font("Segoe UI", 10);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(800, 656);
        BackColor = InterfaceStyle.Background;
        using var iconStream = typeof(MainWindow).Assembly.GetManifestResourceStream("AppIcon.ico")!;
        Icon = new Icon(iconStream);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        if (windowPosition is Point savedPosition)
        {
            StartPosition = FormStartPosition.Manual;
            Rectangle area = Screen.FromRectangle(new Rectangle(savedPosition, Size)).WorkingArea;
            Location = new Point(
                Math.Clamp(savedPosition.X, area.Left, Math.Max(area.Left, area.Right - Width)),
                Math.Clamp(savedPosition.Y, area.Top, Math.Max(area.Top, area.Bottom - Height)));
        }

        screen.Controls.Add(pointInput);
        pointInput.KeyDown += (_, e) =>
        {
            if (e.KeyCode is not (Keys.Enter or Keys.Escape)) return;
            e.SuppressKeyPress = true;
            Run(() => FinishPointInput(e.KeyCode == Keys.Enter ? pointInput.Text : null));
        };
        pointInput.Leave += (_, _) =>
        {
            if (pointInput.Visible) Run(() => FinishPointInput(pointInput.Text));
        };
        screen.Dock = DockStyle.Fill;
        screen.Image = panel.Frame;
        screen.MouseMove += (_, e) =>
        {
            desktopPointer = e.Location;
            if (colorDragChannel >= 0 && !colorDragInVr)
                Run(() => HandleAction(DashboardPanel.CenterChannelAction(colorDragChannel, DesktopPanelPoint(e.Location).X)));
            if (draggingPointScroll && !pointScrollInVr) Run(() => MovePointScroll(DesktopPanelPoint(e.Location).Y));
            UpdateDesktopHover();
        };
        void ScrollPointList(object? sender, MouseEventArgs e)
        {
            if (e is HandledMouseEventArgs { Handled: true }) return;
            Point location = screen.PointToClient(Cursor.Position);
            if (editPoint < 0 || mode != TeleportMode.Custom || settingsOpen ||
                !screen.ClientRectangle.Contains(location) || DesktopPanelPoint(location).X < 286) return;
            if (e is HandledMouseEventArgs handled) handled.Handled = true;
            Run(() => SetPointScroll(editPoint - Math.Sign(e.Delta) * 3));
        }
        MouseWheel += ScrollPointList;
        screen.MouseWheel += ScrollPointList;
        screen.MouseLeave += (_, _) => { desktopPointer = null; UpdateDesktopHover(); };
        screen.Paint += (_, e) =>
        {
            if (desktopHoverBounds.IsEmpty) return;
            e.Graphics.ScaleTransform(screen.Width / (float)DashboardPanel.Width, screen.Height / (float)DashboardPanel.Height);
            DashboardPanel.DrawHover(e.Graphics, desktopHoverBounds, desktopHoverOpacity);
        };
        screen.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            desktopPointer = e.Location;
            screen.Capture = true;
            Run(() => HandlePointer(DesktopAction(e.Location), down: true, inVr: false));
        };
        screen.MouseUp += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            Run(() => HandlePointer(DesktopAction(e.Location), down: false, inVr: false));
            screen.Capture = false;
        };
        screen.MouseCaptureChanged += (_, _) =>
        {
            if (!screen.Capture) draggingPointScroll = false;
            if (!screen.Capture && !repeatingInVr) repeatedStep = DashboardAction.None;
        };
        Controls.Add(screen);
        RefreshPanel();

        if (connectOnShow)
        {
            if (startMinimized) WindowState = FormWindowState.Minimized;
            Shown += (_, _) =>
            {
                autoConnect = true;
                RememberWindowPosition();
                if (startMinimized && minimizeToTray) HideToTray();
            };
        }
        timer.Tick += (_, _) => Run(Tick);
        timer.Start();
        tray.Text = Text;
        tray.Icon = Icon ?? SystemIcons.Application;
        tray.ContextMenuStrip = trayMenu;
        tray.DoubleClick += (_, _) => RestoreFromTray();
        trayMenu.Items.Add(new ToolStripMenuItem("Open OVR Space Control", null, (_, _) => RestoreFromTray())
        {
            Padding = new Padding(12, 6, 12, 6)
        });
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add(new ToolStripMenuItem("Quit", null, (_, _) => { quitRequested = true; Close(); })
        {
            Padding = new Padding(12, 6, 12, 6), ForeColor = Color.FromArgb(255, 133, 141)
        });
        LocationChanged += (_, _) => RememberWindowPosition();
        Resize += (_, _) =>
        {
            if (autoConnect && minimizeToTray && WindowState == FormWindowState.Minimized) HideToTray();
        };
        FormClosing += (_, e) =>
        {
            if (closeToTray && !quitRequested && e.CloseReason == CloseReason.UserClosing && connectOnShow)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }
            closing = true;
            SaveSettings();
            Disconnect();
        };
    }

    private void RememberWindowPosition()
    {
        if (!autoConnect || !Visible || WindowState != FormWindowState.Normal || windowPosition == Location) return;
        windowPosition = Location;
        saveSettingsAt = Environment.TickCount64 + 250;
    }

    private void HideToTray()
    {
        tray.Visible = true;
        Hide();
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
        RememberWindowPosition();
        tray.Visible = false;
    }

    private PanelState State => new(teleportEnabled, mode, settings, settingsError ?? status, vr is not null, dragSettings,
        displayedOffset, turnSettings, new CustomEditorState(recording, editorDrag, lockDragHeight, activePattern?.Points.Length ?? customPoints.Count,
            activePattern?.IntervalMs ?? SavedPattern.DefaultIntervalMs, naming ? EditingName : activePattern?.Name ?? draftName,
            naming, confirmDelete, patternPage, patternNames, selectedPattern, activePattern?.Repeat ?? true, activePattern?.RandomOrder ?? false, saveRotation, headsetPose, saveHeight, activePattern, editPoint, editorDrag3D), settingsOpen, showCoordinates, dashboardAvailable, autoLaunch, closeToTray, minimizeToTray, startMinimized, sweepSettings, rotationDriver.Ready, rotationDriver.Registered, rotationDriver.InstallMessage, rotationDriver.Busy, mode == TeleportMode.Fly ? flightResistance : RegularDragSettings ? spaceDragResistance : glideResistance, gravity3D == 0, turnDragSteps, turnStrength, spinResistance, RegularDragSettings ? spaceDragFloorBounce : floorBounce, RegularDragSettings ? spaceDragFloorOffsetCm : floorOffsetCm, resistanceLocked, mode == TeleportMode.Fly ? flightGravity : RegularDragSettings ? spaceDragGravity : gravity3D, spaceDragLockHeight, RegularDragSettings ? spaceDragCeilingBounce : ceilingBounce, RegularDragSettings ? spaceDragCeilingCm : ceilingCm, displayedRotation, connectionStatus, spaceDragRotate, walls.Count, placingWalls, keepSpaceDragActive, showDragGrids, Environment.TickCount64 < desktopBindingsNoticeUntil, rotationPivot, showPlaySpaceCenter, playSpaceCenterColor, centerColorPickerOpen, playSpaceCenterTransparency, playSpaceCenterSize, scalePlaySpaceCenterWithDistance, flySpeed, flyDirection, flyTopSpeed, flyAcceleration, flyRotationSmoothing,
            flyPitchOffset, flyYawOffset, showFlightDirectionLine, directFlight, idleHoverBobbing, hoverBobbingIntensity);

    private string EditingName
    {
        get => activePattern is null ? draftName : renameName;
        set
        {
            if (activePattern is null) draftName = value;
            else renameName = value;
        }
    }

    private AppSettings CurrentSettings => new()
    {
        Mode = mode,
        Enabled = teleportEnabled,
        Circle = settings,
        Sweep = sweepSettings,
        Gravity3D = gravity3D,
        SpaceDragResistance = spaceDragResistance,
        SpaceDragGravity = spaceDragGravity,
        FlightGravity = flightGravity,
        FlightResistance = flightResistance,
            IdleHoverBobbing = idleHoverBobbing, HoverBobbingIntensity = hoverBobbingIntensity, DirectFlight = directFlight, FlyPitchOffset = flyPitchOffset, FlyYawOffset = flyYawOffset, ShowFlightDirectionLine = showFlightDirectionLine,
            FlySpeed = flySpeed, FlyTopSpeed = flyTopSpeed, FlyAcceleration = flyAcceleration, FlyRotationSmoothing = flyRotationSmoothing, FlyDirection = flyDirection, EditorDrag = editorDrag, EditorDrag3D = editorDrag3D,
        KeepSpaceDragActive = keepSpaceDragActive,
        SpaceDragRotate = spaceDragRotate,
        RotationPivot = rotationPivot,
        SpaceDragLockHeight = spaceDragLockHeight,
        CeilingBounce = ceilingBounce,
        CeilingCm = ceilingCm,
        TurnStrength = turnStrength,
        TurnDragSteps = turnDragSteps,
        ResistanceLocked = resistanceLocked,
        FloorOffsetCm = floorOffsetCm,
        FloorBounce = floorBounce,
        SpaceDragFloorBounce = spaceDragFloorBounce,
        SpaceDragFloorOffsetCm = spaceDragFloorOffsetCm,
        SpaceDragCeilingBounce = spaceDragCeilingBounce,
        SpaceDragCeilingCm = spaceDragCeilingCm,
        SpinResistance = spinResistance,
        GlideResistance = glideResistance,
        Drag = dragSettings,
        Turn = turnSettings,
        StartMinimized = startMinimized,
        WindowPosition = windowPosition,
        CloseToTray = closeToTray,
        MinimizeToTray = minimizeToTray,
        ShowCoordinates = showCoordinates,
            ShowPlaySpaceCenter = showPlaySpaceCenter,
            PlaySpaceCenterColor = playSpaceCenterColor,
            PlaySpaceCenterTransparency = playSpaceCenterTransparency,
            PlaySpaceCenterSize = playSpaceCenterSize,
            ScalePlaySpaceCenterWithDistance = scalePlaySpaceCenterWithDistance,
        LockDragHeight = lockDragHeight,
        SaveRotation = saveRotation,
        SaveHeight = saveHeight,
    };

    private void SaveSettings()
    {
        if (settingsFile is null || saveSettingsAt is null) return;
        try
        {
            settingsFile.Save(CurrentSettings);
            saveSettingsAt = null;
            settingsError = null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            settingsError = "Could not save settings. Retrying.";
            saveSettingsAt = Environment.TickCount64 + 2000;
        }
    }

    private PointF DesktopPanelPoint(Point position) => new(
        position.X * (float)DashboardPanel.Width / screen.Width,
        position.Y * (float)DashboardPanel.Height / screen.Height);

    private DashboardAction DesktopAction(Point position) => DashboardPanel.HitTest(DesktopPanelPoint(position), State);

    private static byte HoverOpacity(Rectangle bounds, long elapsed) =>
        bounds.IsEmpty ? (byte)0 : (byte)(90 + Math.Min(100, elapsed) * 130 / 100);

    private void UpdateDesktopHover()
    {
        var bounds = desktopPointer is Point point ? DashboardPanel.HitTarget(DesktopPanelPoint(point), State).Bounds : Rectangle.Empty;
        long now = Environment.TickCount64;
        bool changed = bounds != desktopHoverBounds;
        if (changed)
        {
            desktopHoverBounds = bounds;
            desktopHoverStarted = now;
        }
        byte opacity = HoverOpacity(bounds, now - desktopHoverStarted);
        if (!changed && opacity == desktopHoverOpacity) return;
        desktopHoverOpacity = opacity;
        screen.Invalidate();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        InterfaceStyle.MatchTitleBar(Handle);
    }

    public void SavePanelPreview(string path) => panel.SavePreview(path);

    internal void SelectMode(TeleportMode next)
    {
        settingsOpen = false;
        centerColorPickerOpen = false;
        colorDragChannel = -1;
        if (mode == next && (next != TeleportMode.Custom || activePattern is null)) return;
        activePattern = null;
        editPoint = -1;
        selectedPattern = -1;
        confirmDelete = false;
        EndSpaceTurn();
        CancelAction();
        repeatedStep = DashboardAction.None;
        mode = next;
        status = ready;
        RefreshPanel();
    }

    private void HandleAction(DashboardAction action)
    {
        if (action == DashboardAction.None) return;
        if (action is not (DashboardAction.EditName or DashboardAction.FinishName or DashboardAction.Backspace))
        {
            vr?.HideNameKeyboard();
            naming = false;
        }
        AppSettings before = CurrentSettings;
        switch (action)
        {
            case DashboardAction.RotationPivot:
                int trackerCount = vr?.ConnectedTrackers().Length ?? 0;
                var nextPivot = rotationPivot + 1;
                rotationPivot = (int)nextPivot < (int)DragPivot.FirstTracker + trackerCount ? nextPivot : DragPivot.RightController;
                break;
            case DashboardAction.Settings:
                settingsOpen = !settingsOpen;
                centerColorPickerOpen = false;
                colorDragChannel = -1;
                if (settingsOpen) rotationDriver.ReadRegistration();
                repeatedStep = DashboardAction.None;
                break;
            case DashboardAction.DriverInstall:
                _ = ChangeDriverRegistration();
                break;
            case DashboardAction.SteamStartup:
                if (vr is not null)
                {
                    vr.SetAutoLaunch(!vr.GetAutoLaunch());
                    autoLaunch = vr.GetAutoLaunch();
                }
                break;
            case DashboardAction.FlightDirectionLine: showFlightDirectionLine = !showFlightDirectionLine; break;
            case DashboardAction.CloseToTray: closeToTray = !closeToTray; break;
            case DashboardAction.StartMinimized: startMinimized = !startMinimized; break;
            case DashboardAction.MinimizeToTray: minimizeToTray = !minimizeToTray; break;
            case DashboardAction.ScaleCenterWithDistance:
                scalePlaySpaceCenterWithDistance = !scalePlaySpaceCenterWithDistance;
                break;
            case DashboardAction.PickCenterColor:
                centerColorPickerOpen = !centerColorPickerOpen;
                break;
            case >= DashboardAction.CenterColorChannel and < DashboardAction.CenterColorChannel + DashboardPanel.ColorChannelCount * DashboardPanel.ColorChannelValues:
                int channelValue = action - DashboardAction.CenterColorChannel;
                int channel = channelValue / DashboardPanel.ColorChannelValues;
                int value = channelValue % DashboardPanel.ColorChannelValues;
                if (channel == DashboardPanel.TransparencyChannel)
                {
                    playSpaceCenterTransparency = value;
                    break;
                }
                if (channel == DashboardPanel.SizeChannel)
                {
                    playSpaceCenterSize = DashboardPanel.MinCenterSize + value;
                    break;
                }
                int shift = (2 - channel) * 8;
                playSpaceCenterColor = (playSpaceCenterColor & ~(255 << shift)) | (value << shift);
                break;
            case DashboardAction.PlaySpaceCenter: showPlaySpaceCenter = !showPlaySpaceCenter; break;
            case DashboardAction.Coordinates: showCoordinates = !showCoordinates; break;
            case DashboardAction.OpenDashboard: vr?.OpenDashboard(); break;
            case DashboardAction.Bindings: vr?.OpenBindings(); break;
            case DashboardAction.UndoPoint:
                if (customPoints.Count == 0) break;
                customPoints.RemoveAt(customPoints.Count - 1);
                customAngles.RemoveAt(customAngles.Count - 1);
                ready = status = "Last point removed.";
                break;
            case DashboardAction.Normal: SelectMode(TeleportMode.Normal); break;
            case DashboardAction.Circle: SelectMode(TeleportMode.Circle); break;
            case DashboardAction.Sweep: SelectMode(TeleportMode.Sweep); break;
            case DashboardAction.SweepDirection: sweepSettings = sweepSettings.Stacked ? sweepSettings with { Stacked = false, Vertical = false }
                    : sweepSettings.Vertical ? sweepSettings with { Stacked = true, Vertical = false }
                    : sweepSettings with { Vertical = true };
                break;
            case DashboardAction.Fly: SelectMode(TeleportMode.Fly); break;
            case DashboardAction.HoverIntensityDown:
            case DashboardAction.HoverIntensityUp:
                hoverBobbingIntensity = Math.Clamp(hoverBobbingIntensity + (action == DashboardAction.HoverIntensityUp ? 1 : -1), 1, 20);
                break;
            case DashboardAction.IdleHoverBobbing:
                idleHoverBobbing = !idleHoverBobbing;
                hoverTick = 0;
                break;
            case DashboardAction.DirectFlight:
                CancelAction();
                directFlight = !directFlight;
                break;
            case DashboardAction.FlyDirection:
                CancelAction();
                flyDirection = flyDirection == DragPivot.Headset ? DragPivot.RightController : flyDirection + 1;
                break;
            case DashboardAction.FlySpeedDown:
            case DashboardAction.FlySpeedUp:
                flySpeed = Math.Clamp(flySpeed + (action == DashboardAction.FlySpeedUp ? .25m : -.25m), .25m, 30m);
                if (flyTopSpeed > 0) flyTopSpeed = Math.Max(flySpeed, flyTopSpeed);
                break;
            case DashboardAction.FlyTopSpeedDown:
            case DashboardAction.FlyTopSpeedUp:
                flyTopSpeed = FlightSteering.AdjustTopSpeed(flyTopSpeed, flySpeed, action == DashboardAction.FlyTopSpeedUp);
                break;
            case DashboardAction.FlyPitchDown:
            case DashboardAction.FlyPitchUp:
                flyPitchOffset = Math.Clamp(flyPitchOffset + (action == DashboardAction.FlyPitchUp ? 1 : -1),
                    -FlightSteering.MaxOffset, FlightSteering.MaxOffset);
                break;
            case DashboardAction.FlyYawDown:
            case DashboardAction.FlyYawUp:
                flyYawOffset = Math.Clamp(flyYawOffset + (action == DashboardAction.FlyYawUp ? 1 : -1),
                    -FlightSteering.MaxOffset, FlightSteering.MaxOffset);
                break;
            case DashboardAction.FlySmoothingDown:
            case DashboardAction.FlySmoothingUp:
                flyRotationSmoothing = Math.Clamp(flyRotationSmoothing + (action == DashboardAction.FlySmoothingUp ? 5 : -5), 0, 100);
                break;
            case DashboardAction.FlyAccelerationDown:
            case DashboardAction.FlyAccelerationUp:
                flyAcceleration = Math.Clamp(flyAcceleration + (action == DashboardAction.FlyAccelerationUp ? .25m : -.25m), 0m, 30m);
                break;
            case DashboardAction.SpaceDrag: SelectMode(TeleportMode.SpaceDrag); break;
            case DashboardAction.ControllerTurn: SelectMode(TeleportMode.ControllerTurn); break;
            case DashboardAction.Custom: SelectMode(TeleportMode.Custom); break;
            case DashboardAction.CustomRecord: ToggleRecording(); break;
            case DashboardAction.CustomDrag:
                CancelAction();
                if (!editorDrag) { editorDrag = true; editorDrag3D = false; }
                else if (!editorDrag3D) editorDrag3D = true;
                else { editorDrag = false; editorDrag3D = false; }
                break;
            case DashboardAction.SaveHeight: saveHeight = !saveHeight; break;
            case DashboardAction.SaveRotation: saveRotation = !saveRotation; break;
            case DashboardAction.LockHeight:
                lockDragHeight = !lockDragHeight;
                break;
            case DashboardAction.EditName:
                if (!naming && activePattern is { } pattern) renameName = pattern.Name ?? "";
                naming = true;
                break;
            case DashboardAction.FinishName:
                if (naming && activePattern is { } saved)
                {
                    string name = renameName.Trim();
                    if (name.Length == 0)
                    {
                        status = "Enter a pattern name.";
                        break;
                    }
                    int id = patternIds[selectedPattern];
                    var updated = saved with { Name = name };
                    patternLibrary.Save(id, updated);
                    activePattern = updated;
                    RefreshPatterns();
                    ready = status = "Pattern renamed.";
                }
                naming = false;
                break;
            case DashboardAction.Backspace:
                if (EditingName.Length > 0) EditingName = EditingName[..^1];
                break;
            case DashboardAction.CancelCustom:
                confirmDelete = false;
                break;
            case DashboardAction.DeletePattern: confirmDelete = true; CancelAction(); break;
            case DashboardAction.ConfirmDelete:
                if (selectedPattern < 0 || !confirmDelete) break;
                patternLibrary.Delete(patternIds[selectedPattern]);
                SelectMode(TeleportMode.Custom);
                RefreshPatterns();
                ready = status = "Pattern deleted.";
                break;
            case DashboardAction.PageUp: patternPage++; break;
            case DashboardAction.PageDown: patternPage--; break;
            case DashboardAction.EditPoints when activePattern is not null:
                CancelAction();
                editPoint = 0;
                break;
            case DashboardAction.DonePoints:
                editPoint = -1;
                break;
            case DashboardAction.AddPoint when editPoint >= 0 && activePattern is { Points.Length: < 256 } savedPoints:
                var appended = savedPoints.AppendPoint();
                patternLibrary.Save(patternIds[selectedPattern], appended);
                activePattern = appended;
                editPoint = Math.Max(0, appended.Points.Length - DashboardPanel.PointRows);
                break;
            case DashboardAction.CustomSmooth:
            case DashboardAction.CustomIntervalDown:
            case DashboardAction.CustomIntervalUp:
            case DashboardAction.CustomRepeat:
            case DashboardAction.CustomOrder:
                ChangePatternSettings(action);
                break;
            case >= DashboardAction.PatternRow and < DashboardAction.PatternRow + DashboardPanel.PatternsPerPage:
                OpenPattern(patternPage * DashboardPanel.PatternsPerPage + (action - DashboardAction.PatternRow));
                break;
            case DashboardAction.Toggle:
                EndSpaceTurn();
                CancelAction();
                teleportEnabled = !teleportEnabled;
                status = teleportEnabled ? ready : PausedStatus();
                break;
            case DashboardAction.Reset: ResetPlaySpace(); break;
            case DashboardAction.PatternTransition when mode == TeleportMode.Normal:
                turnSettings = turnSettings with { SmoothTransition = !turnSettings.SmoothTransition };
                break;
            case DashboardAction.PatternTransition when mode == TeleportMode.Sweep:
                sweepSettings = sweepSettings with { SmoothTransition = !sweepSettings.SmoothTransition };
                break;
            case DashboardAction.PatternTransition when mode == TeleportMode.Circle:
                settings = settings with { SmoothTransition = !settings.SmoothTransition };
                break;
            case DashboardAction.RadiusDown or DashboardAction.RadiusUp or DashboardAction.PointsDown or
                DashboardAction.PointsUp or DashboardAction.IntervalDown or DashboardAction.IntervalUp or DashboardAction.Order or
                DashboardAction.SweepPointsDown or DashboardAction.SweepPointsUp or DashboardAction.RowDistanceDown or DashboardAction.RowDistanceUp
                when mode == TeleportMode.Sweep:
                sweepSettings = action switch
                {
                    DashboardAction.RadiusDown => sweepSettings with { Width = Math.Max(.25m, sweepSettings.Width - .25m) },
                    DashboardAction.RadiusUp => sweepSettings with { Width = Math.Min(1000m, sweepSettings.Width + .25m) },
                    DashboardAction.PointsDown => sweepSettings with { Rows = Math.Max(1, sweepSettings.Rows - 1) },
                    DashboardAction.PointsUp => sweepSettings with { Rows = Math.Min(SweepControls.MaxRows, sweepSettings.Rows + 1) },
                    DashboardAction.SweepPointsDown => sweepSettings with { PointsPerRow = Math.Max(2, sweepSettings.PointsPerRow - 1) },
                    DashboardAction.SweepPointsUp => sweepSettings with { PointsPerRow = Math.Min(32, sweepSettings.PointsPerRow + 1) },
                    DashboardAction.RowDistanceDown => sweepSettings with { RowDistance = SweepControls.SnapRowDistance(sweepSettings.EffectiveRowDistance - SweepControls.RowDistanceStep) },
                    DashboardAction.RowDistanceUp => sweepSettings with { RowDistance = SweepControls.SnapRowDistance(sweepSettings.EffectiveRowDistance + SweepControls.RowDistanceStep) },
                    DashboardAction.Order => sweepSettings with { Random = !sweepSettings.Random },
                    DashboardAction.IntervalDown => sweepSettings with { IntervalMs = Math.Max(10, sweepSettings.IntervalMs - 10) },
                    _ => sweepSettings with { IntervalMs = Math.Min(60000, sweepSettings.IntervalMs + 10) }
                };
                break;
            case DashboardAction.RadiusDown: settings = settings with { Radius = Math.Max(.25m, settings.Radius - .25m) }; break;
            case DashboardAction.RadiusUp: settings = settings with { Radius = Math.Min(1000m, settings.Radius + .25m) }; break;
            case DashboardAction.PointsDown: settings = settings with { Points = Math.Max(3, settings.Points - 1) }; break;
            case DashboardAction.PointsUp: settings = settings with { Points = Math.Min(360, settings.Points + 1) }; break;
            case DashboardAction.IntervalDown or DashboardAction.IntervalUp when mode == TeleportMode.Normal:
                if (turnSettings.SmoothTransition)
                    turnSettings = turnSettings with { TransitionMs = Math.Clamp(turnSettings.TransitionMs +
                        (action == DashboardAction.IntervalUp ? 10 : -10), 10, 60000) };
                break;
            case DashboardAction.IntervalDown: settings = settings with { IntervalMs = Math.Max(10, settings.IntervalMs - 10) }; break;
            case DashboardAction.IntervalUp: settings = settings with { IntervalMs = Math.Min(60000, settings.IntervalMs + 10) }; break;
            case DashboardAction.Order: settings = settings with { Random = !settings.Random }; break;
            case DashboardAction.GravityDown:
            case DashboardAction.GravityUp:
                ref int gravity = ref (mode == TeleportMode.Fly ? ref flightGravity : ref (RegularDragSettings ? ref spaceDragGravity : ref gravity3D));
                bool increase = action == DashboardAction.GravityUp;
                gravity = increase
                    ? Math.Min(100, gravity + (gravity < 10 ? 1 : 10))
                    : gravity > 10 ? Math.Max(10, gravity - 10) : Math.Max(-100, gravity - 1);
                break;
            case DashboardAction.ShowDragGrids:
                showRegularDragCeiling = RegularDragSettings;
                showDragGrids = !showDragGrids;
                if (!showDragGrids)
                {
                    wallFadeUntil = Environment.TickCount64;
                    ceilingPreviewUntil = wallFadeUntil;
                }
                break;
            case DashboardAction.DragWalls:
                CancelAction();
                placingWalls = !placingWalls && walls.Count < 4;
                walls.Clear();
                wallFadeUntil = Environment.TickCount64;
                ready = status = placingWalls ? "Aim the laser at the floor and press R3 to place a wall." : "Walls cleared.";
                break;
            case DashboardAction.KeepDragActive:
                keepSpaceDragActive = !keepSpaceDragActive;
                CancelAction();
                break;
            case DashboardAction.DragRotate:
                CancelAction();
                spaceDragRotate = !spaceDragRotate;
                break;
            case DashboardAction.DragHeightLock:
                spaceDragLockHeight = !spaceDragLockHeight;
                spaceDragFalling = false;
                break;
            case DashboardAction.CeilingToggle:
                ref bool selectedCeiling = ref (RegularDragSettings ? ref spaceDragCeilingBounce : ref ceilingBounce);
                selectedCeiling = !selectedCeiling;
                ceilingPreviewCm = RegularDragSettings ? spaceDragCeilingCm : ceilingCm;
                ceilingPreviewUntil = selectedCeiling ? Environment.TickCount64 + 5500 : Environment.TickCount64;
                break;
            case DashboardAction.CeilingDown:
            case DashboardAction.CeilingUp:
                ref int height = ref (RegularDragSettings ? ref spaceDragCeilingCm : ref ceilingCm);
                height = Math.Clamp(height + (action == DashboardAction.CeilingUp ? 10 : -10), 50, 2000);
                ceilingPreviewCm = height;
                ceilingPreviewUntil = Environment.TickCount64 + 5500;
                break;
            case DashboardAction.TurnStrengthDown: turnStrength = Math.Max(0m, turnStrength - .25m); break;
            case DashboardAction.TurnStrengthUp: turnStrength = Math.Min(int.MaxValue / 4m, turnStrength + .25m); break;
            case DashboardAction.TurnDragDown: turnDragSteps = Math.Max(1, turnDragSteps - 1); break;
            case DashboardAction.TurnDragUp: turnDragSteps = Math.Min(400, turnDragSteps + 1); break;
            case DashboardAction.FloorOffsetDown:
            case DashboardAction.FloorOffsetUp:
                ref int offset = ref (RegularDragSettings ? ref spaceDragFloorOffsetCm : ref floorOffsetCm);
                offset = Math.Clamp(offset + (action == DashboardAction.FloorOffsetUp ? 5 : -5), 0, 300);
                break;
            case DashboardAction.Bounce:
                if (RegularDragSettings) spaceDragFloorBounce = !spaceDragFloorBounce;
                else floorBounce = !floorBounce;
                break;
            case DashboardAction.ResistanceLock:
                resistanceLocked = !resistanceLocked;
                if (resistanceLocked) spinResistance = glideResistance;
                break;
            case DashboardAction.SpinDown: spinResistance = Math.Max(0, spinResistance - 10); if (resistanceLocked) glideResistance = spinResistance; break;
            case DashboardAction.SpinUp: spinResistance = Math.Min(100, spinResistance + 10); if (resistanceLocked) glideResistance = spinResistance; break;
            case DashboardAction.ResistanceDown:
                if (mode == TeleportMode.Fly) flightResistance = Math.Max(0, flightResistance - 10);
                else if (RegularDragSettings) spaceDragResistance = Math.Clamp(spaceDragResistance - 10, 0, 100);
                else { glideResistance = Math.Max(0, glideResistance - 10); if (resistanceLocked) spinResistance = glideResistance; }
                break;
            case DashboardAction.ResistanceUp:
                if (mode == TeleportMode.Fly) flightResistance = Math.Min(100, flightResistance + 10);
                else if (RegularDragSettings) spaceDragResistance = Math.Clamp(spaceDragResistance + 10, 0, 100);
                else { glideResistance = Math.Min(100, glideResistance + 10); if (resistanceLocked) spinResistance = glideResistance; }
                break;
            case DashboardAction.MultiplierDown: dragSettings = dragSettings with { Multiplier = Math.Max(.25m, dragSettings.Multiplier - .25m) }; break;
            case DashboardAction.MultiplierUp: dragSettings = dragSettings with { Multiplier = dragSettings.Multiplier + .25m }; break;
            case DashboardAction.StrengthDown:
                dragSettings = dragSettings with
                {
                    AccelerationStrength = Math.Max(0m, dragSettings.AccelerationStrength - .25m)
                };
                break;
            case DashboardAction.StrengthUp:
                dragSettings = dragSettings with
                {
                    AccelerationStrength = Math.Min(100m, dragSettings.AccelerationStrength + .25m)
                };
                break;
            case DashboardAction.TurnDown: turnSettings = turnSettings with { Multiplier = Math.Max(.25m, turnSettings.Multiplier - .25m) }; break;
            case DashboardAction.TurnUp: turnSettings = turnSettings with { Multiplier = turnSettings.Multiplier + .25m }; break;
            case DashboardAction.ToggleTurn:
                turnSettings = turnSettings with { Enabled = !turnSettings.Enabled };
                vr?.ReleaseTurn();
                lastTurnTick = Environment.TickCount64;
                CancelAction();
                if (normalTeleported) status = ready = NormalLandingStatus();
                break;
        }
        if (settingsFile is not null && before != CurrentSettings)
            saveSettingsAt = Environment.TickCount64 + 250;
    }

    private string PausedStatus() => mode switch
    {
        TeleportMode.Circle => "Circle Teleport paused.",
        TeleportMode.Sweep => "Sweep Teleport paused.",
        TeleportMode.ControllerTurn => "3D Space Drag paused.",
        TeleportMode.SpaceDrag => "Space Drag paused.",
        TeleportMode.Fly => "Flight Mode paused.",
        _ => "Teleport paused."
    };

    private void RefreshPatterns()
    {
        var entries = patternLibrary.Read().OrderBy(pair => pair.Key).ToArray();
        patternIds = entries.Select(pair => pair.Key).ToArray();
        patternNames = entries.Select(pair => pair.Value.Name ?? $"Pattern {pair.Key}").ToArray();
        patternPage = Math.Min(patternPage, Math.Max(0, (patternIds.Length - 1) / DashboardPanel.PatternsPerPage));
    }

    private void OpenPattern(int index)
    {
        if (index < 0 || index >= patternIds.Length) return;
        var saved = patternLibrary.Load(patternIds[index]);
        if (saved is null) return;
        SelectMode(TeleportMode.Custom);
        CancelAction();
        selectedPattern = index;
        activePattern = saved with { Name = patternNames[index] };
        naming = confirmDelete = false;
        ready = status = "Ready to play.";
    }

    private void ChangePatternSettings(DashboardAction action)
    {
        if (activePattern is not { } pattern) return;
        var updated = action switch
        {
            DashboardAction.CustomSmooth => pattern with { SmoothTransition = !pattern.SmoothTransition },
            DashboardAction.CustomRepeat => pattern with { Repeat = !pattern.Repeat },
            DashboardAction.CustomOrder => pattern with { RandomOrder = !pattern.RandomOrder },
            DashboardAction.CustomIntervalUp => pattern with { IntervalMs = Math.Min(60000, pattern.IntervalMs + 10) },
            DashboardAction.CustomIntervalDown => pattern with { IntervalMs = Math.Max(10, pattern.IntervalMs - 10) },
            _ => pattern
        };
        if (updated == pattern) return;
        patternLibrary.Save(patternIds[selectedPattern], updated);
        if (customPlayback is not null) CancelAction();
        activePattern = updated;
    }

    private void SetPointScroll(int first)
    {
        if (editPoint < 0 || activePattern is not { } saved) return;
        int clamped = Math.Clamp(first, 0, Math.Max(0, saved.Points.Length - DashboardPanel.PointRows));
        if (clamped == editPoint) return;
        if (editingCell.HasValue) FinishPointInput(pointInput.Visible ? pointInput.Text : null);
        editPoint = clamped;
        RefreshPanel();
    }

    private void MovePointScroll(float y)
    {
        if (editPoint < 0 || activePattern is not { } saved || saved.Points.Length <= DashboardPanel.PointRows) return;
        var track = DashboardPanel.PointScrollTrack;
        var thumb = DashboardPanel.PointScrollThumb(editPoint, saved.Points.Length);
        float fraction = Math.Clamp((y - track.Top - pointThumbOffset) / (track.Height - thumb.Height), 0, 1);
        SetPointScroll((int)Math.Round(fraction * (saved.Points.Length - DashboardPanel.PointRows)));
    }

    private void BeginPointInput(int cell, bool inVr)
    {
        if (activePattern is not { } saved || editPoint < 0) return;
        int row = cell / DashboardPanel.PointColumns, field = cell % DashboardPanel.PointColumns;
        int index = editPoint + row;
        if (index >= saved.Points.Length) return;
        if (editingCell is not null) FinishPointInput(pointInput.Visible ? pointInput.Text : null);
        editingCell = (index, field);
        string text = saved.CellText(index, field);
        if (inVr)
        {
            if (vr?.ShowNameKeyboard(text, "Point value (number or dash)") != 0)
                FinishPointInput(null);
            return;
        }
        var cellBounds = DashboardPanel.PointCellBounds(row, field);
        float sx = screen.Width / (float)DashboardPanel.Width, sy = screen.Height / (float)DashboardPanel.Height;
        pointInput.Bounds = new Rectangle((int)(cellBounds.Left * sx), (int)(cellBounds.Top * sy + 3),
            (int)(cellBounds.Width * sx), pointInput.PreferredHeight);
        pointInput.Text = text;
        pointInput.Show();
        pointInput.Focus();
        pointInput.SelectAll();
    }

    private void FinishPointInput(string? text)
    {
        if (editingCell is not { } cell) return;
        editingCell = null;
        pointInput.Hide();
        if (text is null || activePattern is not { } saved) return;
        text = text.Trim();
        if (text == saved.CellText(cell.Point, cell.Field)) return;
        float? value = null;
        if (text.Length > 0 && text != "-")
        {
            var styles = System.Globalization.NumberStyles.Float;
            if ((!float.TryParse(text, styles, System.Globalization.CultureInfo.CurrentCulture, out float number) &&
                !float.TryParse(text, styles, System.Globalization.CultureInfo.InvariantCulture, out number)) ||
                !float.IsFinite(number) || MathF.Abs(number) > 100000)
            {
                ready = status = "Enter a valid number or a dash.";
                RefreshPanel();
                return;
            }
            value = cell.Field == 6 ? Math.Clamp(MathF.Round(number), 10, 60000) : number;
        }

        var rows = Enumerable.Range(0, saved.Points.Length)
            .Select(index => Enumerable.Range(0, DashboardPanel.PointColumns)
                .Select(field => saved.CellValue(index, field)).ToArray()).ToArray();
        rows[cell.Point][cell.Field] = value;
        var points = (Vector3[])saved.Points.Clone();
        if (cell.Field < 3 && value is float coordinate)
            points[cell.Point][cell.Field] = coordinate;
        var updated = saved with { Points = points, EditedValues = rows };
        patternLibrary.Save(patternIds[selectedPattern], updated);
        activePattern = updated;
        ready = status = "Point saved.";
        RefreshPanel();
    }

    private void ToggleRecording()
    {
        if (recording)
        {
            if (customPoints.Count == 0) { recording = false; ready = status = "No points captured."; return; }
            SaveRecording();
            return;
        }
        recording = true;
        ready = status = "Recording. Click L3 to capture each point.";
    }

    private void SaveRecording()
    {
        string name = draftName.Trim();
        int id = patternIds.Length == 0 ? 1 : patternIds.Max() + 1;
        if (name.Length == 0) name = $"Pattern {id}";
        Vector3[] points = saveHeight ? customPoints.ToArray() : customPoints.Select(point => point with { Y = 0 }).ToArray();
        patternLibrary.Save(id, new SavedPattern(points, SavedPattern.DefaultIntervalMs, name, saveRotation ? customAngles.Select(angles => angles.Y).ToArray() : null)
        {
            UseRecordedHeight = saveHeight,
            EditedValues = points.Select((point, index) =>
            {
                const float degrees = 180 / MathF.PI;
                var angles = customAngles[index] * degrees;
                return new float?[] { point.X, saveHeight ? point.Y : null, point.Z,
                    saveRotation ? angles.Y : null, saveRotation ? angles.X : null, saveRotation ? angles.Z : null, null };
            }).ToArray()
        });
        RefreshPatterns();
        int index = Array.IndexOf(patternIds, id);
        patternPage = index / DashboardPanel.PatternsPerPage;
        recording = naming = false;
        OpenPattern(index);
        draftName = "";
        customPoints.Clear();
        customAngles.Clear();
        ready = status = "Pattern saved.";
    }

    private void CaptureCustomPoint()
    {
        var (head, _) = vr!.Poses(0);
        if (!head.Tracked) { ready = status = "Headset tracking is unavailable."; return; }
        if (customPoints.Count == 256) { ready = status = "Recording is full. Finish to save."; return; }
        customPoints.Add(head.Matrix.Position);
        customAngles.Add(PlaySpace.HeadAngles(head.Matrix));
        ready = status = $"Captured point {customPoints.Count}.";
    }

    private void HandlePointer(DashboardAction action, bool down, bool inVr)
    {
        if (!down && colorDragChannel >= 0 && colorDragInVr == inVr)
        {
            colorDragChannel = -1;
            return;
        }
        int colorValue = action - DashboardAction.CenterColorChannel;
        if ((uint)colorValue < DashboardPanel.ColorChannelCount * DashboardPanel.ColorChannelValues)
        {
            if (down)
            {
                colorDragChannel = colorValue / DashboardPanel.ColorChannelValues;
                colorDragInVr = inVr;
            }
            HandleAction(action);
            return;
        }
        if (!down && draggingPointScroll)
        {
            draggingPointScroll = false;
            return;
        }
        if (action == DashboardAction.PointScroll && activePattern is { } scrollPattern)
        {
            if (down)
            {
                float y = inVr ? vrPointer?.Y ?? 0 : desktopPointer is Point mouse ? DesktopPanelPoint(mouse).Y : 0;
                var thumb = DashboardPanel.PointScrollThumb(editPoint, scrollPattern.Points.Length);
                pointThumbOffset = y >= thumb.Top && y < thumb.Bottom ? y - thumb.Top : thumb.Height / 2f;
                draggingPointScroll = true;
                pointScrollInVr = inVr;
                MovePointScroll(y);
            }
            return;
        }
        bool step = action is DashboardAction.RadiusDown or DashboardAction.RadiusUp or
            DashboardAction.PointsDown or DashboardAction.PointsUp or DashboardAction.IntervalDown or DashboardAction.IntervalUp or
            DashboardAction.FlyPitchDown or DashboardAction.FlyPitchUp or DashboardAction.FlyYawDown or DashboardAction.FlyYawUp or
            DashboardAction.FlyTopSpeedDown or DashboardAction.FlyTopSpeedUp or DashboardAction.FlyAccelerationDown or DashboardAction.FlyAccelerationUp or DashboardAction.FlySmoothingDown or DashboardAction.FlySmoothingUp or
            DashboardAction.FlySpeedDown or DashboardAction.FlySpeedUp or DashboardAction.MultiplierDown or DashboardAction.MultiplierUp or DashboardAction.TurnDown or DashboardAction.TurnUp or
            DashboardAction.StrengthDown or DashboardAction.StrengthUp or
            DashboardAction.CustomIntervalDown or DashboardAction.CustomIntervalUp or DashboardAction.SweepPointsDown or DashboardAction.SweepPointsUp or DashboardAction.RowDistanceDown or DashboardAction.RowDistanceUp or
            DashboardAction.GravityDown or DashboardAction.GravityUp or DashboardAction.ResistanceDown or DashboardAction.ResistanceUp or
            DashboardAction.SpinDown or DashboardAction.SpinUp or DashboardAction.TurnDragDown or DashboardAction.TurnDragUp or
            DashboardAction.TurnStrengthDown or DashboardAction.TurnStrengthUp or DashboardAction.FloorOffsetDown or DashboardAction.FloorOffsetUp or
            DashboardAction.CeilingDown or DashboardAction.CeilingUp;
        if (down)
        {
            if (!step) return;
            repeatedStep = action;
            repeatingInVr = inVr;
            repeatAt = Environment.TickCount64 + 300;
            HandleAction(action);
            return;
        }
        if (repeatedStep != DashboardAction.None && repeatingInVr == inVr)
        {
            repeatedStep = DashboardAction.None;
            return;
        }
        if (step) return;
        int cell = action - DashboardAction.PointCell;
        if (cell >= 0 && cell < DashboardPanel.PointRows * DashboardPanel.PointColumns)
        {
            BeginPointInput(cell, inVr);
            return;
        }
        if (inVr && action == DashboardAction.Bindings) desktopBindingsNoticeUntil = Environment.TickCount64 + 8000;
        HandleAction(action);
        if (inVr && action == DashboardAction.EditName && vr is not null && vr.ShowNameKeyboard(EditingName) != 0)
        {
            naming = false;
            ready = status = "SteamVR keyboard could not open. Select the name to retry.";
            RefreshPanel();
        }
    }

    private void RepeatStep()
    {
        if (repeatedStep == DashboardAction.None) return;
        bool validMode = repeatedStep switch
        {
            _ when settingsOpen => false,
            DashboardAction.MultiplierDown or DashboardAction.MultiplierUp => mode == TeleportMode.SpaceDrag,
            DashboardAction.StrengthDown or DashboardAction.StrengthUp => RegularDragSettings,
            DashboardAction.FlyPitchDown or DashboardAction.FlyPitchUp or DashboardAction.FlyYawDown or DashboardAction.FlyYawUp =>
                mode == TeleportMode.Fly && flyDirection != DragPivot.Headset,
            DashboardAction.FlySpeedDown or DashboardAction.FlySpeedUp or DashboardAction.FlyTopSpeedDown or DashboardAction.FlyTopSpeedUp or
                DashboardAction.FlyAccelerationDown or DashboardAction.FlyAccelerationUp or DashboardAction.FlySmoothingDown or DashboardAction.FlySmoothingUp => mode == TeleportMode.Fly,
            DashboardAction.IntervalDown or DashboardAction.IntervalUp when mode == TeleportMode.Normal =>
                turnSettings.SmoothTransition,
            DashboardAction.TurnDown or DashboardAction.TurnUp => mode == TeleportMode.Normal && turnSettings.Enabled,
            DashboardAction.CustomIntervalDown or DashboardAction.CustomIntervalUp => mode == TeleportMode.Custom,
            DashboardAction.GravityDown or DashboardAction.GravityUp or DashboardAction.ResistanceDown or DashboardAction.ResistanceUp or
                DashboardAction.FloorOffsetDown or DashboardAction.FloorOffsetUp or DashboardAction.CeilingDown or DashboardAction.CeilingUp => IsDragMode,
            DashboardAction.SpinDown or DashboardAction.SpinUp or DashboardAction.TurnDragDown or DashboardAction.TurnDragUp => mode == TeleportMode.ControllerTurn,
            DashboardAction.TurnStrengthDown or DashboardAction.TurnStrengthUp => mode == TeleportMode.ControllerTurn,
            _ => mode is TeleportMode.Circle or TeleportMode.Sweep
        };
        if (!validMode || (repeatingInVr && (!dashboardAvailable || vr?.DashboardActive != true)))
        {
            repeatedStep = DashboardAction.None;
            return;
        }
        long now = Environment.TickCount64;
        if (now < repeatAt) return;
        HandleAction(repeatedStep);
        repeatAt = now + 90;
    }

    private async Task ChangeDriverRegistration()
    {
        await rotationDriver.ChangeRegistration();
        if (!closing && !IsDisposed) RefreshPanel();
    }

    private void ResetPlaySpace()
    {
        EndSpaceTurn();
        CancelAction();
        rotationDriver.Reset();
        ready = status = space?.Reset() ?? "Play space is already at its original position.";
        displayedOffset = Vector3.Zero;
    }

    private async Task ConnectAsync()
    {
        connecting = true;
        RefreshPanel();
        var connection = Task.Run(() =>
        {
            var runtime = new OpenVr();
            try
            {
                var playSpace = new PlaySpace(runtime);
                return (Runtime: runtime, Space: playSpace, Status: playSpace.Reset() ?? "");
            }
            catch
            {
                runtime.Dispose();
                throw;
            }
        });

        try
        {
            if (await Task.WhenAny(connection, Task.Delay(TimeSpan.FromSeconds(5))) != connection && !closing)
            {
                connectionStatus = "SteamVR is taking longer than expected to respond.";
                RefreshPanel();
            }
            var result = await connection;
            if (closing)
            {
                result.Runtime.Dispose();
                return;
            }
            vr = result.Runtime;
            autoLaunch = vr.GetAutoLaunch();
            space = result.Space;
            ready = status = result.Status;
            try
            {
                vr.CreateDashboard();
                dashboardAvailable = true;
                RefreshPanel();
                dashboardDirty = true;
                UploadPanel();
            }
            catch (Exception exception)
            {
                dashboardAvailable = false;
                ready = status = exception.Message;
            }
            timer.Start();
        }
        catch (Exception)
        {
            if (closing) return;
            connectionStatus = "Waiting for SteamVR.";
        }
        finally
        {
            connecting = false;
            nextConnectAttempt = Environment.TickCount64 + 3000;
        }
        if (!closing) RefreshPanel();
    }

    private static bool SteamVrServerRunning()
    {
        using var current = System.Diagnostics.Process.GetCurrentProcess();
        bool running = false;
        foreach (var process in System.Diagnostics.Process.GetProcessesByName("vrserver"))
        {
            using (process)
            {
                try { running |= process.SessionId == current.SessionId && !process.HasExited; }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { return true; }
            }
        }
        return running;
    }

    private void Tick()
    {
        if (autoConnect) rotationDriver.Poll();
        if (autoConnect && Environment.TickCount64 >= nextRuntimeCheck)
        {
            nextRuntimeCheck = Environment.TickCount64 + 1000;
            bool running = SteamVrServerRunning();
            if (runtimeSeen && !running)
            {
                quitRequested = true;
                Close();
                return;
            }
            runtimeSeen |= running;
        }
        UpdateDesktopHover();
        RepeatStep();
        if (Environment.TickCount64 >= saveSettingsAt) SaveSettings();
        if (vr is null || space is null)
        {
            if (autoConnect && !closing && !connecting && runtimeSeen && Environment.TickCount64 >= nextConnectAttempt)
                _ = ConnectAsync();
            return;
        }
        if (vr.QuitRequested())
        {
            quitRequested = true;
            Close();
            return;
        }
        vr.ShowPlaySpaceCenter(showPlaySpaceCenter, playSpaceCenterColor, playSpaceCenterTransparency, playSpaceCenterSize, scalePlaySpaceCenterWithDistance);
        if (placingWalls ? vr.DashboardVisible || !IsDragMode : showDragGrids || wallFadeUntil != 0)
        {
            float opacity = placingWalls || showDragGrids ? 1 : Math.Clamp((wallFadeUntil - Environment.TickCount64) / 500f, 0, 1);
            vr.ShowWalls(walls, mode == TeleportMode.Fly ? 0 : opacity);
            if (opacity == 0) wallFadeUntil = 0;
        }
        if (mode == TeleportMode.Fly) vr.ShowCeiling(0, 0);
        if (showDragGrids && mode != TeleportMode.Fly)
        {
            int height = showRegularDragCeiling ? spaceDragCeilingCm : ceilingCm;
            vr.ShowCeiling(height / 100f, 1);
            vr.ShowFloor(0f);
        }
        else
        {
            vr.ShowFloor(null);
        }
        if (mode != TeleportMode.Fly && !showDragGrids && ceilingPreviewUntil != 0)
        {
            long remaining = ceilingPreviewUntil - Environment.TickCount64;
            vr.ShowCeiling(ceilingPreviewCm / 100f, Math.Clamp(remaining / 500f, 0, 1));
            if (remaining <= 0) ceilingPreviewUntil = 0;
        }
        vr.ShowCustomGuide(recording && mode == TeleportMode.Custom && activePattern is null ? customPoints : null, null, clearAimOnly: true);
        if (vr.DashboardVisible)
        {
            vr.ReleaseTurn();
            lastTurnTick = Environment.TickCount64;
            CancelAction();
            status = mode == TeleportMode.Custom ? ready : !teleportEnabled ? PausedStatus() : "Close the SteamVR dashboard to use R3.";
            if (dashboardAvailable && (vr.DashboardActive || vr.NameKeyboardOpen))
            {
                for (int i = 0; i < 128 && vr.TryReadDashboardPointer(out var point, out var down, out var name, out bool keyboardClosed); i++)
                {
                    if (keyboardClosed && editingCell.HasValue)
                        FinishPointInput(name);
                    else if (keyboardClosed && naming)
                    {
                        if (name is not null)
                        {
                            EditingName = name;
                            HandleAction(DashboardAction.FinishName);
                        }
                        else naming = false;
                    }
                    vrPointer = point is PointF p && p.X >= 0 && p.X < DashboardPanel.Width &&
                        p.Y >= 0 && p.Y < DashboardPanel.Height ? Point.Truncate(p) : null;
                    if (down is null && colorDragChannel >= 0 && colorDragInVr && vrPointer is Point colorPointer)
                        HandleAction(DashboardPanel.CenterChannelAction(colorDragChannel, colorPointer.X));
                    if (draggingPointScroll && pointScrollInVr && vrPointer is Point scrollPointer)
                        MovePointScroll(scrollPointer.Y);
                    if (point is PointF click && down is bool pressed)
                        HandlePointer(DashboardPanel.HitTest(click, State), pressed, inVr: true);
                }
                RefreshPanel();
                UploadPanel();
            }
            else vrPointer = null;
            return;
        }
        if (vr.NameKeyboardOpen)
        {
            vr.HideNameKeyboard();
            FinishPointInput(null);
            naming = false;
        }
        vrPointer = null;
        if (pointScrollInVr) draggingPointScroll = false;
        long now = Environment.TickCount64;
        float turnSeconds = Math.Clamp((now - lastTurnTick) / 1000f, 0, .06f);
        lastTurnTick = now;
        bool turnActive = teleportEnabled && normalTeleported && turnSettings.Enabled;
        var (action, resetButton, stick, bound) = vr.ReadControls(turnActive);
        if (placingWalls && IsDragMode && mode != TeleportMode.Fly)
        {
            UpdateWallPlacement(action.Origin, action.JustPressed);
            return;
        }
        if (resetButton.Held)
        {
            if (resetButton.JustPressed && (action.Held || gesture.InProgress))
            {
                CancelAction();
                ready = status = "R3 action cancelled.";
            }
            else if (mode == TeleportMode.Custom && activePattern is null && recording)
            {
                if (resetButton.JustPressed) CaptureCustomPoint();
            }
            else if (resetButton.JustPressed) ResetPlaySpace();
            else CancelAction();
            return;
        }
        if (!teleportEnabled && mode != TeleportMode.Custom && !BackgroundDragEnabled)
        {
            status = PausedStatus();
            return;
        }
        if (normalTransition is { } transition)
        {
            if (action.Held || action.Active == 0 || !vr.RawHeadPose().Tracked)
            {
                CancelAction();
                status = ready;
                return;
            }

            float progress = Math.Clamp((now - transition.Started) / (float)transition.DurationMs, 0, 1);
            space.Drag(transition.Anchor, transition.Movement * progress);
            if (progress >= 1)
            {
                normalTransition = null;
                normalTeleported = true;
                ready = status = NormalLandingStatus();
            }
            return;
        }
        if (pattern is not null)
        {
            if (action.Held || action.Active == 0)
            {
                CancelAction();
                status = ready;
            }
            else AdvancePattern(Environment.TickCount64);
            return;
        }
        if (customPlayback is not null)
        {
            if (action.Held || action.Active == 0) { CancelAction(); status = ready; }
            else AdvanceCustom(now);
            return;
        }

        bool circle = mode == TeleportMode.Circle;
        bool sweep = mode == TeleportMode.Sweep;
        bool custom = mode == TeleportMode.Custom;
        bool wasAiming = gesture.InProgress;
        var (origin, release) = gesture.Update(action);
        if (BackgroundDragEnabled)
        {
            if (origin == 0)
            {
                if (routingDrag && drag.HasValue) UpdateDrag(0, false);
                routingDrag = false;
                ApplySpaceDragGravity();
            }
            else if (!release && !routingDrag && !wasAiming)
            {
                var (head, controller) = vr.Poses(origin);
                if (head.Tracked && controller.Tracked)
                    routingDrag = !teleportEnabled || PlaySpace.Aim(head.Matrix, controller.Matrix, circle || sweep) is not { CanTeleport: true };
                spaceDragFalling = false;
            }

            if (routingDrag)
            {
                HideTarget();
                UpdateDrag(origin, release);
                if (release) routingDrag = false;
                return;
            }
            if (!teleportEnabled)
            {
                status = PausedStatus();
                return;
            }
        }
        if (custom)
        {
            if (activePattern is { } saved && !confirmDelete && !naming && editPoint < 0)
            {
                if (release)
                {
                    Vector3? customStart = shownTarget;
                    HideTarget();
                    if (customStart is Vector3 start)
                    {
                        customPlayback = new CustomPlayback(saved, start);
                        AdvanceCustom(now);
                    }
                    else status = "Aim at the floor to place the pattern.";
                }
                else if (origin != 0)
                {
                    var (head, controller) = vr.Poses(origin);
                    var aim = head.Tracked && controller.Tracked ? PlaySpace.Aim(head.Matrix, controller.Matrix, true) : null;
                    vr.Preview(aim, head.Matrix.Position);
                    shownTarget = aim is { CanTeleport: true } valid ? valid.FloorPoint : null;
                    vr.ShowCustomGuide(shownTarget.HasValue ? saved.Points : null, shownTarget);
                    status = shownTarget.HasValue ? "Release to start the pattern." : "Aim at the floor to place the pattern.";
                }
                else { HideTarget(); status = ready; }
            }
            else if (activePattern is null && editorDrag && !naming)
            {
                if (editorDrag3D) UpdateControllerTurn(origin, release);
                else UpdateDrag(origin, release);
            }
            else status = ready;
            return;
        }
        if (mode == TeleportMode.Fly)
        {
            UpdateFly(origin, release, now);
            return;
        }
        if (mode == TeleportMode.SpaceDrag)
        {
            UpdateDrag(origin, release);
            return;
        }
        if (mode == TeleportMode.ControllerTurn)
        {
            UpdateControllerTurn(origin, release);
            return;
        }
        if (origin == 0)
        {
            HideTarget();
            if (turnActive && bound && !action.Held) UpdateSpaceTurn(stick, turnSeconds);
            status = action.Active == 0 ? "Waiting for a controller binding." : ready;
            return;
        }
        if (!release)
        {
            var (head, controller) = vr.Poses(origin);
            AimPreview? aim = head.Tracked && controller.Tracked ? PlaySpace.Aim(head.Matrix, controller.Matrix, circle || sweep) : null;
            vr.Preview(aim, head.Matrix.Position, circle ? settings : null, sweep ? sweepSettings : null);
            shownTarget = aim is { CanTeleport: true } valid ? valid.FloorPoint : null;
            shownHeadPosition = head.Matrix.Position;
            status = shownTarget.HasValue ? (circle || sweep ? "Release to start the pattern." : "Release to teleport.") :
                aim?.FloorPoint is not null ? "Aim farther from your feet." : "Aim down at the tracking floor.";
            return;
        }

        Vector3? previewed = shownTarget;
        HideTarget();
        if (previewed is Vector3 destination)
        {
            var (head, _) = vr.Poses(0);
            if (circle || sweep)
            {
                if (!head.Tracked)
                {
                    ready = status = "Pattern cancelled. Headset tracking was lost.";
                    return;
                }
                var anchor = space.CurrentCalibration();
                pattern = sweep
                    ? new FloorPattern(anchor, destination, shownHeadPosition, sweepSettings)
                    : new FloorPattern(anchor, destination, shownHeadPosition,
                        new CircleSettings((float)settings.Radius, settings.Points, settings.Random,
                            TimeSpan.FromMilliseconds(settings.IntervalMs), settings.SmoothTransition));
                AdvancePattern(Environment.TickCount64);
                return;
            }
            Vector3? movement = head.Tracked ? PlaySpace.ReleaseMovement(head.Matrix, destination) : null;
            if (movement is Vector3 jump)
            {
                if (turnSettings.SmoothTransition)
                {
                    EndSpaceTurn();
                    normalTransition = (space.CurrentCalibration(), jump, Environment.TickCount64, turnSettings.TransitionMs);
                    ready = "Moving to target. Press R3 to stop.";
                }
                else
                {
                    space.Jump(jump);
                    normalTeleported = true;
                    ready = NormalLandingStatus();
                }
            }
            else ready = "Teleport cancelled. Tracking was lost or the destination is too close.";
        }
        status = ready;
    }

    private string NormalLandingStatus() => turnSettings.Enabled
        ? "Right stick turns. R3 aims again. L3 resets."
        : "R3 aims again. L3 resets.";

    private void UpdateSpaceTurn(float stick, float seconds)
    {
        float yaw = turnSettings.Angle(stick, seconds);
        if (yaw == 0) return;
        var head = vr!.RawHeadPose();
        if (head.Tracked) space!.Turn(head.Matrix.Position, yaw);
    }

    private void EndSpaceTurn()
    {
        normalTeleported = false;
        vr?.ReleaseTurn();
    }

    private void UpdateWallPlacement(ulong origin, bool place)
    {
        var (head, controller) = vr!.Poses(origin, allowAnyAimSource: true);
        var aim = head.Tracked && controller.Tracked ? PlaySpace.Aim(head.Matrix, controller.Matrix, true) : null;
        Vector3 normal = controller.Matrix.Forward with { Y = 0 };
        bool stableHeading = normal.LengthSquared() >= .01f;
        bool valid = aim is { CanTeleport: true, FloorPoint: not null } && stableHeading;
        if (aim.HasValue) vr.Preview(aim.Value with { CanTeleport = valid }, head.Matrix.Position);
        else vr.Preview(null);

        DragWall? preview = null;
        if (head.Tracked && controller.Tracked)
        {
            if (!stableHeading)
            {
                Vector3 right = controller.Matrix.Rotate(Vector3.UnitX) with { Y = 0 };
                normal = Vector3.Cross(Vector3.UnitY, right);
            }
            normal = Vector3.Normalize(normal);
            Vector3 target = aim?.FloorPoint ?? controller.Matrix.Position + normal * 3f;
            Vector3 center = target with { Y = 4f };
            if (Vector3.Dot(head.Matrix.Position - center, normal) < 0) normal = -normal;
            preview = new DragWall(center, normal);
        }
        vr.ShowWalls(walls, 1, preview);
        if (!place) return;

        if (!valid || preview is not DragWall wallToPlace)
        {
            ready = status = "Aim the laser at the floor to place a wall.";
            return;
        }

        if (walls.Any(wall => Vector3.Dot(wall.Normal, wallToPlace.Normal) > .995f && MathF.Abs(wall.Distance(wallToPlace.Center)) < .05f))
        {
            ready = status = "That wall is already placed.";
            return;
        }

        walls.Add(wallToPlace);
        vr.ShowWalls(walls, 1);
        if (walls.Count == 4)
        {
            placingWalls = false;
            HideTarget();
            wallFadeUntil = Environment.TickCount64 + 500;
        }
        ready = status = walls.Count == 4 ? "Four walls placed." : $"Wall {walls.Count} of 4 placed.";
    }
    private void ReleaseSpaceDrag()
    {
        if (!rotationDriver.Holding) return;
        rotationDriver.Finish(false, glideResistance, spinResistance,
            space?.CurrentCalibration().Origin, floorOffsetCm / 100f, gravity3D, floorBounce, ceilingBounce ? ceilingCm / 100f : 0, walls.Count == 4 ? walls : null);
    }
    private void UpdateControllerTurn(ulong origin, bool release)
    {
        if (rotationDriver.Ready && !drag.HasValue && (!spaceDragFalling || origin != 0 && !release))
        {
            spaceDragFalling = false;
            if (release) { ReleaseSpaceDrag(); ready = status = "Released. L3 resets."; }
            else if (origin != 0) { rotationDriver.Hold(vr!.ControllerIndex(origin), turnDragSteps, (int)(4 * turnStrength), vr.RotationPivotIndex(rotationPivot)); status = "3D Space Drag active."; }
            else ReleaseSpaceDrag();
            return;
        }

        if (rotationDriver.Holding)
        {
            CancelAction();
            return;
        }
        UpdateDrag(origin, release);
    }

    private void UpdateFly(ulong origin, bool release, long now)
    {
        if (release || origin == 0)
        {
            if (flyStarted != 0)
            {
                flyStarted = 0;
                vr?.ShowDirectionLine(null);
                spaceDragFalling = release;
                hoverTick = release && idleHoverBobbing ? now : 0;
                hoverPhase = 0;
                if (!release)
                {
                    dragVelocity = Vector3.Zero;
                    dragYawVelocity = 0;
                }
                verticalSpeed = 0;
                gravityTick = flySample;
                ready = "Flight released.";
            }
            float bobDelta = 0;
            if (hoverTick != 0)
            {
                float previous = FlightSteering.HoverOffset(hoverPhase, hoverBobbingIntensity);
                hoverPhase = (hoverPhase + Math.Clamp((now - hoverTick) / 1000f, 0, .06f)) % 4f;
                hoverTick = now;
                bobDelta = FlightSteering.HoverOffset(hoverPhase, hoverBobbingIntensity) - previous;
                status = "Hovering. Hold R3 to fly.";
            }
            else status = ready;
            ApplySpaceDragGravity(applyGravity: hoverTick == 0, bobDelta: bobDelta);
            return;
        }

        var anchor = space!.CurrentCalibration();
        var directionPose = vr!.FlightDirectionPose(flyDirection);
        if (!directionPose.Tracked)
        {
            CancelAction();
            status = flyDirection == DragPivot.Headset ? "Headset is not tracked." : "Fly controller tip pose unavailable. Check tracking and the Aim binding.";
            return;
        }

        var controller = directFlight ? vr.RawDevicePose(vr.ControllerIndex(origin)) : default;
        var head = flyDirection == DragPivot.Headset ? directionPose : vr.RawHeadPose();
        var targetForward = Vector3.Normalize(flyDirection == DragPivot.Headset
            ? directionPose.Matrix.Forward
            : FlightSteering.ControllerDirection(directionPose.Matrix, flyPitchOffset, flyYawOffset));
        float heading = controller.Tracked ? PlaySpace.Heading(controller.Matrix) : flyHeading;
        if (flyStarted == 0)
        {
            float carriedSpeed = spaceDragFalling ? dragVelocity.Length() : 0;
            flyEntrySpeed = flyAcceleration > 0 && carriedSpeed > .001f ? carriedSpeed : (float)flySpeed;
            flyStarted = spaceDragFalling ? gravityTick : now;
            flySample = flyStarted;
            hoverTick = 0;
            float neutralHeading = PlaySpace.Heading(head.Tracked ? head.Matrix : directionPose.Matrix);
            flyNeutralForward = Vector3.Transform(-Vector3.UnitZ, Quaternion.CreateFromAxisAngle(Vector3.UnitY, neutralHeading));
            flyForward = targetForward;
            flyTurnRate = 0;
            flyTurnTracked = false;
            spaceDragFalling = false;
            verticalSpeed = dragYawVelocity = 0;
            dragVelocity = Vector3.Zero;
        }

        float seconds = Math.Clamp((now - flySample) / 1000f, 0, .06f);
        flySample = now;
        float blend = FlightSteering.Blend(flyRotationSmoothing, seconds);
        flyForward = FlightSteering.SmoothDirection(flyForward, targetForward, blend);
        Vector3 rawDirection = flyForward;
        if (!directFlight)
        {
            // The physical neutral axis stays fixed while the play space turns around it.
            flyTurnRate += (FlightSteering.PlaneTurnRate(flyNeutralForward, targetForward) - flyTurnRate) * blend;
            float climb = Math.Clamp(flyForward.Y, -1, 1);
            rawDirection = flyNeutralForward * MathF.Sqrt(1 - climb * climb) + Vector3.UnitY * climb;
        }
        Vector3 direction = anchor.Origin.Unrotate(rawDirection);
        float speed = flyEntrySpeed + (float)flyAcceleration * ((now - flyStarted) / 1000f);
        if (flyTopSpeed > 0) speed = Math.Min((float)flyTopSpeed, speed);
        dragVelocity = direction.LengthSquared() > .000001f ? Vector3.Normalize(direction) * speed : Vector3.Zero;
        float yaw = directFlight
            ? (controller.Tracked && flyTurnTracked ? MathF.IEEERemainder(heading - flyHeading, 2 * MathF.PI) * blend : 0)
            : flyTurnRate * seconds;
        flyHeading = controller.Tracked && flyTurnTracked ? flyHeading + yaw : heading;
        flyTurnTracked = controller.Tracked;
        if (seconds > 0) dragYawVelocity = yaw / seconds;
        if (yaw != 0)
        {
            var pivot = directFlight ? vr.RawDevicePose(vr.RotationPivotIndex(rotationPivot)) : head;
            dragYawPivot = pivot.Tracked ? pivot.Matrix.Position :
                directFlight ? controller.Matrix.Position : directionPose.Matrix.Position;
        }
        Vector3 movement = dragVelocity * seconds;
        var currentOrigin = anchor.Origin;
        if (movement != Vector3.Zero || yaw != 0)
        {
            space.Drag(anchor, movement, yaw, dragYawPivot);
            currentOrigin = space.CurrentCalibration().Origin;
            dragVelocity = currentOrigin.Unrotate(anchor.Origin.Rotate(dragVelocity));
        }
        if (showFlightDirectionLine)
        {
            const float lineLength = .375f;
            Vector3 forward = currentOrigin.Unrotate(directFlight ? flyForward : targetForward);
            Vector3 start = currentOrigin.Unrotate(directionPose.Matrix.Position - currentOrigin.Position);
            Vector3 eye = currentOrigin.Unrotate(head.Matrix.Position - currentOrigin.Position);
            if (flyDirection == DragPivot.Headset)
                start += forward * .25f + currentOrigin.Unrotate(head.Matrix.Rotate(-Vector3.UnitY)) * .15f;
            vr.ShowDirectionLine(new AimPreview(start, forward, start + forward * lineLength, false),
                eye, Color.FromArgb(unchecked((int)0xff000000) | playSpaceCenterColor));
        }
        else vr.ShowDirectionLine(null);
        status = "Flying. Release R3 to let go.";
    }

    private void UpdateDrag(ulong origin, bool release)
    {
        bool fallback3D = mode == TeleportMode.ControllerTurn || mode == TeleportMode.Custom && editorDrag3D;
        bool regularDrag = mode == TeleportMode.SpaceDrag || BackgroundDragEnabled || fallback3D;
        var controls = fallback3D ? new DragControls(turnDragSteps * .25m) { AccelerationStrength = turnStrength } : dragSettings;
        if (origin == 0 || release)
        {
            if (drag.HasValue)
            {
                if (release && space is { } currentSpace && dragTranslationRemainder != Vector3.Zero)
                {
                    var anchor = currentSpace.CurrentCalibration();
                    currentSpace.Drag(anchor, anchor.Origin.Unrotate(dragTranslationRemainder));
                }
                dragTranslationRemainder = Vector3.Zero;
                ready = "Space Drag released.";
                spaceDragFalling = release && regularDrag;
                if (!spaceDragFalling || Environment.TickCount64 - drag.Value.At > 120)
                {
                    dragVelocity = Vector3.Zero;
                    dragYawVelocity = 0;
                }
                verticalSpeed = 0;
                gravityTick = Environment.TickCount64;
            }
            ApplySpaceDragGravity();
            drag = null;
            status = ready;
            return;
        }

        var (_, controller) = vr!.Poses(origin, raw: true);
        if (!controller.Tracked)
        {
            CancelAction();
            ready = status = "Space Drag stopped. Controller tracking was lost.";
            return;
        }

        spaceDragFalling = false;
        Vector3 position = controller.Matrix.Position;
        long now = Environment.TickCount64;
        float heading = PlaySpace.Heading(controller.Matrix);
        if (!drag.HasValue)
        {
            dragVelocity = Vector3.Zero;
            dragHeading = heading;
            dragTranslationRemainder = Vector3.Zero;
            dragYawVelocity = 0;
        }
        drag ??= (space!.CurrentCalibration(), position, Vector3.Zero, now);
        var current = drag.Value;
        if (fallback3D || regularDrag && spaceDragRotate)
        {
            float yaw = MathF.IEEERemainder(heading - dragHeading, 2 * MathF.PI);
            var anchor = space!.CurrentCalibration();
            float seconds = Math.Max(.001f, (now - current.At) / 1000f);
            Vector3 delta = controls.Scale(anchor.Origin.Unrotate(current.Previous - position), seconds);
            if (!fallback3D && spaceDragLockHeight) delta.Y = 0;
            dragVelocity = Vector3.Lerp(dragVelocity, delta / seconds, 1 - MathF.Exp(-seconds / .035f));
            if (MathF.Abs(yaw) < .0001f) yaw = 0;
            float blend = 1 - MathF.Exp(-seconds / .035f);
            dragYawVelocity += (yaw / seconds - dragYawVelocity) * blend;
            uint pivotIndex = vr.RotationPivotIndex(rotationPivot);
            var pivotPose = pivotIndex == vr.ControllerIndex(origin) ? controller : vr.RawDevicePose(pivotIndex);
            dragYawPivot = pivotPose.Tracked ? pivotPose.Matrix.Position : position;
            if (delta != Vector3.Zero || yaw != 0 || dragTranslationRemainder != Vector3.Zero)
            {
                space.Drag(anchor, SmoothDragTranslation(anchor, delta, seconds), yaw, dragYawPivot);
                dragVelocity = space.CurrentCalibration().Origin.Unrotate(anchor.Origin.Rotate(dragVelocity));
                dragHeading = heading;
            }
            drag = (current.Anchor, position, Vector3.Zero, now);
        }
        else if (current.Previous != position || dragTranslationRemainder != Vector3.Zero)
        {
            Vector3 movement = current.Anchor.Origin.Unrotate(current.Previous - position);
            float seconds = Math.Max(.001f, (now - current.At) / 1000f);
            Vector3 delta = controls.Scale(movement, seconds);
            if ((mode == TeleportMode.Custom && lockDragHeight) || (regularDrag && !fallback3D && spaceDragLockHeight)) delta.Y = 0;
            dragVelocity = Vector3.Lerp(dragVelocity, delta / seconds, 1 - MathF.Exp(-seconds / .035f));
            Vector3 total = current.Movement + SmoothDragTranslation(current.Anchor, delta, seconds);
            space!.Drag(current.Anchor, total);
            drag = (current.Anchor, position, total, now);
        }
        status = "Space Drag active. Release to let go.";
    }

    private Vector3 SmoothDragTranslation(CalibrationSnapshot anchor, Vector3 delta, float seconds)
    {
        Vector3 pending = dragTranslationRemainder + anchor.Origin.Rotate(delta);
        Vector3 applied = pending * (1 - MathF.Exp(-seconds / .016f));
        dragTranslationRemainder = pending - applied;
        if (dragTranslationRemainder.LengthSquared() < 1e-10f)
        {
            applied += dragTranslationRemainder;
            dragTranslationRemainder = Vector3.Zero;
        }
        return anchor.Origin.Unrotate(applied);
    }

    private void ApplySpaceDragGravity(bool applyGravity = true, float bobDelta = 0)
    {
        if (!spaceDragFalling || space is null) return;
        long now = Environment.TickCount64;
        float seconds = Math.Clamp((now - gravityTick) / 1000f, 0, .06f);
        gravityTick = now;
        if (seconds == 0) return;

        bool fallback3D = mode == TeleportMode.ControllerTurn || mode == TeleportMode.Custom && editorDrag3D;
        bool flight = mode == TeleportMode.Fly;
        int linearResistance = flight ? flightResistance : fallback3D ? glideResistance : spaceDragResistance;
        int angularResistance = fallback3D ? spinResistance : linearResistance;
        var (linearTime, linearRemaining) = DragDecay(linearResistance, seconds, flight);
        var (angularTime, angularRemaining) = DragDecay(angularResistance, seconds, flight);
        Vector3 delta = dragVelocity * linearTime + new Vector3(0, bobDelta, 0);
        float yaw = dragYawVelocity * angularTime;
        dragVelocity *= linearRemaining;
        dragYawVelocity *= angularRemaining;
        int gravity = applyGravity ? (flight ? flightGravity : fallback3D ? gravity3D : spaceDragGravity) : 0;
        bool bounceFloor = mode != TeleportMode.Fly && (fallback3D ? floorBounce : spaceDragFloorBounce);
        bool bounceCeiling = mode != TeleportMode.Fly && (fallback3D ? ceilingBounce : spaceDragCeilingBounce);
        bool lockHeight = mode != TeleportMode.Fly && !fallback3D && spaceDragLockHeight;
        if (lockHeight) delta.Y = 0;
        else
        {
            float acceleration = -9.80665f * gravity / 100f;
            delta.Y += (verticalSpeed + .5f * acceleration * seconds) * seconds;
            verticalSpeed += acceleration * seconds;
            if (bounceFloor || bounceCeiling)
                ApplySpaceDragBounce(space.CurrentCalibration().Origin, ref delta.Y, acceleration, linearResistance, bounceFloor, bounceCeiling,
                    fallback3D ? floorOffsetCm : spaceDragFloorOffsetCm, fallback3D ? ceilingCm : spaceDragCeilingCm);

        }
        if (mode != TeleportMode.Fly && walls.Count == 4 && vr!.RawHeadPose() is { Tracked: true } head)
        {
            Matrix34 origin = space.CurrentCalibration().Origin;
            Vector3 position = origin.Unrotate(head.Matrix.Position - origin.Position);
            foreach (var wall in walls)
            {
                float toward = Vector3.Dot(delta, wall.Normal);
                float distance = wall.Distance(position);
                if (toward >= 0 || distance + toward > 0) continue;
                delta -= 2 * (Math.Max(0, distance) + toward) * wall.Normal;
                float inward = Vector3.Dot(dragVelocity, wall.Normal);
                if (inward < 0) dragVelocity -= 2 * inward * wall.Normal;
            }
        }
        if (!lockHeight && !bounceFloor && gravity > 0 && delta.Y <= 0)
        {
            // Keep the gravity landing at the resting play-space height and absorb all vertical momentum.
            float dropRemaining = Math.Max(0, space.Offset.Y);
            if (-delta.Y >= dropRemaining)
            {
                delta.Y = -dropRemaining;
                verticalSpeed = 0;
                dragVelocity.Y = 0;
            }
        }
        if (delta != Vector3.Zero || yaw != 0) space.Drag(space.CurrentCalibration(), delta, yaw, dragYawPivot);
    }

    private static (float TravelTime, float Remaining) DragDecay(int resistance, float seconds, bool flight = false)
    {
        if (resistance == 100 && !flight) return (0, 0);
        if (resistance == 0) return (seconds, 1);
        float damping = -MathF.Log(Math.Max(.001f, 1 - resistance / 100f));
        float remaining = MathF.Exp(-damping * seconds);
        return ((1 - remaining) / damping, remaining);
    }
    private void ApplySpaceDragBounce(Matrix34 origin, ref float movement, float acceleration, int resistance, bool bounceFloor, bool bounceCeiling, int floorCm, int ceilingHeightCm)
    {
        if (movement == 0 || vr is null) return;
        var head = vr.RawHeadPose();
        if (!head.Tracked) return;

        float height = origin.Unrotate(head.Matrix.Position - origin.Position).Y;
        bool towardFloor = movement < 0;
        float threshold = towardFloor ? floorCm / 100f : ceilingHeightCm / 100f;
        bool enabled = movement < 0 ? bounceFloor : bounceCeiling;
        float nextHeight = height + movement;
        if (!enabled || (movement < 0 ? nextHeight > threshold : nextHeight < threshold)) return;

        movement = towardFloor ? Math.Min(0, threshold - height) : Math.Max(0, threshold - height);
        float speed = verticalSpeed + dragVelocity.Y;
        // Remove the speed gained below the floor or above the ceiling this frame.
        float excess = Math.Max(0, 2 * acceleration * (nextHeight - threshold));
        float impactSpeed = MathF.Sqrt(Math.Max(0, speed * speed - excess));
        bool approaching = towardFloor ? speed < 0 : speed > 0;
        if (!approaching) return;
        float minimumImpact = resistance == 0 ? .001f : .12f;
        float restitution = resistance == 0 ? 1f : .95f;
        float reflection = impactSpeed > minimumImpact ? -restitution * impactSpeed / Math.Abs(speed) : 0;
        verticalSpeed *= reflection;
        dragVelocity.Y *= reflection;
    }
    private static Vector3 RoundPose(Vector3 value, int decimals) =>
        new(MathF.Round(value.X, decimals), MathF.Round(value.Y, decimals), MathF.Round(value.Z, decimals));

    private void RefreshPanel()
    {
        long now = Environment.TickCount64;
        if (now >= nextOffsetSample)
        {
            displayedRotation = null;
            headsetPose = null;
            if (space is not null)
            {
                var (translation, rotation) = rotationDriver.ReadOffset();
                var applied = space.AppliedPose(translation, rotation);
                displayedOffset = RoundPose(applied.Position, 2);
                float degrees = 180f / MathF.PI;
                displayedRotation = RoundPose(new Vector3(
                    MathF.Asin(Math.Clamp(applied.Forward.Y, -1, 1)),
                    PlaySpace.Heading(applied), MathF.Atan2(applied.YX, applied.YY)) * degrees, 1);
            }
            if (vr is not null && mode == TeleportMode.Custom && activePattern is null && !settingsOpen)
            {
                var (head, _) = vr.Poses(0);
                if (head.Tracked)
                {
                    var position = head.Matrix.Position;
                    headsetPose = $"Headset   X {position.X:0.00}   Y {position.Y:0.00}   Z {position.Z:0.00} m";
                }
            }
            nextOffsetSample = now + 100;
        }
        if (vr is null) headsetPose = null;
        var next = State;
        if (next == rendered) return;
        panel.Render(next);
        screen.Invalidate();
        dashboardDirty = true;
        rendered = next;
    }

    private void UploadPanel()
    {
        Rectangle bounds = vrPointer is Point position ? DashboardPanel.HitTarget(position, State).Bounds : Rectangle.Empty;
        long now = Environment.TickCount64;
        if (bounds != hoverBounds)
        {
            hoverBounds = bounds;
            hoverStarted = now;
        }
        byte opacity = HoverOpacity(bounds, now - hoverStarted);
        var pointer = new DashboardPointerFrame(vrPointer, bounds, opacity);
        if (!dashboardDirty && pointer == uploadedPointer) return;
        vr!.UploadDashboard(panel.CopyBgra(pointer));
        dashboardDirty = false;
        uploadedPointer = pointer;
    }

    private void AdvancePattern(long now)
    {
        if (pattern is not { } run || (!run.SmoothTransition && !run.Due(now))) return;
        var head = vr!.RawHeadPose();
        if (!head.Tracked)
        {
            CancelAction();
            ready = status = "Pattern stopped. Headset tracking was lost.";
            return;
        }
        Vector3 landing = run.SmoothTransition ? run.SampleTransition(now, head.Matrix) : run.TakePoint(now);
        space!.JumpInPattern(run.Anchor, run.TransitionHead ?? head.Matrix, landing, run.Center, run.MovesVertically);
        ready = status = mode == TeleportMode.Sweep ? "Sweep running. Press R3 to stop." : "Circle running. Press R3 to stop.";
    }

    private void AdvanceCustom(long now)
    {
        if (customPlayback is not { } run || (!run.SmoothTransition && !run.Due(now))) return;
        var head = vr!.RawHeadPose();
        if (!head.Tracked)
        {
            CancelAction();
            ready = status = "Pattern stopped. Headset tracking was lost.";
            return;
        }
        Vector3 point;
        float? yaw, pitch, roll;
        int index;
        bool complete;
        var raw = run.TransitionHead ?? head.Matrix;
        var standing = space!.StandingPose(raw);
        if (run.SmoothTransition)
        {
            Vector3 angles;
            (point, angles, index, complete) = run.Sample(now, standing, raw);
            yaw = run.YawAt(index).HasValue ? angles.Y : null;
            pitch = run.AngleAt(index, 4).HasValue ? angles.X : null;
            roll = run.AngleAt(index, 5).HasValue ? angles.Z : null;
        }
        else
        {
            (point, index) = run.TakePoint(now);
            point = run.ResolvePoint(point, index, standing.Position);
            yaw = run.YawAt(index);
            pitch = run.AngleAt(index, 4);
            roll = run.AngleAt(index, 5);
            complete = run.Finished;
        }
        if (pitch.HasValue || roll.HasValue)
            space.JumpToRecordedOrientation(point, pitch, yaw, roll, raw, run.HasHeight(index));
        else if (yaw is float heading)
            space.JumpToRecordedPose(point, heading, raw, run.HasHeight(index));
        else
            space.JumpToRecordedPoint(point, raw.Position, run.HasHeight(index));
        if (complete)
        {
            customPlayback = null;
            ready = status = "Pattern finished. Hold R3 to aim again.";
            return;
        }
        ready = status = $"Playing point {index}/{run.Count}. Press R3 to stop.";
    }

    private void HideTarget()
    {
        vr?.Preview(null);
        if (!recording) vr?.ShowCustomGuide(null, null);
        shownTarget = null;
    }

    private void CancelAction()
    {
        flyStarted = hoverTick = 0;
        dragTranslationRemainder = Vector3.Zero;
        routingDrag = false;
        spaceDragFalling = false;
        rotationDriver.StopMotion();
        if (pattern is not null) ready = mode == TeleportMode.Sweep ? "Sweep stopped." : "Circle stopped.";
        if (customPlayback is not null) ready = "Pattern stopped.";
        if (drag.HasValue) ready = "Space Drag released.";
        if (normalTransition.HasValue) ready = "Smooth transition stopped.";
        normalTransition = null;
        pattern = null;
        customPlayback = null;
        if ((mode == TeleportMode.ControllerTurn || mode == TeleportMode.Custom && editorDrag3D) && drag is { } cancelled) space?.RestorePreview(cancelled.Anchor);
        drag = null;
        gesture.Cancel();

        HideTarget();
    }

    private void Disconnect()
    {
        rotationDriver.Reset();
        if (closing) timer.Stop();
        vrPointer = null;
        repeatedStep = DashboardAction.None;
        try
        {
            EndSpaceTurn();
            CancelAction();
            space?.EndPreview();
        }
        catch (Exception exception)
        {
            status = exception.Message;
        }
        finally
        {
            vr?.Dispose();
            vr = null;
            space = null;
            dashboardAvailable = false;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            rotationDriver.Dispose();
            tray.Dispose();
            trayMenu.Dispose();
            timer.Dispose();
            screen.Image = null;
            panel.Dispose();
        }
        base.Dispose(disposing);
    }

    private void Run(Action action)
    {
        try { action(); }
        catch (Exception exception)
        {
            try { EndSpaceTurn(); CancelAction(); }
            catch (InvalidOperationException) { shownTarget = null; }
            ready = status = exception.Message;
        }
        if (!closing) RefreshPanel();
    }
}
