# PomoDeck — Focus you can feel. 🍅

## Inspiration

Pomodoro apps live on the same screen that's distracting you. A notification you dismiss. A tab you forget. A phone timer you ignore.

The MX Creative Console has physical buttons with LCD screens, a dial, and haptic feedback through the MX Master 4. We asked: what if the timer wasn't on your screen at all?

The original Pomodoro was a tomato-shaped kitchen timer. PomoDeck brings that tactile feeling back — powered by modern hardware and backed by productivity research. 50% of Pomodoro users quit within two weeks because rigid intervals don't match attention spans ([Neurosity, 2026](https://neurosity.co/guides/best-ways-use-pomodoro-technique)). Context switching after breaks costs 23 minutes ([UC Irvine](https://pomocool.com/blog-pages/focus-study-timers)). Every design decision in PomoDeck traces back to one of these findings.

## What it does

**On the console — 9 plugin actions:**

- **Focus Timer** — live countdown with Classic dial or Liquid water-fill display. Tap to start/pause. Double-tap to switch style.
- **Adjust Time** — physical dial: turn to change duration 1–120 min. Press to cycle Focus/Break/Long Break. Triple-press resets to 25-5-15.
- **Reset / Skip** — reset current phase (paused only) or jump to next phase with per-phase audio feedback.
- **Active Task** — shows current task with color tag and progress dots. Press to cycle. Double-press to add new task.
- **Complete Task** — animated checkmark with trim-path drawing. Next task activates automatically.
- **PomoDeck App** — tomato mascot with 6 flow-driven poses (Sleeping → Deep Flow). Press to show/hide app.
- **Pomo Jar** — physics-based jar collecting today's sessions as colored tomatoes. Size from duration, color from flow quality. Press to open stats.
- **Sound** — toggle focus tick on/off, syncs with app.

**In the desktop app (Tauri 2 + Svelte 5 + Rust):**

- Real-time flow scoring (0–100) with 6 levels — builds on sustained focus, drops on distraction, decays gradually during breaks.
- Web blocking via PAC proxy + app blocking via process monitor — activates during focus, unlocks on break.
- Task management with color tags, pomo estimation, and progress tracking.
- Statistics — daily flow graph on 24h axis with live updates, weekly bar chart with physics jars and value coins, yearly activity heatmap.
- Two themes (dark/light) synced to plugin. 8 languages. Custom sounds. System tray with auto-start.
- Bidirectional WebSocket sync — every setting, every state change updates both sides in real-time.

**PomoDeck solves three problems.** Distraction — the blocker enforces focus. Awareness — the flow score shows how you're actually doing. Motivation — the jar, the streaks, the haptics make every session feel earned.

## How we built it

The plugin is C# on .NET 8, rendering every button with SkiaSharp at 118×118 pixels. The tomato mascot interpolates 17 pose parameters for smooth flow-state transitions. The Pomo Jar runs a 45-frame physics simulation with gravity, 8-pass collision resolution, and frozen-ball optimization.

The desktop app is Tauri 2 with Svelte 5 and Rust. The timer engine runs on a dedicated thread with drift-correcting sleep. The flow tracker processes 8 real-time input signals. The web blocker runs a PAC proxy on port 9998 and an intercepting proxy on port 9999. WebSocket on port 1314 handles bidirectional sync.

The bridge reconnects automatically. When the app connects mid-session, the plugin syncs its timer state so no progress is lost. When the app disconnects, the plugin cleans up the proxy and continues standalone.

All rendering goes through a global RenderGate — concurrent renders capped at 4, 150ms per-widget cooldown. Images are JPEG 85% (3x faster encode, 40% smaller than PNG). Static widgets cache rendered bytes. Sound system uses alert cooldown to prevent tick queuing. Steady-state: 1 USB transfer/second. All other widgets: zero.

## Challenges we ran into

**The accidental deletion.** Midway through development, we lost the entire app source directory. We rebuilt the app — the second version came out with cleaner architecture and better decisions.

**Late hardware.** Testing devices arrived late. We built most of the plugin against SDK docs before testing on real hardware. When the console arrived, we discovered the Plugin Service has a 1-second timeout per render call — undocumented. This led to a complete rendering pipeline overhaul.

**Console heat.** Every `ActionImageChanged()` sends an image over USB. We engineered the system around minimizing transfers: silent state storage, cache-key deduplication, connection cooldown, idle polling at 0.33fps.

**The haptic insight.** We added haptic pulses at every flow level. Testing showed feedback during deep focus broke concentration. So we inverted it: low flow gets encouragement, deep flow gets silence. The system disappears when you need it most.

**Sound queuing.** Phase-complete alerts blocked tick sounds, causing rapid bursts. We built a priority system with cooldown flags and queue draining.

## Accomplishments that we're proud of

**Complete product.** 9 plugin actions, full desktop app, bidirectional sync, flow scoring, web/app blocking, task management, three-tab statistics, two themes, 8 languages, custom sounds, onboarding, system tray, auto-start, NSIS installer. All working together.

**The flow system.** Real-time 0–100 score driven by 8 input signals. The tomato mascot on the console visualizes it — you see your focus state without reading a number.

**The physics jar.** Every session becomes a tomato with real physics. Weekly stats show 7 mini jars under each day with value coins.

**Zero-overhead optimization.** Heavy features, light on the device. JPEG encoding, byte caching, render throttling, state deduplication, connection cooldown, sound priority. The console stays cool through an 8-hour workday.

## What we learned

**Hardware UX is different.** On an 80×80px LCD button, every pixel matters. Color and shape communicate faster than text.

**Haptics have emotional range.** Distinct waveforms for celebration, enforcement, and gentle pauses create a layer screens can't deliver. The most important haptic decision is knowing when to stay silent.

**USB bandwidth is a real constraint.** Every image transfer heats the device. Optimization is the difference between a product that works for 8 hours and one that throttles after 20 minutes.

**Research-driven design works.** Every feature traces to a specific finding about why people fail at focus. The adjustable dial exists because rigid intervals cause abandonment. The flow score exists because users have no idea if they're actually focusing. The gradual break decay exists because binary penalties don't match reality.

## What's next for PomoDeck

| Feature | Description |
|---------|-------------|
| Adaptive Timer | Local ML model learning optimal duration from flow patterns |
| Smart Break Coach | Suggest break types based on follow-up session quality |
| Warm-up Ritual | 60-second breathing phase before focus to reduce context-switching cost |
| Focus Momentum | Daily goals, milestones, rank progression |
| Proactive Insights | Daily summary delivered to the console on first session |
| Focus Profiles | Deep Work / Light Work / Creative with auto-configured blocking and sounds |

The architecture supports all of this. The data is in SQLite. The protocol is extensible. The rendering pipeline has headroom.

PomoDeck — Focus you can feel. 🍅
