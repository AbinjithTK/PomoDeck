namespace Loupedeck.PomoDeckPlugin
{
    using System;
    using System.Diagnostics;
    using System.Timers;
    using SkiaSharp;

    /// <summary>
    /// Open PomoDeck — tomato mascot with smooth flow-state-driven pose transitions.
    /// All poses defined as numeric parameters, interpolated for smooth blending.
    /// </summary>
    public class LaunchAppCommand : PluginDynamicCommand
    {
        private PomoDeckPlugin Pomo => this.Plugin as PomoDeckPlugin;
        private readonly Timer _pollTimer;
        private readonly PressAnimation _pressAnim;
        private Boolean _lastRunning;
        private String _lastLevel = "sleeping";
        private Int32 _lastScore;
        private Int32 _scoreDelta;
        private readonly Stopwatch _deltaClock = new();
        private const Int32 DeltaFadeMs = 1200;

        private readonly Stopwatch _eyeClock = new();
        private readonly Timer _eyeTimer;
        private volatile Boolean _eyeActive;
        private const Int32 EyeDurationMs = 1400;

        private readonly Stopwatch _poseClock = new();
        private readonly Timer _animTimer; // shared for pose + delta
        private volatile Boolean _animActive;
        private Pose _fromPose;
        private Pose _toPose;
        private Pose _currentPose;
        private const Int32 PoseTransMs = 600;

        private const Single Cx = 40f, Cy = 33f, R = 14f;

        // ── Pose: all visual properties as floats for interpolation ───────
        private struct Pose
        {
            public Single EyeOpenness;   // 0=closed arcs, 1=full open ovals
            public Single EyeHeight;     // vertical radius of eye oval
            public Single PupilSize;     // pupil radius
            public Single LidDroop;      // 0=none, 1=half covered
            public Single SmileCurve;    // 0=none, 0.5=slight, 1=grin
            public Single MouthOpen;     // 0=none, 1=full "O" mouth (breathing/determined)
            public Single LevitateY;     // 0=grounded, -3=floating
            public Single GlowAlpha;     // 0=none, 40=deep flow glow
            public Single HandAlpha;     // 0=no hands, 180=mudra visible
            public Single SereneEyes;    // 0=normal, 1=upward peaceful curves
            public Single ZzzAlpha;      // 0=none, 80=sleeping zzz
            public Single EyeHighlight;  // 0=none, 200=bright sparkle
            public Single CheekPuff;     // 0=none, 1=puffed cheeks (deep breath)
            public Single LegExtend;     // 0=lotus, 1=one leg straightened out
            public Single ArmsWide;      // 0=none, 1=arms spread wide open
            public Single LyingSide;     // 0=none, 1=lying on side (head on hand, legs crossed)
            public Single HeadTilt;      // 0=none, negative=tilt left (radians)

            public static Pose Lerp(Pose a, Pose b, Single t)
            {
                return new Pose
                {
                    EyeOpenness  = L(a.EyeOpenness, b.EyeOpenness, t),
                    EyeHeight    = L(a.EyeHeight, b.EyeHeight, t),
                    PupilSize    = L(a.PupilSize, b.PupilSize, t),
                    LidDroop     = L(a.LidDroop, b.LidDroop, t),
                    SmileCurve   = L(a.SmileCurve, b.SmileCurve, t),
                    MouthOpen    = L(a.MouthOpen, b.MouthOpen, t),
                    LevitateY    = L(a.LevitateY, b.LevitateY, t),
                    GlowAlpha    = L(a.GlowAlpha, b.GlowAlpha, t),
                    HandAlpha    = L(a.HandAlpha, b.HandAlpha, t),
                    SereneEyes   = L(a.SereneEyes, b.SereneEyes, t),
                    ZzzAlpha     = L(a.ZzzAlpha, b.ZzzAlpha, t),
                    EyeHighlight = L(a.EyeHighlight, b.EyeHighlight, t),
                    CheekPuff    = L(a.CheekPuff, b.CheekPuff, t),
                    LegExtend    = L(a.LegExtend, b.LegExtend, t),
                    ArmsWide     = L(a.ArmsWide, b.ArmsWide, t),
                    LyingSide    = L(a.LyingSide, b.LyingSide, t),
                    HeadTilt     = L(a.HeadTilt, b.HeadTilt, t),
                };
            }
            private static Single L(Single a, Single b, Single t) => a + (b - a) * t;
        }

        private static Pose PoseFor(String level) => level switch
        {
            "sleeping"    => new Pose { EyeOpenness=0, EyeHeight=0, PupilSize=0, LidDroop=0, SmileCurve=0, MouthOpen=0, LevitateY=0, GlowAlpha=0, HandAlpha=0, SereneEyes=0, ZzzAlpha=80, EyeHighlight=0, CheekPuff=0, LegExtend=0, ArmsWide=0, LyingSide=0, HeadTilt=0 },
            "drowsy"      => new Pose { EyeOpenness=0, EyeHeight=0, PupilSize=0, LidDroop=0, SmileCurve=-0.3f, MouthOpen=0, LevitateY=0, GlowAlpha=0, HandAlpha=0, SereneEyes=0, ZzzAlpha=0, EyeHighlight=0, CheekPuff=0, LegExtend=0, ArmsWide=0, LyingSide=1, HeadTilt=-0.12f },
            "awake"       => new Pose { EyeOpenness=0, EyeHeight=0, PupilSize=0, LidDroop=0, SmileCurve=0, MouthOpen=1, LevitateY=0, GlowAlpha=0, HandAlpha=0, SereneEyes=1, ZzzAlpha=0, EyeHighlight=0, CheekPuff=1, LegExtend=0, ArmsWide=1, LyingSide=0, HeadTilt=0 },
            "focused"     => new Pose { EyeOpenness=1, EyeHeight=2.2f, PupilSize=1.3f, LidDroop=0.15f, SmileCurve=0.4f, MouthOpen=0, LevitateY=0, GlowAlpha=0, HandAlpha=0, SereneEyes=0, ZzzAlpha=0, EyeHighlight=0, CheekPuff=0, LegExtend=0, ArmsWide=0, LyingSide=0, HeadTilt=0 },
            "in_the_zone" => new Pose { EyeOpenness=1, EyeHeight=3.5f, PupilSize=1.8f, LidDroop=0, SmileCurve=0.5f, MouthOpen=0, LevitateY=-1.5f, GlowAlpha=20, HandAlpha=180, SereneEyes=0, ZzzAlpha=0, EyeHighlight=200, CheekPuff=0, LegExtend=1, ArmsWide=0, LyingSide=0, HeadTilt=0 },
            "deep_flow"   => new Pose { EyeOpenness=0, EyeHeight=0, PupilSize=0, LidDroop=0, SmileCurve=0.5f, MouthOpen=0, LevitateY=-3f, GlowAlpha=40, HandAlpha=180, SereneEyes=1, ZzzAlpha=0, EyeHighlight=0, CheekPuff=0, LegExtend=0, ArmsWide=0, LyingSide=0, HeadTilt=0 },
            _ => new Pose { EyeOpenness=0, EyeHeight=0, PupilSize=0, LidDroop=0, SmileCurve=0, MouthOpen=0, LevitateY=0, GlowAlpha=0, HandAlpha=0, SereneEyes=0, ZzzAlpha=80, EyeHighlight=0, CheekPuff=0, LegExtend=0, ArmsWide=0, LyingSide=0, HeadTilt=0 },
        };

        public LaunchAppCommand() : base("1. PomoDeck App", "Open or toggle the PomoDeck desktop app. Shows your live flow state and score when connected", "3. Flow & Stats (App Required)")
        {
            this.IsWidget = true;
            _currentPose = _fromPose = _toPose = PoseFor("sleeping");
            _pressAnim = new PressAnimation(() => { RenderGate.Request("LaunchApp", () => { try { this.ActionImageChanged(); } catch { } }); });

            _eyeTimer = new Timer(50) { AutoReset = true };
            _eyeTimer.Elapsed += (_, _) => { try {
                if (!_eyeActive || _eyeClock.ElapsedMilliseconds >= EyeDurationMs) { _eyeActive = false; _eyeTimer.Stop(); _eyeClock.Stop(); }
                this.ActionImageChanged();
            } catch { } };

            _animTimer = new Timer(33) { AutoReset = true };
            _animTimer.Elapsed += (_, _) => { try {
                if (_animActive && _poseClock.ElapsedMilliseconds < PoseTransMs)
                {
                    var t = EaseOut((Single)_poseClock.ElapsedMilliseconds / PoseTransMs);
                    _currentPose = Pose.Lerp(_fromPose, _toPose, t);
                }
                else if (_animActive) { _currentPose = _toPose; _animActive = false; }
                var deltaDone = !_deltaClock.IsRunning || _deltaClock.ElapsedMilliseconds >= DeltaFadeMs;
                if (!_animActive && deltaDone) { _animTimer.Stop(); }
                RenderGate.Request("LaunchApp", () => { try { this.ActionImageChanged(); } catch { } });
            } catch { } };

            _pollTimer = new Timer(5000) { AutoReset = true }; // 5s poll — reduced from 2s
            _pollTimer.Elapsed += (_, _) => { if (_pressAnim.IsActive || _eyeActive || _animActive) return; try {
                var running = Pomo?.IsRemote == true || PomodoroApplication.IsRunning();
                var level = Pomo?.FlowState?.Level ?? "sleeping";
                if (running != _lastRunning || level != _lastLevel) { _lastRunning = running; TransitionTo(level); RenderGate.Request("LaunchApp", () => { try { this.ActionImageChanged(); } catch { } }); }
            } catch { } };
            _pollTimer.Start();
        }

        private void TransitionTo(String level)
        {
            if (level == _lastLevel) return;
            _fromPose = _currentPose;
            _toPose = PoseFor(level);
            _lastLevel = level;
            _animActive = true;
            _poseClock.Restart();
            _animTimer.Start();
        }

        protected override Boolean OnLoad()
        {
            Pomo?.RegisterThemeListener(() => { RenderGate.Request("LaunchApp", () => { try { this.ActionImageChanged(); } catch { } }); });
            Pomo?.RegisterFlowListener(flow => {
                var level = flow?.Level ?? "sleeping";
                var score = flow?.Score ?? 0;
                var delta = score - _lastScore;
                if (delta != 0) { _scoreDelta = delta; _deltaClock.Restart(); if (!_animActive) _animTimer.Start(); }
                _lastScore = score;
                TransitionTo(level);
            });
            return true;
        }

        protected override Boolean OnUnload()
        {
            _pollTimer?.Stop(); _pollTimer?.Dispose();
            _eyeTimer?.Stop(); _eyeTimer?.Dispose();
            _animTimer?.Stop(); _animTimer?.Dispose();
            return true;
        }

        private DateTime _lastAppPress = DateTime.MinValue;

        protected override void RunCommand(String actionParameter)
        {
            // Debounce: ignore rapid presses within 500ms
            var now = DateTime.UtcNow;
            if ((now - _lastAppPress).TotalMilliseconds < 500) return;
            _lastAppPress = now;

            if (PomodoroApplication.IsRunning()) { var p = Pomo; if (p?.Bridge != null && p.Bridge.IsConnected) p.Bridge.Send("{\"type\":\"toggleWindow\"}"); else ToggleWindowViaWin32(); }
            else { PomodoroApplication.Launch(); }
            _pressAnim.Kick(); _eyeActive = true; _eyeClock.Restart(); _eyeTimer.Start();
        }

        private static void ToggleWindowViaWin32() { try { Win32Window.Toggle("PomoDeck", "pomodeck"); } catch { } }
        protected override String GetCommandDisplayName(String a, PluginImageSize s) => null;

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize)
        {
            try
            {
                var pomo = Pomo;
                var tc = ThemeHelper.Resolve(pomo);
                var running = PomodoroApplication.IsRunning();
                var available = PomodoroApplication.FindExe() != null;
                var p = _currentPose;

                var size = ThemeHelper.RenderSize(imageSize);
                using var bmp = new SKBitmap(size, size);
                using var c = new SKCanvas(bmp);
                c.Scale(size / 80f);
                c.Clear(tc.Bg);

                var sc = _pressAnim.Scale;
                c.Save(); c.Translate(40, 35); c.Scale(sc, sc); c.Translate(-40, -35);

                // Levitate
                c.Save(); c.Translate(0, p.LevitateY);

                // Head tilt
                if (Math.Abs(p.HeadTilt) > 0.01f)
                {
                    c.Save(); c.Translate(Cx, Cy); c.RotateRadians(p.HeadTilt); c.Translate(-Cx, -Cy);
                }

                var calyxGreen = new SKColor(0, 180, 130); // fixed teal green — never changes with theme
                var red = running || available ? new SKColor(230, 55, 45) : tc.Track;

                // Golden aura behind tomato (deep flow states)
                if (p.GlowAlpha > 5)
                {
                    var goldAlpha = (Byte)Math.Min(60, p.GlowAlpha * 1.5f);
                    using var auraShader = SKShader.CreateRadialGradient(
                        new SKPoint(Cx, Cy), R + 12,
                        new[] { new SKColor(255, 200, 80, goldAlpha), new SKColor(255, 180, 50, (Byte)(goldAlpha / 3)), SKColors.Transparent },
                        new[] { 0f, 0.5f, 1f }, SKShaderTileMode.Clamp);
                    using var auraPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Shader = auraShader };
                    c.DrawCircle(Cx, Cy, R + 12, auraPaint);
                }

                DrawBody(c, calyxGreen, red);
                DrawCalyx(c, calyxGreen);

                // Eyes — look-around overrides only the eyes, everything else stays
                var eyeT = _eyeActive ? (Single)_eyeClock.ElapsedMilliseconds / EyeDurationMs : -1f;
                if (eyeT >= 0f && eyeT < 1f) { DrawLookAroundEyes(c, eyeT); }
                else { DrawPoseEyes(c, p); }

                // Mouth
                if (p.MouthOpen > 0.01f)
                {
                    var mouthY = Cy + 6f + p.MouthOpen * 1.5f;
                    var ow = 1.5f + p.MouthOpen * 1f;
                    var oh = 1.2f + p.MouthOpen * 1.5f;
                    using var op = new SKPaint { IsAntialias = true, Color = new SKColor(40, 20, 20), Style = SKPaintStyle.Fill };
                    c.DrawOval(Cx, mouthY, ow, oh, op);
                    using var ip = new SKPaint { IsAntialias = true, Color = new SKColor(80, 30, 30), Style = SKPaintStyle.Fill };
                    c.DrawOval(Cx, mouthY + 0.3f, ow * 0.5f, oh * 0.4f, ip);
                }
                else if (p.SmileCurve > 0.01f)
                {
                    var curve = p.SmileCurve * 3f;
                    var mouthY = Cy + 5.5f + p.SmileCurve * 2f;
                    using var mp = new SKPaint { IsAntialias = true, Color = new SKColor(40, 20, 20), Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, StrokeCap = SKStrokeCap.Round };
                    using var smile = new SKPath();
                    var hw = 2f + p.SmileCurve * 2f;
                    smile.MoveTo(Cx - hw, mouthY); smile.QuadTo(Cx, mouthY + curve, Cx + hw, mouthY);
                    c.DrawPath(smile, mp);
                }
                // Tired mouth — small flat or slightly downward line
                else if (p.SmileCurve < -0.01f)
                {
                    var mouthY = Cy + 6.5f;
                    var hw = 2.5f;
                    var droop = p.SmileCurve * 2f; // negative = curves down
                    using var mp = new SKPaint { IsAntialias = true, Color = new SKColor(40, 20, 20, 160), Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f, StrokeCap = SKStrokeCap.Round };
                    using var tired = new SKPath();
                    tired.MoveTo(Cx - hw, mouthY); tired.QuadTo(Cx, mouthY + droop, Cx + hw, mouthY);
                    c.DrawPath(tired, mp);
                }

                // Puffed cheeks
                if (p.CheekPuff > 0.05f)
                {
                    var puffAlpha = (Byte)(35 * p.CheekPuff);
                    using var cp = new SKPaint { IsAntialias = true, Color = new SKColor(255, 130, 110, puffAlpha), Style = SKPaintStyle.Fill };
                    c.DrawOval(Cx - 11f, Cy + 4f, 3f * p.CheekPuff, 2.5f * p.CheekPuff, cp);
                    c.DrawOval(Cx + 11f, Cy + 4f, 3f * p.CheekPuff, 2.5f * p.CheekPuff, cp);
                }

                // Eyebrows — different styles per state
                if (p.EyeOpenness > 0.5f)
                {
                    // Focused (SmileCurve ~0.4, slight lid droop): raised attentive eyebrows + sweat drop
                    if (p.SmileCurve > 0.2f && p.SmileCurve < 0.6f && p.EyeHighlight < 50)
                    {
                        using var bp = new SKPaint { IsAntialias = true, Color = new SKColor(40, 20, 20, 160), Style = SKPaintStyle.Stroke, StrokeWidth = 1.3f, StrokeCap = SKStrokeCap.Round };
                        // Raised brows (attentive, not angry)
                        c.DrawLine(Cx - 8f, Cy - 2f, Cx - 3.5f, Cy - 3.5f, bp);
                        c.DrawLine(Cx + 8f, Cy - 2f, Cx + 3.5f, Cy - 3.5f, bp);
                        // Small concentration sweat drop on right side
                        using var dp = new SKPaint { IsAntialias = true, Color = tc.Phase.WithAlpha(80), Style = SKPaintStyle.Fill };
                        using var drop = new SKPath();
                        drop.MoveTo(Cx + 12, Cy - 2);
                        drop.QuadTo(Cx + 13, Cy + 1, Cx + 12, Cy + 2);
                        drop.QuadTo(Cx + 11, Cy + 1, Cx + 12, Cy - 2);
                        drop.Close();
                        c.DrawPath(drop, dp);
                    }

                    // InTheZone (EyeHighlight > 100): determined V-brows + energy spark lines
                    if (p.EyeHighlight > 100)
                    {
                        var browAlpha = (Byte)Math.Min(255, p.EyeHighlight);
                        using var bp = new SKPaint { IsAntialias = true, Color = new SKColor(40, 20, 20, browAlpha), Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, StrokeCap = SKStrokeCap.Round };
                        // Determined V-brows angled inward
                        c.DrawLine(Cx - 8.5f, Cy - 2f, Cx - 3f, Cy - 3.5f, bp);
                        c.DrawLine(Cx + 8.5f, Cy - 2f, Cx + 3f, Cy - 3.5f, bp);
                        // Small energy spark lines radiating from head
                        using var ep = new SKPaint { IsAntialias = true, Color = tc.Phase.WithAlpha((Byte)(browAlpha * 0.6f)), Style = SKPaintStyle.Stroke, StrokeWidth = 1f, StrokeCap = SKStrokeCap.Round };
                        c.DrawLine(Cx - 12, Cy - 8, Cx - 14, Cy - 11, ep);
                        c.DrawLine(Cx + 12, Cy - 8, Cx + 14, Cy - 11, ep);
                        c.DrawLine(Cx, Cy - 14, Cx, Cy - 17, ep);
                    }
                }

                // Zzz (suppress only during look-around animation)
                if (p.ZzzAlpha > 1)
                {
                    using var zp = new SKPaint { IsAntialias = true, Color = new SKColor(200, 200, 200, (Byte)p.ZzzAlpha), TextSize = 6, Typeface = ThemeHelper.Typeface, SubpixelText = true };
                    c.DrawText("z", Cx + 10, Cy - 4, zp);
                    zp.TextSize = 5; c.DrawText("z", Cx + 13, Cy - 8, zp);
                }

                // Restore head tilt before drawing limbs
                if (Math.Abs(p.HeadTilt) > 0.01f) { c.Restore(); }

                // Meditation hands + legs — dark red strokes
                if (p.HandAlpha > 1)
                {
                    var ha = (Byte)p.HandAlpha;
                    using var hp = new SKPaint { IsAntialias = true, Color = new SKColor(140, 35, 30, ha), Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };

                    // Yoga lotus legs — feet tucked in, knees out
                    // Right leg extends when LegExtend > 0
                    using var legs = new SKPath();
                    // Left shin — always tucked
                    legs.MoveTo(Cx - 9, Cy + 14);
                    legs.QuadTo(Cx - 12, Cy + 17, Cx - 4, Cy + 19);
                    legs.QuadTo(Cx - 1, Cy + 19.5f, Cx + 2, Cy + 18);
                    // Right shin — tucked or extended based on LegExtend
                    var ext = p.LegExtend;
                    if (ext < 0.1f)
                    {
                        // Fully tucked
                        legs.MoveTo(Cx + 9, Cy + 14);
                        legs.QuadTo(Cx + 12, Cy + 17, Cx + 4, Cy + 19);
                        legs.QuadTo(Cx + 1, Cy + 19.5f, Cx - 2, Cy + 18);
                    }
                    else
                    {
                        // Straightened out at an angle (down-right)
                        var endX = Cx + 9 + ext * 10f;
                        var endY = Cy + 14 + ext * 6f;
                        legs.MoveTo(Cx + 9, Cy + 14);
                        legs.QuadTo(Cx + 11, Cy + 16, endX, endY);
                    }
                    c.DrawPath(legs, hp);

                    // Left hand — palm up on left knee
                    using var lh = new SKPath();
                    lh.MoveTo(Cx - 11, Cy + 6);
                    lh.QuadTo(Cx - 14, Cy + 8, Cx - 16, Cy + 9);
                    lh.LineTo(Cx - 17, Cy + 7.5f);
                    lh.MoveTo(Cx - 16, Cy + 9);
                    lh.LineTo(Cx - 18, Cy + 8.5f);
                    lh.MoveTo(Cx - 16, Cy + 9);
                    lh.LineTo(Cx - 18, Cy + 9.5f);
                    c.DrawPath(lh, hp);

                    // Right hand — mirror
                    using var rh = new SKPath();
                    rh.MoveTo(Cx + 11, Cy + 6);
                    rh.QuadTo(Cx + 14, Cy + 8, Cx + 16, Cy + 9);
                    rh.LineTo(Cx + 17, Cy + 7.5f);
                    rh.MoveTo(Cx + 16, Cy + 9);
                    rh.LineTo(Cx + 18, Cy + 8.5f);
                    rh.MoveTo(Cx + 16, Cy + 9);
                    rh.LineTo(Cx + 18, Cy + 9.5f);
                    c.DrawPath(rh, hp);
                }

                // Standing pose (awake deep breath) — arms wide + short straight legs
                if (p.ArmsWide > 0.05f)
                {
                    var wa = (Byte)(160 * p.ArmsWide);
                    using var ap = new SKPaint { IsAntialias = true, Color = new SKColor(140, 35, 30, wa), Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f, StrokeCap = SKStrokeCap.Round };
                    // Left arm — extends out and slightly up
                    using var la = new SKPath();
                    la.MoveTo(Cx - 11, Cy + 4);
                    la.QuadTo(Cx - 16, Cy + 1, Cx - 20, Cy - 2);
                    c.DrawPath(la, ap);
                    // Right arm — mirror
                    using var ra = new SKPath();
                    ra.MoveTo(Cx + 11, Cy + 4);
                    ra.QuadTo(Cx + 16, Cy + 1, Cx + 20, Cy - 2);
                    c.DrawPath(ra, ap);
                    // Standing legs — short, straight down, slightly apart
                    c.DrawLine(Cx - 4, Cy + 14, Cx - 5, Cy + 20, ap);
                    c.DrawLine(Cx + 4, Cy + 14, Cx + 5, Cy + 20, ap);
                }

                // Lying on side (drowsy) — head propped on hand, legs crossed
                if (p.LyingSide > 0.05f)
                {
                    var lyA = (Byte)(160 * p.LyingSide);
                    using var lyP = new SKPaint { IsAntialias = true, Color = new SKColor(140, 35, 30, lyA), Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f, StrokeCap = SKStrokeCap.Round };
                    // Left arm propping head — elbow on ground, hand under cheek
                    using var arm = new SKPath();
                    arm.MoveTo(Cx - 14, Cy + 12); // elbow on ground
                    arm.LineTo(Cx - 14, Cy + 2);  // forearm up
                    arm.LineTo(Cx - 11, Cy - 1);  // hand touching head
                    c.DrawPath(arm, lyP);
                    // Right arm resting on body
                    using var rarm = new SKPath();
                    rarm.MoveTo(Cx + 10, Cy + 5);
                    rarm.QuadTo(Cx + 13, Cy + 8, Cx + 11, Cy + 12);
                    c.DrawPath(rarm, lyP);
                    // Legs — stretched out to the right, one over the other
                    c.DrawLine(Cx - 2, Cy + 14, Cx + 12, Cy + 16, lyP); // bottom leg
                    c.DrawLine(Cx, Cy + 13, Cx + 10, Cy + 13, lyP);     // top leg crossed over
                }

                c.Restore(); // levitate

                // Glow under tomato
                if (p.GlowAlpha > 1)
                {
                    using var glowShader = SKShader.CreateRadialGradient(new SKPoint(Cx, Cy + 16), 10,
                            new[] { tc.Phase.WithAlpha((Byte)p.GlowAlpha), SKColors.Transparent }, null, SKShaderTileMode.Clamp);
                    using var gp = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Shader = glowShader };
                    c.DrawOval(Cx, Cy + 16, 12, 3, gp);
                }

                c.Restore(); // press scale

                // Label — show flow state when running, otherwise app status
                String label;
                SKColor labelColor;
                if (running && pomo?.IsRemote == true)
                {
                    // Show current flow level
                    label = _lastLevel switch
                    {
                        "sleeping" => "SLEEPING",
                        "drowsy" => "STARTING",
                        "awake" => "DEEP BREATH",
                        "focused" => "FOCUSED",
                        "in_the_zone" => "IN THE ZONE",
                        "deep_flow" => "DEEP FLOW",
                        _ => "FOCUS"
                    };
                    labelColor = tc.Phase;
                }
                else if (running)
                {
                    label = "RUNNING";
                    labelColor = tc.Accent;
                }
                else if (available)
                {
                    label = "OPEN";
                    labelColor = tc.Text;
                }
                else
                {
                    label = "NOT FOUND";
                    labelColor = tc.Dim;
                }
                using var lp = new SKPaint { IsAntialias = true, Color = labelColor, TextSize = 9, TextAlign = SKTextAlign.Center, Typeface = ThemeHelper.Typeface, SubpixelText = true };
                c.DrawText(label, Cx, 68, lp);

                // Score
                if (_lastScore > 0 || (pomo?.FlowState?.InFocus == true))
                {
                    using var sp = new SKPaint { IsAntialias = true, Color = tc.Dim.WithAlpha(140), TextSize = 7, TextAlign = SKTextAlign.Right, Typeface = ThemeHelper.Typeface, SubpixelText = true };
                    c.DrawText($"{_lastScore}", 74, 10, sp);
                }

                // Delta
                var da = _deltaClock.ElapsedMilliseconds;
                if (_scoreDelta != 0 && da < DeltaFadeMs)
                {
                    var ft = (Single)da / DeltaFadeMs;
                    var al = (Byte)(255 * (1f - ft));
                    var fy = 16f - ft * 8f;
                    var dc = _scoreDelta > 0 ? tc.Phase.WithAlpha(al) : new SKColor(230, 65, 55, al);
                    using var dp = new SKPaint { IsAntialias = true, Color = dc, TextSize = 8, TextAlign = SKTextAlign.Right, Typeface = ThemeHelper.Typeface, SubpixelText = true };
                    c.DrawText(_scoreDelta > 0 ? $"+{_scoreDelta}" : $"{_scoreDelta}", 74, fy, dp);
                }

                using var img = SKImage.FromBitmap(bmp);
                using var data = img.Encode(SKEncodedImageFormat.Jpeg, 85);
                return BitmapImage.FromArray(data.ToArray());
            }
            catch (Exception ex) { PluginLog.Warning(ex, "LaunchApp render"); using var b = new BitmapBuilder(imageSize); b.Clear(new BitmapColor(26, 29, 35)); return b.ToImage(); }
        }

        // ── Pose-driven eyes (interpolated) ───────────────────────────────
        private static void DrawPoseEyes(SKCanvas c, Pose p)
        {
            var lx = Cx - 5.5f; var ly = Cy + 1.5f;
            var rx = Cx + 4.5f; var ry = Cy + 1f;
            var dark = new SKColor(40, 20, 20);

            // Serene eyes (deep flow — upward curves)
            if (p.SereneEyes > 0.5f)
            {
                using var ep = new SKPaint { IsAntialias = true, Color = dark, Style = SKPaintStyle.Stroke, StrokeWidth = 1.8f, StrokeCap = SKStrokeCap.Round };
                using var le = new SKPath(); le.MoveTo(Cx - 8, Cy + 2); le.QuadTo(Cx - 5.5f, Cy - 0.5f, Cx - 3, Cy + 2); c.DrawPath(le, ep);
                using var re = new SKPath(); re.MoveTo(Cx + 2, Cy + 1.5f); re.QuadTo(Cx + 4.5f, Cy - 1f, Cx + 7, Cy + 1.5f); c.DrawPath(re, ep);
                return;
            }

            // Closed sleepy arcs
            if (p.EyeOpenness < 0.05f)
            {
                using var ep = new SKPaint { IsAntialias = true, Color = dark, Style = SKPaintStyle.Stroke, StrokeWidth = 2f, StrokeCap = SKStrokeCap.Round };
                using var le = new SKPath(); le.MoveTo(Cx - 8, Cy + 1); le.QuadTo(Cx - 5.5f, Cy + 4, Cx - 3, Cy + 1); c.DrawPath(le, ep);
                using var re = new SKPath(); re.MoveTo(Cx + 2, Cy); re.QuadTo(Cx + 4.5f, Cy + 3, Cx + 7, Cy); c.DrawPath(re, ep);
                return;
            }

            // Open eyes with interpolated height
            var eyeRy = Math.Max(0.5f, p.EyeHeight);
            using var wp = new SKPaint { IsAntialias = true, Color = SKColors.White, Style = SKPaintStyle.Fill };
            c.DrawOval(lx, ly, 3.5f, eyeRy, wp);
            c.DrawOval(rx, ry, 3.5f, eyeRy, wp);

            // Pupils
            if (p.PupilSize > 0.1f)
            {
                using var pp = new SKPaint { IsAntialias = true, Color = new SKColor(30, 15, 15), Style = SKPaintStyle.Fill };
                c.DrawCircle(lx, ly, p.PupilSize, pp);
                c.DrawCircle(rx, ry, p.PupilSize, pp);
            }

            // Eye highlights
            if (p.EyeHighlight > 10)
            {
                using var hp = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, (Byte)p.EyeHighlight), Style = SKPaintStyle.Fill };
                c.DrawCircle(lx - 0.8f, ly - 0.8f, 0.7f, hp);
                c.DrawCircle(rx - 0.8f, ry - 0.8f, 0.7f, hp);
            }

            // Droopy lids
            if (p.LidDroop > 0.05f)
            {
                var lidH = eyeRy * p.LidDroop;
                using var lp = new SKPaint { IsAntialias = true, Color = new SKColor(200, 45, 38), Style = SKPaintStyle.Fill };
                c.DrawRect(lx - 4, ly - eyeRy, 8, lidH, lp);
                c.DrawRect(rx - 4, ry - eyeRy, 8, lidH, lp);
            }
        }

        // ── Body + Calyx (unchanged) ──────────────────────────────────────
        private static void DrawBody(SKCanvas c, SKColor leaf, SKColor body)
        {
            using var bp = new SKPaint { IsAntialias = true, Color = body, Style = SKPaintStyle.Fill }; c.DrawCircle(Cx, Cy + 1, R, bp);
            using var shader = SKShader.CreateRadialGradient(new SKPoint(Cx - 4, Cy - 4), 10, new[] { new SKColor(255, 120, 100, 50), SKColors.Transparent }, null, SKShaderTileMode.Clamp);
            using var sp = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Shader = shader }; c.DrawCircle(Cx, Cy + 1, R, sp);
        }
        private static void DrawCalyx(SKCanvas c, SKColor leaf)
        {
            using var lp = new SKPaint { IsAntialias = true, Color = leaf, Style = SKPaintStyle.Fill };
            var sy = Cy - 11f; using var star = new SKPath();
            for (var i = 0; i < 10; i++) { var r = (i % 2 == 0) ? 9f : 3f; var a = (Single)(i * 36 - 90) * (Single)Math.PI / 180f; var px = Cx + r * (Single)Math.Cos(a); var py = sy + r * (Single)Math.Sin(a); if (i == 0) star.MoveTo(px, py); else star.LineTo(px, py); }
            star.Close(); c.DrawPath(star, lp);
            using var sp = new SKPaint { IsAntialias = true, Color = leaf, Style = SKPaintStyle.Stroke, StrokeWidth = 2f, StrokeCap = SKStrokeCap.Round };
            using var st = new SKPath(); st.MoveTo(Cx, sy - 1); st.CubicTo(Cx + 1, sy - 3, Cx + 2, sy - 5, Cx + 1, sy - 7); c.DrawPath(st, sp);
        }

        // ── Press look-around (unchanged) ─────────────────────────────────
        private static void DrawLookAroundEyes(SKCanvas c, Single t)
        {
            Single op; if (t < 0.15f) op = EaseOut(t / 0.15f); else if (t < 0.75f) op = 1f; else op = 1f - EaseIn((t - 0.75f) / 0.25f);
            Single gz; if (t < 0.15f) gz = 0f; else if (t < 0.35f) gz = -EaseOut((t - 0.15f) / 0.2f); else if (t < 0.55f) gz = -1f + 2f * EaseOut((t - 0.35f) / 0.2f); else if (t < 0.75f) gz = 1f - EaseOut((t - 0.55f) / 0.2f); else gz = 0f;
            if (op < 0.05f) { using var ep = new SKPaint { IsAntialias = true, Color = new SKColor(40, 20, 20), Style = SKPaintStyle.Stroke, StrokeWidth = 2f, StrokeCap = SKStrokeCap.Round }; using var le = new SKPath(); le.MoveTo(Cx - 8, Cy + 1); le.QuadTo(Cx - 5.5f, Cy + 4, Cx - 3, Cy + 1); c.DrawPath(le, ep); using var re = new SKPath(); re.MoveTo(Cx + 2, Cy); re.QuadTo(Cx + 4.5f, Cy + 3, Cx + 7, Cy); c.DrawPath(re, ep); return; }
            var lx = Cx - 5.5f; var ly = Cy + 1.5f; var rx = Cx + 4.5f; var ry = Cy + 1f; var ey = 3f * op;
            using var wp = new SKPaint { IsAntialias = true, Color = SKColors.White, Style = SKPaintStyle.Fill }; c.DrawOval(lx, ly, 3.5f, ey, wp); c.DrawOval(rx, ry, 3.5f, ey, wp);
            var off = gz * 1.5f; var pr = 1.5f * Math.Min(1f, op * 1.5f);
            using var pp = new SKPaint { IsAntialias = true, Color = new SKColor(30, 15, 15), Style = SKPaintStyle.Fill }; c.DrawCircle(lx + off, ly, pr, pp); c.DrawCircle(rx + off, ry, pr, pp);
            if (op < 0.95f) { var lc = new SKColor(200, 45, 38); var lh = ey * (1f - op) * 1.2f; using var lip = new SKPaint { IsAntialias = true, Color = lc, Style = SKPaintStyle.Fill }; c.DrawRect(lx - 4, ly - ey, 8, lh, lip); c.DrawRect(rx - 4, ry - ey, 8, lh, lip); c.DrawRect(lx - 4, ly + ey - lh, 8, lh, lip); c.DrawRect(rx - 4, ry + ey - lh, 8, lh, lip); }
        }

        private static Single EaseOut(Single t) { var f = 1f - t; return 1f - f * f * f; }
        private static Single EaseIn(Single t) => t * t * t;
    }

    /// <summary>Isolated DllImport class — prevents type load failures in action classes.</summary>
    internal static class Win32Window
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern Boolean ShowWindow(IntPtr h, Int32 c);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern Boolean SetForegroundWindow(IntPtr h);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern Boolean IsIconic(IntPtr h);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern Boolean IsWindowVisible(IntPtr h);

        public static void Toggle(params String[] names)
        {
            foreach (var name in names)
            {
                var ps = System.Diagnostics.Process.GetProcessesByName(name);
                foreach (var p in ps)
                {
                    var h = p.MainWindowHandle;
                    if (h == IntPtr.Zero) continue;
                    if (IsIconic(h) || !IsWindowVisible(h)) { ShowWindow(h, 9); SetForegroundWindow(h); }
                    else { ShowWindow(h, 6); }
                    return;
                }
            }
        }
    }
}
