use rusqlite::{params, Connection, OptionalExtension, Result};
use serde::Serialize;

// ---------------------------------------------------------------------------
// Session CRUD (DATA-03)
// ---------------------------------------------------------------------------

/// Inserts a new session row when a round begins.
/// Returns the row ID so it can be passed to `complete_session` later.
pub fn insert_session(
    conn: &Connection,
    round_type: &str,
    duration_secs: u32,
) -> Result<i64> {
    let started_at = unix_now();
    conn.execute(
        "INSERT INTO sessions (started_at, round_type, duration_secs, completed)
         VALUES (?1, ?2, ?3, 0)",
        params![started_at, round_type, duration_secs],
    )?;
    let id = conn.last_insert_rowid();
    log::debug!("[db] session started: id={id} type={round_type} duration={duration_secs}s");
    Ok(id)
}

/// Updates a session when the round ends (by completion or skip).
pub fn complete_session(
    conn: &Connection,
    session_id: i64,
    completed: bool,
) -> Result<()> {
    conn.execute(
        "UPDATE sessions SET ended_at = ?1, completed = ?2 WHERE id = ?3",
        params![unix_now(), completed as i64, session_id],
    )?;
    log::debug!("[db] session ended: id={session_id} completed={completed}");
    Ok(())
}

// ---------------------------------------------------------------------------
// Stats queries
// ---------------------------------------------------------------------------

#[derive(Debug, Serialize)]
pub struct SessionStats {
    pub total_work_sessions: i64,
    pub completed_work_sessions: i64,
    /// Sum of duration_secs for all *completed* work sessions.
    pub total_work_secs: i64,
}

pub fn get_all_time_stats(conn: &Connection) -> Result<SessionStats> {
    let total_work_sessions: i64 = conn.query_row(
        "SELECT COUNT(*) FROM sessions WHERE round_type = 'work'",
        [],
        |r| r.get(0),
    )?;

    let completed_work_sessions: i64 = conn.query_row(
        "SELECT COUNT(*) FROM sessions WHERE round_type = 'work' AND completed = 1",
        [],
        |r| r.get(0),
    )?;

    let total_work_secs: i64 = conn.query_row(
        "SELECT COALESCE(SUM(duration_secs), 0)
         FROM sessions WHERE round_type = 'work' AND completed = 1",
        [],
        |r| r.get(0),
    )?;

    Ok(SessionStats {
        total_work_sessions,
        completed_work_sessions,
        total_work_secs,
    })
}

// ---------------------------------------------------------------------------
// Detailed stats queries (DATA-04)
// ---------------------------------------------------------------------------

#[derive(Debug, Serialize)]
pub struct DailyStats {
    pub rounds: u32,
    pub focus_mins: u32,
    /// None when no work sessions were started today (avoids 0/0).
    pub completion_rate: Option<f32>,
    /// Completed work rounds per hour of the day (index 0 = midnight).
    pub by_hour: Vec<u32>,
}

#[derive(Debug, Serialize)]
pub struct DayStat {
    /// Local calendar date in "YYYY-MM-DD" format.
    pub date: String,
    pub rounds: u32,
}

#[derive(Debug, Serialize)]
pub struct HeatmapEntry {
    /// Local calendar date in "YYYY-MM-DD" format.
    pub date: String,
    pub count: u32,
}

#[derive(Debug, Serialize)]
pub struct StreakInfo {
    pub current: u32,
    pub longest: u32,
}

