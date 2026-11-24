using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;

[StructLayout(LayoutKind.Sequential)]
internal readonly record struct Matrix34(
    float XX, float XY, float XZ, float TX,
    float YX, float YY, float YZ, float TY,
    float ZX, float ZY, float ZZ, float TZ)
{
    public Vector3 Position => new(TX, TY, TZ);
    public Vector3 Forward => new(-XZ, -YZ, -ZZ);
    public Vector3 Rotate(Vector3 value) => new(
        XX * value.X + XY * value.Y + XZ * value.Z,
        YX * value.X + YY * value.Y + YZ * value.Z,
        ZX * value.X + ZY * value.Y + ZZ * value.Z);

    public Vector3 Unrotate(Vector3 value) => new(
        XX * value.X + YX * value.Y + ZX * value.Z,
        XY * value.X + YY * value.Y + ZY * value.Z,
        XZ * value.X + YZ * value.Y + ZZ * value.Z);

    public static Matrix34 FromBasis(Vector3 x, Vector3 y, Vector3 z, Vector3 position) =>
        new(x.X, y.X, z.X, position.X, x.Y, y.Y, z.Y, position.Y, x.Z, y.Z, z.Z, position.Z);

    public Matrix34 Translate(Vector3 delta) => this with
    {
        TX = TX + delta.X, TY = TY + delta.Y, TZ = TZ + delta.Z
    };

    public bool Matches(Matrix34 other) =>
        Vector3.DistanceSquared(Position, other.Position) < .000004f &&
        Vector3.DistanceSquared(Rotate(Vector3.UnitX), other.Rotate(Vector3.UnitX)) < .000004f &&
        Vector3.DistanceSquared(Rotate(Vector3.UnitY), other.Rotate(Vector3.UnitY)) < .000004f &&
        Vector3.DistanceSquared(Rotate(Vector3.UnitZ), other.Rotate(Vector3.UnitZ)) < .000004f;
}

internal readonly record struct DragWall(Vector3 Center, Vector3 Normal)
{
    public float Distance(Vector3 point) => Vector3.Dot(point - Center, Normal);
}

internal sealed record CalibrationSnapshot(Matrix34 Origin, Vector3[] Corners, Matrix34? SeatedOrigin = null)
{
    public CalibrationSnapshot MoveHead(Matrix34 rawHead, Vector3 landing, Vector3 center)
    {
        Vector3 head = Origin.Unrotate(rawHead.Position - Origin.Position);
        Vector3 forward = Origin.Unrotate(rawHead.Forward);
        Vector3 inward = center - landing;
        float currentYaw = MathF.Atan2(-forward.X, -forward.Z);
        float targetYaw = MathF.Atan2(-inward.X, -inward.Z);
        if (forward.X * forward.X + forward.Z * forward.Z < .000001f)
        {
            Vector3 right = Origin.Unrotate(rawHead.Rotate(Vector3.UnitX));
            currentYaw = MathF.Atan2(-right.Z, right.X);
        }
        return Reposition(rawHead.Position, head, landing, targetYaw - currentYaw);
    }

    public CalibrationSnapshot TurnAroundHead(Vector3 rawHead, float yaw)
    {
        Vector3 head = Origin.Unrotate(rawHead - Origin.Position);
        return Reposition(rawHead, head, head, yaw);
    }

    public CalibrationSnapshot TurnAroundHead(Vector3 rawHead, Quaternion turn)
    {
        Vector3 head = Origin.Unrotate(rawHead - Origin.Position);
        return Reposition(rawHead, head, head, turn);
    }

