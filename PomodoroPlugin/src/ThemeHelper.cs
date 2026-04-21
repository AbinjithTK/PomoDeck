namespace Loupedeck.PomoDeckPlugin
{
    using System;
    using SkiaSharp;

    /// <summary>
    /// Shared theme color + font resolver for all PomoDeck actions.
    /// Ensures consistent background, text, and phase colors across every button.
    /// </summary>
    internal static class ThemeHelper
    {
        // ── Default fallback colors (dark teal theme) ──────────────────────
        internal static readonly SKColor DefaultBg    = new(26, 29, 35);
        internal static readonly SKColor DefaultTrack = new(34, 38, 46);
        internal static readonly SKColor DefaultText  = new(232, 234, 237);
        internal static readonly SKColor DefaultDim   = new(139, 149, 165);
        internal static readonly SKColor WorkColor    = new(0, 212, 170);
        internal static readonly SKColor ShortColor   = new(0, 229, 160);
        internal static readonly SKColor LongColor    = new(0, 180, 216);
        internal static readonly SKColor AccentColor  = new(0, 212, 170);

        // ── Shared typeface (immutable after init — thread-safe) ──────────
        private static SKTypeface _typeface;
        private static readonly Object _lock = new();

        internal static SKTypeface Typeface
        {
            get
            {
                if (_typeface != null) return _typeface;
                lock (_lock)
                {
                    _typeface ??= SKTypeface.FromFamilyName("Segoe UI",
                        SKFontStyleWeight.Medium, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
                        ?? SKTypeface.Default;
                }
                return _typeface;
            }
        }

        // ── Phase-label colors (distinct per phase) ─────────────────────
        internal static readonly SKColor BreakLabelColor    = new(230, 65, 55);   // red
        internal static readonly SKColor LongBreakLabelColor = new(60, 130, 230); // blue

        /// <summary>Resolved theme colors for a single render frame.</summary>
        internal readonly struct Colors
        {
            public readonly SKColor Bg;
            public readonly SKColor Track;
            public readonly SKColor Text;
            public readonly SKColor Dim;
            public readonly SKColor Phase;
            public readonly SKColor Accent;
            /// <summary>Phase label text color — Focus=accent, Break=red, Long Break=blue.</summary>
            public readonly SKColor PhaseLabel;

            public Colors(SKColor bg, SKColor track, SKColor text, SKColor dim, SKColor phase, SKColor accent, SKColor phaseLabel)
            {
                Bg = bg; Track = track; Text = text; Dim = dim; Phase = phase; Accent = accent; PhaseLabel = phaseLabel;
            }
        }

        /// <summary>Resolve all theme colors from the plugin's bridge state.</summary>
        internal static Colors Resolve(PomoDeckPlugin pomo)
        {
            var theme = pomo?.Bridge?.Theme;

            var bg     = theme?.GetColor("--color-background", DefaultBg) ?? DefaultBg;
            var track  = theme?.GetColor("--color-background-light", DefaultTrack) ?? DefaultTrack;
            var text   = theme?.GetColor("--color-foreground", DefaultText) ?? DefaultText;
            var dim    = theme?.GetColor("--color-foreground-darker", DefaultDim) ?? DefaultDim;
            var accent = theme?.GetColor("--color-accent", AccentColor) ?? AccentColor;

            var work  = theme?.GetColor("--color-focus-round", WorkColor) ?? WorkColor;
            var sbrk  = theme?.GetColor("--color-short-round", ShortColor) ?? ShortColor;
            var lbrk  = theme?.GetColor("--color-long-round", LongColor) ?? LongColor;

            var phase = pomo?.GetPhase() switch
            {
                PomodoroTimer.TimerPhase.ShortBreak => sbrk,
                PomodoroTimer.TimerPhase.LongBreak  => lbrk,
                _ => work
            };

            var phaseLabel = pomo?.GetPhase() switch
            {
                PomodoroTimer.TimerPhase.ShortBreak => BreakLabelColor,
                PomodoroTimer.TimerPhase.LongBreak  => LongBreakLabelColor,
                _ => accent
            };

            return new Colors(bg, track, text, dim, phase, accent, phaseLabel);
        }

        /// <summary>Dimmed colors for secondary buttons — makes the main timer stand out.</summary>
        internal static Colors ResolveSecondary(PomoDeckPlugin pomo)
        {
            var c = Resolve(pomo);
            return new Colors(
                c.Bg,
                c.Track,
                c.Text.WithAlpha(120),    // much dimmer text
                c.Dim.WithAlpha(90),      // very dim labels
                c.Phase.WithAlpha(120),   // dimmed phase color
                c.Accent.WithAlpha(120),
                c.PhaseLabel.WithAlpha(120)
            );
        }

        /// <summary>Minimum render size for consistent resolution.</summary>
        internal static Int32 RenderSize(PluginImageSize imageSize)
            => Math.Max(Math.Max(imageSize.GetButtonWidth(), imageSize.GetButtonHeight()), 200);
    }
}