/// Completed work rounds and focus time for today (local calendar date).
pub fn get_daily_stats(conn: &Connection) -> Result<DailyStats> {
    let today: String = conn.query_row(
        "SELECT date('now', 'localtime')",
        [],
        |r| r.get(0),
    )?;

    let total: i64 = conn.query_row(
        "SELECT COUNT(*) FROM sessions
         WHERE round_type = 'work'
         AND date(started_at, 'unixepoch', 'localtime') = ?1",
        [&today],
        |r| r.get(0),
    )?;

    let completed: i64 = conn.query_row(
        "SELECT COUNT(*) FROM sessions
         WHERE round_type = 'work' AND completed = 1
         AND date(started_at, 'unixepoch', 'localtime') = ?1",
        [&today],
        |r| r.get(0),
    )?;

    let focus_secs: i64 = conn.query_row(
        "SELECT COALESCE(SUM(duration_secs), 0) FROM sessions
         WHERE round_type = 'work' AND completed = 1
         AND date(started_at, 'unixepoch', 'localtime') = ?1",
        [&today],
        |r| r.get(0),
    )?;

    let mut by_hour = vec![0u32; 24];
    let mut stmt = conn.prepare(
        "SELECT CAST(strftime('%H', datetime(started_at, 'unixepoch', 'localtime')) AS INTEGER) as h,
                COUNT(*) as cnt
         FROM sessions
         WHERE round_type = 'work' AND completed = 1
         AND date(started_at, 'unixepoch', 'localtime') = ?1
         GROUP BY h",
    )?;
    let rows = stmt.query_map([&today], |r| Ok((r.get::<_, i64>(0)?, r.get::<_, u32>(1)?)))?;
    for row in rows.flatten() {
        let (h, cnt) = row;
        if (0..24).contains(&h) {
            by_hour[h as usize] = cnt;
        }
    }

    Ok(DailyStats {
        rounds: completed as u32,
        focus_mins: ((focus_secs + 30) / 60) as u32,
        completion_rate: if total > 0 { Some(completed as f32 / total as f32) } else { None },
        by_hour,
    })
}

/// Completed work rounds per local calendar day for the last 7 days.
pub fn get_weekly_stats(conn: &Connection) -> Result<Vec<DayStat>> {
    let mut stmt = conn.prepare(
        "SELECT date(started_at, 'unixepoch', 'localtime') as day,
                COUNT(*) as rounds
         FROM sessions
         WHERE round_type = 'work' AND completed = 1
         AND date(started_at, 'unixepoch', 'localtime') >= date('now', 'localtime', '-6 days')
         GROUP BY day
         ORDER BY day",
    )?;
    let rows = stmt.query_map([], |r| Ok(DayStat { date: r.get(0)?, rounds: r.get(1)? }))?
        .collect();
    rows
}

/// Completed work rounds per local calendar day, all time (no date limit).
/// The frontend slices this into per-year views for navigation.
pub fn get_heatmap_data(conn: &Connection) -> Result<Vec<HeatmapEntry>> {
    let mut stmt = conn.prepare(
        "SELECT date(started_at, 'unixepoch', 'localtime') as day,
                COUNT(*) as cnt
         FROM sessions
         WHERE round_type = 'work' AND completed = 1
         GROUP BY day
         ORDER BY day",
    )?;
    let rows = stmt.query_map([], |r| Ok(HeatmapEntry { date: r.get(0)?, count: r.get(1)? }))?
        .collect();
    rows
}

/// Current and longest work-session streaks (consecutive local calendar days).
/// A streak stays active until midnight: if yesterday had sessions but today does not,
/// the streak is still counted as current.
pub fn get_streak(conn: &Connection) -> Result<StreakInfo> {
    let today: String = conn.query_row(
        "SELECT date('now', 'localtime')",
        [],
        |r| r.get(0),
    )?;

    let mut stmt = conn.prepare(
        "SELECT date(started_at, 'unixepoch', 'localtime') as day
         FROM sessions
         WHERE round_type = 'work' AND completed = 1
         GROUP BY day
         ORDER BY day",
    )?;
    let days: Vec<String> = stmt
        .query_map([], |r| r.get(0))?
        .flatten()
        .collect();

    Ok(compute_streak(&days, &today))
}

// ---------------------------------------------------------------------------
// Streak helpers
// ---------------------------------------------------------------------------

/// Convert a "YYYY-MM-DD" string to a day number for arithmetic comparison.
/// Uses the proleptic Gregorian calendar; absolute value is arbitrary — only
/// differences between dates matter.
fn date_to_day_num(s: &str) -> Option<i32> {
    let mut parts = s.splitn(3, '-');
    let y: i32 = parts.next()?.parse().ok()?;
    let m: i32 = parts.next()?.parse().ok()?;
    let d: i32 = parts.next()?.parse().ok()?;
    let y = if m <= 2 { y - 1 } else { y };
    let m = if m <= 2 { m + 12 } else { m };
    Some(y * 365 + y / 4 - y / 100 + y / 400 + (153 * m - 457) / 5 + d)
}