    public CalibrationSnapshot FollowControllerRotation(Vector3 rawHead, Matrix34 initial, Matrix34 current)
    {
        Vector3 MapAxis(Vector3 axis) => Origin.Unrotate(current.Rotate(initial.Unrotate(Origin.Rotate(axis))));
        Vector3 x = MapAxis(Vector3.UnitX), y = MapAxis(Vector3.UnitY), z = MapAxis(Vector3.UnitZ);
        var basis = new Matrix4x4(
            x.X, x.Y, x.Z, 0,
            y.X, y.Y, y.Z, 0,
            z.X, z.Y, z.Z, 0,
            0, 0, 0, 1);
        var turn = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(basis));
        Vector3 head = Origin.Unrotate(rawHead - Origin.Position);
        return Reposition(rawHead, head, head, turn);
    }

    private CalibrationSnapshot Reposition(Vector3 rawHead, Vector3 head, Vector3 landing, float yaw) =>
        Reposition(rawHead, head, landing, Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw));

    private CalibrationSnapshot Reposition(Vector3 rawHead, Vector3 head, Vector3 landing, Quaternion turn)
    {
        var inverse = Quaternion.Conjugate(turn);
        landing.Y = head.Y;

        Vector3 NewToRawDirection(Vector3 direction) => Origin.Rotate(Vector3.Transform(direction, inverse));
        Vector3 RebaseRawDirection(Vector3 direction) => NewToRawDirection(Origin.Unrotate(direction));
        Vector3 MapPoint(Vector3 point) => landing + Vector3.Transform(point - head, turn);
        var standing = Matrix34.FromBasis(NewToRawDirection(Vector3.UnitX), NewToRawDirection(Vector3.UnitY),
            NewToRawDirection(Vector3.UnitZ), rawHead - NewToRawDirection(landing));
        Matrix34? seated = SeatedOrigin is Matrix34 pose
            ? Matrix34.FromBasis(RebaseRawDirection(pose.Rotate(Vector3.UnitX)), RebaseRawDirection(pose.Rotate(Vector3.UnitY)),
                RebaseRawDirection(pose.Rotate(Vector3.UnitZ)), standing.Position + RebaseRawDirection(pose.Position - Origin.Position))
            : null;
        return new(standing, Corners.Select(MapPoint).ToArray(), seated);
    }

    public CalibrationSnapshot Shift(Vector3 movement, bool allowHeight = false)
    {
        if (!allowHeight) movement.Y = 0;
        // Moving the tracking origin opposite the jump advances the tracked user.
        // Moving the bounds in standing space by the jump keeps them fixed in raw space.
        Vector3 rawOffset = -Origin.Rotate(movement);
        Matrix34? seated = SeatedOrigin;
        if (seated is Matrix34 pose)
        {
            Vector3 up = pose.Rotate(Vector3.UnitY);
            seated = pose.Translate(allowHeight ? rawOffset : rawOffset - up * Vector3.Dot(up, rawOffset));
        }
        return new(Origin.Translate(rawOffset),
            Corners.Select(corner => corner + movement).ToArray(), seated);
    }

    public bool Matches(CalibrationSnapshot other) =>
        Origin.Matches(other.Origin) && Corners.Length == other.Corners.Length &&
        Corners.Zip(other.Corners).All(pair =>
            Vector3.DistanceSquared(pair.First, pair.Second) < .000004f);
}

[method: System.Text.Json.Serialization.JsonConstructor]
internal readonly record struct DragControls(decimal Multiplier)
{
    public static DragControls Default => new(1);
    public decimal AccelerationStrength { get; init; }

    public Vector3 Scale(Vector3 delta, float seconds)
    {
        float speed = delta.Length() / Math.Max(.001f, seconds);
        float gain = 1f + .75f * Math.Min(speed, 3f) * (float)AccelerationStrength;
        return delta * ((float)Multiplier * gain);
    }
}

internal readonly record struct TurnControls(bool Enabled, decimal Multiplier, bool SmoothTransition = false, int TransitionMs = 500)
{
    public static TurnControls Default => new(true, 1);

    public float Angle(float stick, float seconds)
    {
        if (!Enabled || !float.IsFinite(stick)) return 0;
        float amount = Math.Clamp((Math.Abs(stick) - .2f) / .8f, 0, 1);
        return -MathF.CopySign(amount, stick) * MathF.PI * seconds * (float)Multiplier;
    }
}

internal static class FlightSteering
{
    public const int MaxOffset = 90;

    public static Vector3 ControllerDirection(Matrix34 pose, int pitch, int yaw)
    {
        float elevation = pitch * (MathF.PI / 180);
        float azimuth = yaw * (MathF.PI / 180);
        float horizontal = MathF.Cos(elevation);
        return pose.Rotate(new Vector3(
            horizontal * MathF.Sin(azimuth),
            MathF.Sin(elevation),
            -horizontal * MathF.Cos(azimuth)));
    }

