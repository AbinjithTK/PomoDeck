/// Real-time flow state tracker with distraction detection.
///
/// Score (0–100) updates in real-time during focus:
///   - Accelerating sustained focus bonus (+1 to +3/min)
///   - Single-app focus reward (+1 per 30s)
///   - Active input reward (+1 per ~10s of keyboard/mouse activity)
///   - Pause penalty scales with depth (–6 to –15)
///   - Blocked app/site attempt (–8)
///   - Idle 90s+ (–5)
///   - Rapid window switching 6+ in 30s (–1)
///   - Session complete (+25), break carry-over (+10/+20)
///   - Natural decay when not in focus (–1 every 2min)

use std::sync::Mutex;
use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum FlowLevel {
    Sleeping, Drowsy, Awake, Focused, InTheZone, DeepFlow,
}

impl FlowLevel {
    pub fn from_score(score: u32, in_focus: bool) -> Self {
        if score == 0 { return Self::Sleeping; }
        if !in_focus && score < 20 { return Self::Sleeping; }
        match score {
            0..=19 => Self::Drowsy, 20..=39 => Self::Awake, 40..=64 => Self::Focused,
            65..=84 => Self::InTheZone, _ => Self::DeepFlow,
        }
    }
    pub fn as_str(&self) -> &'static str {
        match self {
            Self::Sleeping => "sleeping", Self::Drowsy => "drowsy", Self::Awake => "awake",
            Self::Focused => "focused", Self::InTheZone => "in_the_zone", Self::DeepFlow => "deep_flow",
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct FlowSnapshot {
    pub score: u32, pub level: FlowLevel, pub streak: u32, pub pause_count: u32,
    pub in_focus: bool, pub blocking_active: bool, pub focus_minutes: u32,
}

#[derive(Debug)]
struct Inner {
    score: u32, streak: u32, pause_count: u32, in_focus: bool,
    blocking_enabled: bool, break_bonus: i32,
    uninterrupted_ticks: u32, sustained_bonuses: u32,
    daily_scores: Vec<u32>,
    last_window: String, same_app_ticks: u32,
    switch_times: Vec<u32>, session_ticks: u32,
    idle_secs: u32, zoned_penalized: bool, decay_ticks: u32,
}

impl Default for Inner {
    fn default() -> Self {
        Self {
            score: 0, streak: 0, pause_count: 0, in_focus: false,
            blocking_enabled: false, break_bonus: 0,
            uninterrupted_ticks: 0, sustained_bonuses: 0, daily_scores: Vec::new(),
            last_window: String::new(), same_app_ticks: 0,
            switch_times: Vec::new(), session_ticks: 0,
            idle_secs: 0, zoned_penalized: false, decay_ticks: 0,
        }
    }
}

pub struct FlowTracker { inner: Mutex<Inner> }

impl FlowTracker {
    pub fn new(blocking_enabled: bool) -> Self {
        Self { inner: Mutex::new(Inner { blocking_enabled, ..Default::default() }) }
    }
    pub fn set_blocking_enabled(&self, enabled: bool) {
        self.inner.lock().unwrap().blocking_enabled = enabled;
    }

    pub fn on_focus_start(&self) -> FlowSnapshot {
        let mut s = self.inner.lock().unwrap();
        s.in_focus = true; s.pause_count = 0; s.uninterrupted_ticks = 0;
        s.sustained_bonuses = 0; s.last_window.clear(); s.same_app_ticks = 0;
        s.switch_times.clear(); s.session_ticks = 0; s.idle_secs = 0;
        s.zoned_penalized = false; s.decay_ticks = 0;
        let bonus = s.break_bonus; s.break_bonus = 0;
        if s.score == 0 {
            let mut base: i32 = 5;
            base += (s.streak as i32 * 4).min(20);
            if s.blocking_enabled { base += 10; }
            base += bonus;
            s.score = (base.max(0) as u32).min(100);
        } else {
            s.score = ((s.score as i32 + bonus).max(0) as u32).min(100);
        }
        snapshot(&s)
    }

    pub fn on_tick(&self) -> FlowSnapshot {
        let mut s = self.inner.lock().unwrap();
        if !s.in_focus {
            if s.score > 0 { s.decay_ticks += 1; if s.decay_ticks >= 120 { s.decay_ticks = 0; s.score = s.score.saturating_sub(1); } }
            return snapshot(&s);
        }
        s.uninterrupted_ticks += 1; s.session_ticks += 1;
        let mins = s.uninterrupted_ticks / 60;
        let interval = match mins { 0..=2 => 60, 3..=6 => 30, _ => 20 };
        if s.uninterrupted_ticks > 0 && s.uninterrupted_ticks % interval == 0 {
            s.sustained_bonuses += 1; s.score = (s.score + 1).min(100);
        }
        if s.same_app_ticks > 0 && s.same_app_ticks % 30 == 0 { s.score = (s.score + 1).min(100); }
        s.same_app_ticks += 1;
        let cutoff = s.session_ticks.saturating_sub(60);
        s.switch_times.retain(|&t| t > cutoff);
        snapshot(&s)
    }

    pub fn on_window_change(&self, process_name: &str) -> Option<FlowSnapshot> {
        let mut s = self.inner.lock().unwrap();
        if !s.in_focus { return None; }
        let name = process_name.to_lowercase();
        if name == s.last_window { return None; }
        s.last_window = name; s.same_app_ticks = 0;
        let tick = s.session_ticks;
        if s.switch_times.len() < 20 { s.switch_times.push(tick); }
        let cutoff = s.session_ticks.saturating_sub(30);
        s.switch_times.retain(|&t| t > cutoff);
        if s.switch_times.len() >= 6 { s.score = s.score.saturating_sub(1); s.switch_times.clear(); return Some(snapshot(&s)); }
        None
    }

    pub fn on_idle_update(&self, idle_seconds: u32) -> Option<FlowSnapshot> {
        let mut s = self.inner.lock().unwrap();
        if !s.in_focus { return None; }
        s.idle_secs = idle_seconds;
        if idle_seconds < 3 {
            s.zoned_penalized = false;
            s.decay_ticks += 1;
            if s.decay_ticks >= 20 { s.decay_ticks = 0; s.score = (s.score + 1).min(100); return Some(snapshot(&s)); }
            return None;
        }
        s.decay_ticks = 0;
        // First trigger at 90s, then every 15s after: -5 each time
        if idle_seconds >= 90 {
            if !s.zoned_penalized {
                // First hit at 90s
                s.zoned_penalized = true; s.uninterrupted_ticks = 0;
                s.score = s.score.saturating_sub(5);
                return Some(snapshot(&s));
            }
            // Repeated penalty every 15s after the first trigger
            // idle_seconds 90=first, 105=second, 120=third...
            let since_first = idle_seconds - 90;
            if since_first > 0 && since_first % 15 == 0 {
                s.score = s.score.saturating_sub(5);
                return Some(snapshot(&s));
            }
        }
        None
    }

    pub fn on_pause(&self) -> FlowSnapshot {
        let mut s = self.inner.lock().unwrap();
        if !s.in_focus { return snapshot(&s); }
        s.pause_count += 1; s.uninterrupted_ticks = 0;
        let penalty = match s.score { 0..=29 => 6, 30..=64 => 10, _ => 15 };
        s.score = s.score.saturating_sub(penalty); snapshot(&s)
    }
    pub fn on_focus_complete(&self) -> FlowSnapshot {
        let mut s = self.inner.lock().unwrap();
        s.in_focus = false; s.streak += 1; s.score = (s.score + 25).min(100);
        let sc = s.score; s.daily_scores.push(sc);
        if s.daily_scores.len() > 100 { s.daily_scores.remove(0); }
        s.decay_ticks = 0; snapshot(&s)
    }
    pub fn on_focus_skipped(&self) -> FlowSnapshot {
        let mut s = self.inner.lock().unwrap();
        s.in_focus = false; s.streak = 0; s.score = s.score.saturating_sub(25);
        let sc = s.score; s.daily_scores.push(sc);
        if s.daily_scores.len() > 100 { s.daily_scores.remove(0); }
        s.decay_ticks = 0; snapshot(&s)
    }
    pub fn on_short_break_taken(&self) -> FlowSnapshot { let mut s = self.inner.lock().unwrap(); s.break_bonus = 10; snapshot(&s) }
    pub fn on_long_break_taken(&self) -> FlowSnapshot { let mut s = self.inner.lock().unwrap(); s.break_bonus = 20; snapshot(&s) }
    pub fn on_short_break_skipped(&self) -> FlowSnapshot { let mut s = self.inner.lock().unwrap(); s.score = (s.score + 5).min(100); s.break_bonus = 5; snapshot(&s) }
    pub fn on_long_break_skipped(&self) -> FlowSnapshot { let mut s = self.inner.lock().unwrap(); s.score = (s.score + 8).min(100); s.break_bonus = 8; snapshot(&s) }

    /// Gradual decay during breaks. Called every second from the timer tick handler.
    /// Decays -1 every 30 seconds. Skipping the break early preserves remaining score.
    pub fn on_break_tick(&self) -> FlowSnapshot {
        let mut s = self.inner.lock().unwrap();
        if s.score > 0 {
            s.decay_ticks += 1;
            // -1 every 30 seconds during break (gradual, not harsh)
            if s.decay_ticks >= 30 {
                s.decay_ticks = 0;
                s.score = s.score.saturating_sub(1);
            }
        }
        snapshot(&s)
    }
    pub fn on_distraction(&self) -> FlowSnapshot {
        let mut s = self.inner.lock().unwrap();
        if !s.in_focus { return snapshot(&s); }
        s.score = s.score.saturating_sub(8); s.uninterrupted_ticks = 0; s.same_app_ticks = 0; snapshot(&s)
    }
    pub fn snapshot(&self) -> FlowSnapshot { let s = self.inner.lock().unwrap(); snapshot(&s) }
}

fn snapshot(s: &Inner) -> FlowSnapshot {
    FlowSnapshot {
        score: s.score, level: FlowLevel::from_score(s.score, s.in_focus),
        streak: s.streak, pause_count: s.pause_count, in_focus: s.in_focus,
        blocking_active: s.blocking_enabled, focus_minutes: s.uninterrupted_ticks / 60,
    }
}