pub fn compute_streak(days: &[String], today: &str) -> StreakInfo {
    let nums: Vec<i32> = days.iter().filter_map(|s| date_to_day_num(s)).collect();
    if nums.is_empty() {
        return StreakInfo { current: 0, longest: 0 };
    }

    let today_n = match date_to_day_num(today) {
        Some(n) => n,
        None => return StreakInfo { current: 0, longest: 0 },
    };

    // Current streak — alive if most recent session day is today or yesterday.
    let last = *nums.last().unwrap();
    let current = if last == today_n || last == today_n - 1 {
        let mut count = 0u32;
        let mut expected = last;
        for &n in nums.iter().rev() {
            if n == expected {
                count += 1;
                expected -= 1;
            } else {
                break;
            }
        }
        count
    } else {
        0
    };

    // Longest streak.
    let mut longest = 1u32;
    let mut run = 1u32;
    for i in 1..nums.len() {
        if nums[i] == nums[i - 1] + 1 {
            run += 1;
            if run > longest { longest = run; }
        } else {
            run = 1;
        }
    }

    StreakInfo { current, longest }
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

fn unix_now() -> i64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .unwrap_or_default()
        .as_secs() as i64
}

// ---------------------------------------------------------------------------
// Task CRUD
// ---------------------------------------------------------------------------

#[derive(Debug, Clone, Serialize)]
pub struct Task {
    pub id: i64, pub title: String, pub estimated_pomodoros: i64,
    pub completed_pomodoros: i64, pub elapsed_work_secs: i64,
    pub status: String, pub is_active: bool, pub color: String,
}