    public static float PlaneTurnRate(Vector3 neutral, Vector3 pointing)
    {
        Vector3 right = Vector3.Cross(neutral, Vector3.UnitY);
        float lateral = Vector3.Dot(pointing, right);
        float forward = Vector3.Dot(pointing, neutral);
        if (lateral * lateral + forward * forward < .000001f) return 0;

        const float deadZone = 5 * MathF.PI / 180;
        const float fullTurn = 45 * MathF.PI / 180;
        float angle = MathF.Atan2(-lateral, forward);
        float amount = Math.Clamp((MathF.Abs(angle) - deadZone) / (fullTurn - deadZone), 0, 1);
        return MathF.CopySign(amount * MathF.PI / 2, angle);
    }

    // Rise by the selected distance and return to the release height over four seconds.
    public static float HoverOffset(float seconds, int intensityCm) =>
        .005f * intensityCm * (1 - MathF.Cos(seconds * MathF.PI / 2));

    public static decimal AdjustTopSpeed(decimal limit, decimal startingSpeed, bool increase)
    {
        if (increase) return Math.Clamp(limit + .5m, startingSpeed, 100m);
        decimal next = limit - .5m;
        return next < startingSpeed ? 0m : next;
    }

    public static float Blend(int smoothing, float seconds) =>
        smoothing == 0 ? 1f : 1f - MathF.Exp(-seconds / (.004f * smoothing));

    public static Vector3 SmoothDirection(Vector3 current, Vector3 target, float blend)
    {
        if (blend >= 1f) return target;
        var axis = Vector3.Cross(current, target);
        float sinAngle = axis.Length();
        float cosAngle = Vector3.Dot(current, target);
        if (sinAngle < .00001f)
        {
            if (cosAngle > 0) return target;
            var perpendicular = Math.Abs(current.Y) < .9f ? Vector3.UnitY : Vector3.UnitX;
            axis = Vector3.Normalize(Vector3.Cross(current, perpendicular));
        }
        else axis /= sinAngle;
        float angle = MathF.Atan2(sinAngle, cosAngle);
        return Vector3.Transform(current, Quaternion.CreateFromAxisAngle(axis, angle * blend));
    }
}

internal readonly record struct AimPreview(Vector3 Start, Vector3 Direction, Vector3? FloorPoint, bool CanTeleport)
{
    public Vector3 PointingEnd => FloorPoint ?? Start + Direction * 8f;

    public (Matrix34 Pose, float Length)? Laser(Vector3 eye)
    {
        Vector3 end = PointingEnd;
        Vector3 ray = end - Start;
        float distance = ray.Length();
        if (!float.IsFinite(distance) || distance <= .04f) return null;
        Vector3 along = ray / distance;
        // Keep the compositor quad small enough to render reliably. The floor marker
        // and teleport destination still use the full intersection distance.
        float length = Math.Min(distance - .035f, 30f);
        Vector3 center = Start + along * (.035f + length / 2);
        Vector3 side = Vector3.Cross(eye - center, along);
        if (side.LengthSquared() < .000001f)
            side = Vector3.Cross(Math.Abs(along.Y) < .9f ? Vector3.UnitY : Vector3.UnitZ, along);
        side = Vector3.Normalize(side);
        Vector3 normal = Vector3.Cross(along, side);
        return (new Matrix34(along.X, side.X, normal.X, center.X,
                             along.Y, side.Y, normal.Y, center.Y,
                             along.Z, side.Z, normal.Z, center.Z), length);
    }
}

internal sealed class PlaySpace(
    Func<CalibrationSnapshot> readCalibration,
    Action<CalibrationSnapshot> showPreview,
    Action hidePreview,
    Action<CalibrationSnapshot> commitCalibration)
{
    public PlaySpace(OpenVr vr)
        : this(vr.ReadCalibration, vr.ShowPlaySpacePreview, vr.HidePlaySpacePreview, vr.CommitCalibration)
    {
    }

    private static readonly JsonSerializerOptions Json = new() { IncludeFields = true, IgnoreReadOnlyProperties = true };
    private static readonly string JournalPath = Path.Combine(AppContext.BaseDirectory, "play-space-recovery.json");

    private sealed record Recovery(CalibrationSnapshot Original, CalibrationSnapshot Before, CalibrationSnapshot After);
    private CalibrationSnapshot? startingCalibration;
    private CalibrationSnapshot? lastPreview;

    public Vector3 Offset => startingCalibration is { } baseline && lastPreview is { } current
        ? baseline.Origin.Unrotate(baseline.Origin.Position - current.Origin.Position)
        : Vector3.Zero;

    public Matrix34 AppliedPose(Vector3 translation, Quaternion rotation)
    {
        Matrix34 current = CurrentCalibration().Origin;
        Matrix34 baseline = startingCalibration?.Origin ?? current;
        Vector3 MapAxis(Vector3 axis) => current.Unrotate(Vector3.Transform(baseline.Rotate(axis), rotation));
        Vector3 position = current.Unrotate(Vector3.Transform(baseline.Position, rotation) + translation - current.Position);
        return Matrix34.FromBasis(MapAxis(Vector3.UnitX), MapAxis(Vector3.UnitY), MapAxis(Vector3.UnitZ), position);
    }
    public static AimPreview? Aim(Matrix34 head, Matrix34 controller, bool selectCenter = false)
    {
        Vector3 from = controller.Position;
        Vector3 direction = controller.Forward;
        float magnitude = direction.Length();
        if (!IsFinite(from) || !float.IsFinite(magnitude) || magnitude <= 0) return null;
        Vector3? floorPoint = null;
        if (from.Y > 0 && direction.Y < 0)
        {
            double time = -(double)from.Y / direction.Y;
            Vector3 hit = new((float)(from.X + direction.X * time), 0,
                (float)(from.Z + direction.Z * time));
            if (IsFinite(hit)) floorPoint = hit;
        }
        return new(from, direction / magnitude, floorPoint,
            floorPoint is Vector3 point && (selectCenter || ReleaseMovement(head, point).HasValue));
    }

    public static Vector3? ReleaseMovement(Matrix34 head, Vector3 target)
    {
        var movement = Movement(head, target);
        return IsFinite(movement) && movement.LengthSquared() >= .15f * .15f ? movement : null;
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    public static Vector3 Movement(Matrix34 head, Vector3 target) =>
        new(target.X - head.TX, 0, target.Z - head.TZ);

    public void Jump(Vector3 movement)
    {
        CalibrationSnapshot current = CurrentCalibration();
        ApplyPreview(current, current.Shift(movement));
    }

    public CalibrationSnapshot CurrentCalibration() => lastPreview ?? readCalibration();

    public void JumpToRecordedPoint(Vector3 point, Vector3 rawHead, bool allowHeight = false)
    {
        CalibrationSnapshot current = CurrentCalibration();
        Vector3 head = current.Origin.Unrotate(rawHead - current.Origin.Position);
        ApplyPreview(current, current.Shift(point - head, allowHeight));
    }

    public static float Heading(Matrix34 pose)
    {
        var forward = pose.Forward;
        if (forward.X * forward.X + forward.Z * forward.Z < .000001f)
        {
            var right = pose.Rotate(Vector3.UnitX);
            return MathF.Atan2(-right.Z, right.X);
        }
        return MathF.Atan2(-forward.X, -forward.Z);
    }

    public static Vector3 HeadAngles(Matrix34 pose) => new(
        MathF.Asin(Math.Clamp(pose.Forward.Y, -1, 1)), Heading(pose), MathF.Atan2(pose.YX, pose.YY));

    public Matrix34 StandingPose(Matrix34 raw)
    {
        var origin = CurrentCalibration().Origin;
        return Matrix34.FromBasis(origin.Unrotate(raw.Rotate(Vector3.UnitX)),
            origin.Unrotate(raw.Rotate(Vector3.UnitY)), origin.Unrotate(raw.Rotate(Vector3.UnitZ)),
            origin.Unrotate(raw.Position - origin.Position));
    }

    public void JumpToRecordedOrientation(Vector3 point, float? pitch, float? yaw, float? roll,
        Matrix34 rawHead, bool allowHeight)
    {
        var current = CurrentCalibration();
        var pose = StandingPose(rawHead);
        var angles = HeadAngles(pose);
        var before = Quaternion.CreateFromYawPitchRoll(angles.Y, angles.X, angles.Z);
        var target = Quaternion.CreateFromYawPitchRoll(yaw ?? angles.Y, pitch ?? angles.X, roll ?? angles.Z);
        var shifted = current.Shift(point - pose.Position, allowHeight);
        ApplyPreview(current, shifted.TurnAroundHead(rawHead.Position, Quaternion.Normalize(target * Quaternion.Conjugate(before))));
    }

    public void JumpToRecordedPose(Vector3 point, float yaw, Matrix34 rawHead, bool allowHeight = true)
    {
        var current = CurrentCalibration();
        var head = current.Origin.Unrotate(rawHead.Position - current.Origin.Position);
        var pose = Matrix34.FromBasis(
            current.Origin.Unrotate(rawHead.Rotate(Vector3.UnitX)),
            current.Origin.Unrotate(rawHead.Rotate(Vector3.UnitY)),
            current.Origin.Unrotate(rawHead.Rotate(Vector3.UnitZ)), head);
        var shifted = current.Shift(point - head, allowHeight);
        ApplyPreview(current, shifted.TurnAroundHead(rawHead.Position, yaw - Heading(pose)));
    }

    public void Drag(CalibrationSnapshot anchor, Vector3 movement, float yaw = 0, Vector3 controllerPivot = default)
    {
        CalibrationSnapshot next = anchor.Shift(movement, allowHeight: true);
        if (yaw != 0) next = next.TurnAroundHead(controllerPivot, yaw);
        if (!IsFinite(next.Origin.Position) || next.Corners.Any(corner => !IsFinite(corner)) ||
            (next.SeatedOrigin is Matrix34 seated && !IsFinite(seated.Position)))
            throw new InvalidOperationException("Space Drag stopped. The movement exceeded tracking coordinate precision.");
        ApplyPreview(anchor, next);
    }

    public void Turn(Vector3 rawHeadPosition, float yaw)
    {
        CalibrationSnapshot current = CurrentCalibration();
        ApplyPreview(current, current.TurnAroundHead(rawHeadPosition, yaw));
    }

    public void TurnWithController(CalibrationSnapshot anchor, Vector3 rawHead, Matrix34 initial, Matrix34 current) =>
        ApplyPreview(anchor, anchor.FollowControllerRotation(rawHead, initial, current));

    public void RestorePreview(CalibrationSnapshot anchor) => ApplyPreview(CurrentCalibration(), anchor);

    public void JumpInPattern(CalibrationSnapshot anchor, Matrix34 rawHead, Vector3 landing, Vector3? center, bool movesVertically = false)
    {
        Vector3 head = anchor.Origin.Unrotate(rawHead.Position - anchor.Origin.Position);
        Vector3 movement = landing - head;
        movement.Y = landing.Y;
        ApplyPreview(anchor, center is Vector3 focus ? anchor.MoveHead(rawHead, landing, focus) : anchor.Shift(movement, movesVertically));
    }

    private void ApplyPreview(CalibrationSnapshot before, CalibrationSnapshot after)
    {
        showPreview(after);
        startingCalibration ??= before;
        lastPreview = after;
    }

    public string? Reset()
    {
        if (startingCalibration is not null)
        {
            // Apply the exact starting transforms through the same path as a jump.
            showPreview(startingCalibration);
            lastPreview = startingCalibration;
            return "Play space restored.";
        }

        // Recover live offsets created by earlier versions of the app.
        Recovery? previous = ReadJournal();
        if (previous is null) return null;
        CalibrationSnapshot current = readCalibration();
        if (!current.Matches(previous.Original))
        {
            if (!IsKnown(current, previous))
            {
                File.Delete(JournalPath);
                return "SteamVR calibration changed elsewhere. The old teleport offset was discarded.";
            }
            commitCalibration(previous.Original);
        }
        File.Delete(JournalPath);
        return "Play space restored.";
    }

    public void EndPreview()
    {
        if (startingCalibration is null) return;
        hidePreview();
        startingCalibration = null;
        lastPreview = null;
    }

    private static bool IsKnown(CalibrationSnapshot current, Recovery previous) =>
        current.Matches(previous.Original) || current.Matches(previous.Before) || current.Matches(previous.After);

    private static Recovery? ReadJournal() => File.Exists(JournalPath)
        ? JsonSerializer.Deserialize<Recovery>(File.ReadAllText(JournalPath), Json)
        : null;
}

internal sealed class AimGesture
{
    private bool canStart;
    private ulong aimingOrigin;
    public bool InProgress => aimingOrigin != 0;

    public void Cancel()
    {
        canStart = false;
        aimingOrigin = 0;
    }

    public (ulong Origin, bool Release) Update(DigitalAction action)
    {
        if (action.Active == 0)
        {
            Cancel();
            return default;
        }

        if (action.Pressed == 0)
        {
            ulong origin = aimingOrigin;
            aimingOrigin = 0;
            canStart = true;
            return (origin, origin != 0);
        }

        if (canStart)
        {
            aimingOrigin = action.Origin;
            canStart = false;
        }
        if (aimingOrigin != action.Origin) Cancel();
        return (aimingOrigin, false);
    }
}