pub fn task_list(conn: &Connection) -> Result<Vec<Task>> {
    let mut stmt = conn.prepare(
        "SELECT id, title, estimated_pomos, completed_pomos, elapsed_work_secs, is_active, is_completed, color
         FROM tasks WHERE is_completed = 0 ORDER BY created_at ASC")?;
    let rows = stmt.query_map([], |r| {
        Ok(Task {
            id: r.get(0)?, title: r.get(1)?, estimated_pomodoros: r.get(2)?,
            completed_pomodoros: r.get(3)?, elapsed_work_secs: r.get(4)?,
            is_active: r.get::<_, i64>(5)? != 0,
            status: if r.get::<_, i64>(6)? != 0 { "completed".into() } else { "active".into() },
            color: r.get(7)?,
        })
    })?.collect();
    rows
}

pub fn task_set_active(conn: &Connection, id: i64) -> Result<()> {
    conn.execute("UPDATE tasks SET is_active = 0 WHERE is_completed = 0", [])?;
    conn.execute("UPDATE tasks SET is_active = 1 WHERE id = ?1", [id])?;
    Ok(())
}

pub fn task_get_active(conn: &Connection) -> Result<Option<i64>> {
    conn.query_row(
        "SELECT id FROM tasks WHERE is_active = 1 AND is_completed = 0 LIMIT 1",
        [], |r| r.get(0),
    ).optional()
}

pub fn complete_task(conn: &Connection, id: i64) -> Result<()> {
    conn.execute("UPDATE tasks SET is_completed = 1, is_active = 0, completed_at = ?1 WHERE id = ?2",
        params![unix_now(), id])?;
    // Auto-activate next
    if let Ok(Some(next)) = conn.query_row(
        "SELECT id FROM tasks WHERE is_completed = 0 ORDER BY created_at ASC LIMIT 1",
        [], |r| r.get::<_, i64>(0)).optional() {
        conn.execute("UPDATE tasks SET is_active = 1 WHERE id = ?1", [next])?;
    }
    Ok(())
}

pub fn task_increment_pomo(conn: &Connection, id: i64) -> Result<()> {
    conn.execute("UPDATE tasks SET completed_pomos = completed_pomos + 1 WHERE id = ?1", [id])?;
    Ok(())
}

const TASK_PALETTE: &[&str] = &[
    "#e74c3c", "#e67e22", "#f1c40f", "#2ecc71", "#1abc9c",
    "#3498db", "#9b59b6", "#e91e63", "#00bcd4", "#8bc34a",
    "#ff9800", "#795548", "#607d8b", "#673ab7", "#009688",
];

pub fn task_create(conn: &Connection, title: &str, estimated_pomos: i64) -> Result<Task> {
    let count: i64 = conn.query_row("SELECT COUNT(*) FROM tasks", [], |r| r.get(0))?;
    let color = TASK_PALETTE[count as usize % TASK_PALETTE.len()];
    let now = unix_now();
    conn.execute(
        "INSERT INTO tasks (title, estimated_pomos, color, created_at) VALUES (?1, ?2, ?3, ?4)",
        params![title, estimated_pomos, color, now],
    )?;
    let id = conn.last_insert_rowid();
    // Auto-activate if no active task
    let active_count: i64 = conn.query_row(
        "SELECT COUNT(*) FROM tasks WHERE is_active = 1 AND is_completed = 0", [], |r| r.get(0))?;
    if active_count == 0 {
        conn.execute("UPDATE tasks SET is_active = 1 WHERE id = ?1", [id])?;
    }
    Ok(Task {
        id, title: title.to_string(), estimated_pomodoros: estimated_pomos,
        completed_pomodoros: 0, elapsed_work_secs: 0,
        is_active: active_count == 0, status: "active".to_string(),
        color: color.to_string(),
    })
}

pub fn task_delete(conn: &Connection, id: i64) -> Result<()> {
    let was_active: bool = conn.query_row(
        "SELECT is_active FROM tasks WHERE id = ?1", [id], |r| r.get::<_, i64>(0),
    ).map(|v| v != 0).unwrap_or(false);
    conn.execute("DELETE FROM tasks WHERE id = ?1", [id])?;
    if was_active {
        if let Ok(Some(next)) = conn.query_row(
            "SELECT id FROM tasks WHERE is_completed = 0 ORDER BY created_at ASC LIMIT 1",
            [], |r| r.get::<_, i64>(0)).optional() {
            conn.execute("UPDATE tasks SET is_active = 1 WHERE id = ?1", [next])?;
        }
    }
    Ok(())
}

pub fn task_update_estimate(conn: &Connection, id: i64, estimated_pomos: i64) -> Result<()> {
    conn.execute("UPDATE tasks SET estimated_pomos = ?1 WHERE id = ?2", params![estimated_pomos, id])?;
    Ok(())
}

pub fn task_list_completed(conn: &Connection) -> Result<Vec<Task>> {
    let mut stmt = conn.prepare(
        "SELECT id, title, estimated_pomos, completed_pomos, elapsed_work_secs, is_active, is_completed, color
         FROM tasks WHERE is_completed = 1 ORDER BY completed_at DESC")?;
    let rows = stmt.query_map([], |r| {
        Ok(Task {
            id: r.get(0)?, title: r.get(1)?, estimated_pomodoros: r.get(2)?,
            completed_pomodoros: r.get(3)?, elapsed_work_secs: r.get(4)?,
            is_active: false, status: "completed".to_string(), color: r.get(7)?,
        })
    })?.collect();
    rows
}

pub fn task_reopen(conn: &Connection, id: i64) -> Result<()> {
    conn.execute("UPDATE tasks SET is_completed = 0, completed_at = NULL WHERE id = ?1", [id])?;
    Ok(())
}

// ---------------------------------------------------------------------------
// Jar queries (today's completed work sessions for plugin)
// ---------------------------------------------------------------------------

#[derive(Debug, Clone, Serialize)]
pub struct JarEntry {
    pub duration_secs: i64,
    pub flow_score: i64,
    pub hour: i64,
    pub completed: bool,
    pub task_title: String,
    pub task_color: String,
}

pub fn jar_today(conn: &Connection) -> Result<Vec<JarEntry>> {
    let today: String = conn.query_row("SELECT date('now', 'localtime')", [], |r| r.get(0))?;
    let mut stmt = conn.prepare(
        "SELECT s.duration_secs, COALESCE(s.flow_score, 0),
                CAST(strftime('%H', s.started_at, 'unixepoch', 'localtime') AS INTEGER),
                s.completed, COALESCE(t.title, ''), COALESCE(t.color, '')
         FROM sessions s LEFT JOIN tasks t ON s.task_id = t.id
         WHERE s.round_type = 'work' AND s.completed = 1
         AND date(s.started_at, 'unixepoch', 'localtime') = ?1
         ORDER BY s.started_at ASC")?;
    let rows = stmt.query_map([&today], |r| {
        Ok(JarEntry {
            duration_secs: r.get(0)?, flow_score: r.get(1)?, hour: r.get(2)?,
            completed: r.get::<_, i64>(3)? != 0, task_title: r.get(4)?, task_color: r.get(5)?,
        })
    })?.collect();
    rows
}

/// Completed work sessions for the last 7 days (for weekly jar visualization).
/// Includes the local date string for grouping by day.
#[derive(Debug, Clone, Serialize)]
pub struct WeekJarEntry {
    pub date: String,
    pub duration_secs: i64,
    pub flow_score: i64,
}

pub fn jar_week(conn: &Connection) -> Result<Vec<WeekJarEntry>> {
    let mut stmt = conn.prepare(
        "SELECT date(s.started_at, 'unixepoch', 'localtime'),
                s.duration_secs, COALESCE(s.flow_score, 0)
         FROM sessions s
         WHERE s.round_type = 'work' AND s.completed = 1
         AND date(s.started_at, 'unixepoch', 'localtime') >= date('now', 'localtime', '-6 days')
         ORDER BY s.started_at ASC")?;
    let rows = stmt.query_map([], |r| {
        Ok(WeekJarEntry {
            date: r.get(0)?, duration_secs: r.get(1)?, flow_score: r.get(2)?,
        })
    })?.collect();
    rows
}

// ---------------------------------------------------------------------------
// Session recording with flow
// ---------------------------------------------------------------------------

pub fn insert_session_with_flow(conn: &Connection, round_type: &str, duration_secs: u32, task_id: Option<i64>) -> Result<i64> {
    conn.execute(
        "INSERT INTO sessions (started_at, round_type, duration_secs, completed, task_id) VALUES (?1, ?2, ?3, 0, ?4)",
        params![unix_now(), round_type, duration_secs, task_id])?;
    Ok(conn.last_insert_rowid())
}

pub fn complete_session_with_flow(conn: &Connection, id: i64, completed: bool, flow_score: u32, pause_count: u32) -> Result<()> {
    conn.execute(
        "UPDATE sessions SET ended_at = ?1, completed = ?2, flow_score = ?3, pause_count = ?4 WHERE id = ?5",
        params![unix_now(), completed as i64, flow_score, pause_count, id])?;
    Ok(())
}

// ---------------------------------------------------------------------------
// Flow timeline for stats graph
// ---------------------------------------------------------------------------

#[derive(Debug, Clone, Serialize)]
pub struct FlowTimelineEntry {
    pub started_at: i64,
    pub duration_secs: u32,
    pub flow_score: u32,
    pub task_id: Option<i64>,
    pub task_title: Option<String>,
    pub task_color: Option<String>,
}

pub fn flow_timeline_today(conn: &Connection) -> Result<Vec<FlowTimelineEntry>> {
    let today: String = conn.query_row("SELECT date('now', 'localtime')", [], |r| r.get(0))?;
    let mut stmt = conn.prepare(
        "SELECT s.started_at, s.duration_secs, COALESCE(s.flow_score, 0), s.task_id,
                t.title, t.color
         FROM sessions s
         LEFT JOIN tasks t ON s.task_id = t.id
         WHERE s.round_type = 'work' AND s.completed = 1
         AND date(s.started_at, 'unixepoch', 'localtime') = ?1
         ORDER BY s.started_at ASC"
    )?;
    let rows = stmt.query_map([&today], |r| {
        Ok(FlowTimelineEntry {
            started_at: r.get(0)?, duration_secs: r.get(1)?,
            flow_score: r.get(2)?, task_id: r.get(3)?,
            task_title: r.get(4)?, task_color: r.get(5)?,
        })
    })?.collect();
    rows
}

pub fn get_blocked_sites(conn: &Connection) -> Result<Vec<String>> {
    let raw: String = conn.query_row(
        "SELECT COALESCE(value, '') FROM settings WHERE key = 'blocked_sites'",
        [], |r| r.get(0),
    ).unwrap_or_default();
    Ok(raw.split(',').map(|s| s.trim().to_string()).filter(|s| !s.is_empty()).collect())
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::db::migrations;

    fn setup() -> Connection {
        let conn = Connection::open_in_memory().unwrap();
        migrations::run(&conn).unwrap();
        conn
    }

    #[test]
    fn insert_and_complete_session() {
        let conn = setup();
        let id = insert_session(&conn, "work", 1500).unwrap();
        assert!(id > 0);

        complete_session(&conn, id, true).unwrap();

        let completed: i64 = conn
            .query_row(
                "SELECT completed FROM sessions WHERE id = ?1",
                [id],
                |r| r.get(0),
            )
            .unwrap();
        assert_eq!(completed, 1);
    }

    #[test]
    fn stats_empty_db() {
        let conn = setup();
        let stats = get_all_time_stats(&conn).unwrap();
        assert_eq!(stats.total_work_sessions, 0);
        assert_eq!(stats.completed_work_sessions, 0);
        assert_eq!(stats.total_work_secs, 0);
    }

    #[test]
    fn compute_streak_empty() {
        let info = compute_streak(&[], "2024-03-15");
        assert_eq!(info.current, 0);
        assert_eq!(info.longest, 0);
    }

    #[test]
    fn compute_streak_active_today() {
        let days = vec!["2024-03-13".to_string(), "2024-03-14".to_string(), "2024-03-15".to_string()];
        let info = compute_streak(&days, "2024-03-15");
        assert_eq!(info.current, 3);
        assert_eq!(info.longest, 3);
    }

    #[test]
    fn compute_streak_active_until_midnight() {
        // Yesterday had sessions, today does not — streak still live.
        let days = vec!["2024-03-13".to_string(), "2024-03-14".to_string()];
        let info = compute_streak(&days, "2024-03-15");
        assert_eq!(info.current, 2);
    }

    #[test]
    fn compute_streak_broken() {
        // Last session was 2 days ago — streak is broken.
        let days = vec!["2024-03-12".to_string(), "2024-03-13".to_string()];
        let info = compute_streak(&days, "2024-03-15");
        assert_eq!(info.current, 0);
    }

    #[test]
    fn compute_streak_longest_across_break() {
        let days = vec![
            "2024-03-01".to_string(), "2024-03-02".to_string(), "2024-03-03".to_string(),
            "2024-03-10".to_string(), "2024-03-11".to_string(),
        ];
        let info = compute_streak(&days, "2024-03-11");
        assert_eq!(info.current, 2);
        assert_eq!(info.longest, 3);
    }

    #[test]
    fn get_daily_stats_empty() {
        let conn = setup();
        let stats = get_daily_stats(&conn).unwrap();
        assert_eq!(stats.rounds, 0);
        assert_eq!(stats.focus_mins, 0);
        assert!(stats.completion_rate.is_none());
        assert_eq!(stats.by_hour.len(), 24);
    }

    #[test]
    fn get_weekly_stats_empty() {
        let conn = setup();
        let stats = get_weekly_stats(&conn).unwrap();
        assert!(stats.is_empty());
    }

    #[test]
    fn get_heatmap_data_empty() {
        let conn = setup();
        let entries = get_heatmap_data(&conn).unwrap();
        assert!(entries.is_empty());
    }

    #[test]
    fn focus_mins_rounds_to_nearest_minute() {
        let conn = setup();

        // 339 s = 5:39 → rounds up to 6 min (remainder 39 ≥ 30).
        let id1 = insert_session(&conn, "work", 339).unwrap();
        complete_session(&conn, id1, true).unwrap();
        let stats = get_daily_stats(&conn).unwrap();
        assert_eq!(stats.focus_mins, 6, "339 s should round to 6 min");

        // Reset and test round-down: 324 s = 5:24 → rounds down to 5 min (remainder 24 < 30).
        let conn2 = setup();
        let id2 = insert_session(&conn2, "work", 324).unwrap();
        complete_session(&conn2, id2, true).unwrap();
        let stats2 = get_daily_stats(&conn2).unwrap();
        assert_eq!(stats2.focus_mins, 5, "324 s should round to 5 min");

        // Exact minute boundary: 1500 s = 25:00 → stays 25 min.
        let conn3 = setup();
        let id3 = insert_session(&conn3, "work", 1500).unwrap();
        complete_session(&conn3, id3, true).unwrap();
        let stats3 = get_daily_stats(&conn3).unwrap();
        assert_eq!(stats3.focus_mins, 25, "1500 s should be exactly 25 min");
    }

    #[test]
    fn stats_counts_correctly() {
        let conn = setup();

        let id1 = insert_session(&conn, "work", 1500).unwrap();
        complete_session(&conn, id1, true).unwrap();

        let id2 = insert_session(&conn, "work", 1500).unwrap();
        complete_session(&conn, id2, false).unwrap(); // skipped

        let _id3 = insert_session(&conn, "short-break", 300).unwrap();

        let stats = get_all_time_stats(&conn).unwrap();
        assert_eq!(stats.total_work_sessions, 2);
        assert_eq!(stats.completed_work_sessions, 1);
        assert_eq!(stats.total_work_secs, 1500);
    }
}
