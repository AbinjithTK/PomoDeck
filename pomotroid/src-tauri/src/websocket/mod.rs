/// Optional opt-in WebSocket server via tokio + axum.
///
/// Binds to `127.0.0.1:{port}` (localhost only — never all interfaces).
///
/// Protocol:
///   Client → Server: `{ "type": "getState" }`
///   Server → Client: `{ "type": "state",       "payload": TimerSnapshot }`              (getState response)
///                    `{ "type": "started",      "payload": { "total_secs": u32 } }`     (broadcast)
///                    `{ "type": "roundChange",  "payload": TimerSnapshot }`              (broadcast)
///                    `{ "type": "paused",       "payload": { "elapsed_secs": u32 } }`   (broadcast)
///                    `{ "type": "resumed",      "payload": { "elapsed_secs": u32 } }`   (broadcast)
///                    `{ "type": "reset" }`                                               (broadcast)
///                    `{ "type": "error",        "message": "..." }`                     (startup failure)
///
/// Lifecycle:
///   - `start(port, app)` spawns a Tokio task; sets running handle in `WsState`.
///   - `stop()` aborts the task.
///   - On port conflict: emits `websocket:error` Tauri event instead of panicking.
use std::net::SocketAddr;
use std::sync::Arc;

use axum::{
    extract::{ws::{Message, WebSocket, WebSocketUpgrade}, State as AxumState},
    response::IntoResponse,
    routing::get,
    Router,
};
use futures_util::{SinkExt, StreamExt};
use tauri::{AppHandle, Emitter, Manager};
use tokio::{
    net::TcpListener,
    sync::broadcast,
    task::JoinHandle,
};

use crate::timer::{TimerController, TimerSnapshot};

// ---------------------------------------------------------------------------
// Broadcast channel payload
// ---------------------------------------------------------------------------

/// Shared payload for events that carry elapsed time.
#[derive(Clone, serde::Serialize)]
pub struct ElapsedPayload {
    pub elapsed_secs: u32,
}

/// Payload for the `started` event.
#[derive(Clone, serde::Serialize)]
pub struct StartedPayload {
    pub total_secs: u32,
}

/// Events broadcast to all connected WebSocket clients.
#[derive(Clone, serde::Serialize)]
#[serde(tag = "type", rename_all = "camelCase")]
pub enum WsEvent {
    State { payload: TimerSnapshot },
    Started { payload: StartedPayload },
    RoundChange { payload: TimerSnapshot },
    Paused { payload: ElapsedPayload },
    Resumed { payload: ElapsedPayload },
    Reset,
    FlowChanged { payload: crate::flow::FlowSnapshot },
    ThemeChanged { payload: serde_json::Value },
    TasksChanged { payload: serde_json::Value },
    JarChanged { payload: serde_json::Value },
    SkinChanged { payload: serde_json::Value },
    SettingsSync { payload: serde_json::Value },
}

// ---------------------------------------------------------------------------
// Server state shared between connections
// ---------------------------------------------------------------------------

#[derive(Clone)]
struct ServerState {
    app: AppHandle,
    broadcast_tx: broadcast::Sender<WsEvent>,
}

// ---------------------------------------------------------------------------
// Tauri-managed WebSocket state
// ---------------------------------------------------------------------------

pub struct WsState {
    task: tokio::sync::Mutex<Option<JoinHandle<()>>>,
    pub broadcast_tx: broadcast::Sender<WsEvent>,
}

impl WsState {
    pub fn new() -> Arc<Self> {
        let (broadcast_tx, _) = broadcast::channel(64);
        Arc::new(Self {
            task: tokio::sync::Mutex::new(None),
            broadcast_tx,
        })
    }
}

// ---------------------------------------------------------------------------
// Lifecycle
// ---------------------------------------------------------------------------

/// Start the WebSocket server on `127.0.0.1:{port}`.
///
/// Emits `websocket:error` if the port is already in use.
/// No-ops if already running (call `stop` first to change port).
pub async fn start(port: u16, app: AppHandle, state: &Arc<WsState>) {
    let addr: SocketAddr = ([127, 0, 0, 1], port).into();
    let listener = match TcpListener::bind(addr).await {
        Ok(l) => l,
        Err(e) => {
            log::error!("[ws] failed to bind {addr}: {e}");
            let _ = app.emit(
                "websocket:error",
                serde_json::json!({ "message": e.to_string(), "port": port }),
            );
            return;
        }
    };

    let server_state = ServerState {
        app: app.clone(),
        broadcast_tx: state.broadcast_tx.clone(),
    };

    let router = Router::new()
        .route("/ws", get(ws_handler))
        .with_state(server_state);

    let handle = tokio::spawn(async move {
        if let Err(e) = axum::serve(listener, router).await {
            log::error!("[ws] server error: {e}");
        }
    });

    *state.task.lock().await = Some(handle);
    log::info!("[ws] listening on ws://127.0.0.1:{port}/ws");
}

/// Stop the WebSocket server (aborts the task).
pub async fn stop(state: &Arc<WsState>) {
    if let Some(handle) = state.task.lock().await.take() {
        handle.abort();
    }
}

// ---------------------------------------------------------------------------
// WebSocket handler
// ---------------------------------------------------------------------------

async fn ws_handler(
    ws: WebSocketUpgrade,
    AxumState(state): AxumState<ServerState>,
) -> impl IntoResponse {
    ws.on_upgrade(move |socket| handle_socket(socket, state))
}

async fn handle_socket(socket: WebSocket, state: ServerState) {
    log::debug!("[ws] client connected");
    let (mut sender, mut receiver) = socket.split();
    let mut rx = state.broadcast_tx.subscribe();

    // Task: forward broadcast events to this client.
    let mut send_task = tokio::spawn(async move {
        while let Ok(event) = rx.recv().await {
            let json = match serde_json::to_string(&event) {
                Ok(s) => s,
                Err(_) => continue,
            };
            if sender.send(Message::Text(json.into())).await.is_err() {
                break;
            }
        }
    });

    // Main loop: handle incoming messages from this client.
    let app = state.app.clone();
    let broadcast_tx = state.broadcast_tx.clone();
    let mut recv_task = tokio::spawn(async move {
        while let Some(Ok(msg)) = receiver.next().await {
            match msg {
                Message::Text(text) => {
                    handle_client_message(&text, &app, &broadcast_tx).await;
                }
                Message::Close(_) => break,
                _ => {}
            }
        }
    });

    // If either task finishes, abort the other.
    tokio::select! {
        _ = &mut send_task => recv_task.abort(),
        _ = &mut recv_task => send_task.abort(),
    }
    log::debug!("[ws] client disconnected");
}

async fn handle_client_message(
    text: &str,
    app: &AppHandle,
    _broadcast_tx: &broadcast::Sender<WsEvent>,
) {
    let Ok(msg) = serde_json::from_str::<serde_json::Value>(text) else { return; };
    let msg_type = match msg.get("type").and_then(|t| t.as_str()) {
        Some(t) => t, None => return,
    };

    match msg_type {
        "getState" => {
            if let Some(timer) = app.try_state::<TimerController>() {
                let snapshot = timer.get_snapshot();
                let _ = _broadcast_tx.send(WsEvent::State { payload: snapshot });
            }
            // Also send current tick/volume settings so plugin can sync
            if let Some(db) = app.try_state::<crate::db::DbState>() {
                if let Ok(conn) = db.lock() {
                    if let Ok(s) = crate::settings::load(&conn) {
                        let payload = serde_json::json!({
                            "tick_sounds_work": s.tick_sounds_during_work,
                            "tick_sounds_break": s.tick_sounds_during_break,
                        });
                        let _ = _broadcast_tx.send(WsEvent::SettingsSync { payload });
                    }
                }
            }
        }
        "toggle" => { if let Some(t) = app.try_state::<TimerController>() { t.toggle(); } }
        "skip" => { if let Some(t) = app.try_state::<TimerController>() { t.skip(); } }
        "reset" => {
            if let Some(t) = app.try_state::<TimerController>() {
                let snap = t.get_snapshot();
                if snap.is_running {
                    // Don't reset a running timer — must pause first
                    log::debug!("[ws] reset ignored: timer is running");
                } else if snap.is_paused {
                    // Paused: restart current round (preserve sequence)
                    t.restart_round();
                } else {
                    // Idle: full sequence reset
                    t.reset();
                }
            }
        }
        "getTheme" => {
            // Send current theme colors
            if let Some(db) = app.try_state::<crate::db::DbState>() {
                if let Ok(conn) = db.lock() {
                    if let Ok(s) = crate::settings::load(&conn) {
                        let data_dir = match app.path().app_data_dir() { Ok(d) => d, Err(_) => return };
                        let _osd = true; // assume dark
                        let name = if s.theme_mode == "light" { &s.theme_light } else { &s.theme_dark };
                        if let Some(theme) = crate::themes::find(&data_dir, name) {
                            let payload = serde_json::json!({ "name": theme.name, "colors": theme.colors });
                            let _ = _broadcast_tx.send(WsEvent::ThemeChanged { payload });
                        }
                    }
                }
            }
        }
        "getFlow" => {
            if let Some(ft) = app.try_state::<std::sync::Arc<crate::flow::FlowTracker>>() {
                let snap = ft.snapshot();
                let _ = _broadcast_tx.send(WsEvent::FlowChanged { payload: snap });
            }
        }
        "toggleWindow" => {
            if let Some(window) = app.get_webview_window("main") {
                if window.is_visible().unwrap_or(false) { let _ = window.hide(); }
                else { let _ = window.show(); let _ = window.unminimize(); let _ = window.set_focus(); }
            }
        }
        "openTasks" => {
            if let Some(window) = app.get_webview_window("main") {
                let _ = window.show(); let _ = window.unminimize(); let _ = window.set_focus();
            }
            let _ = app.emit("navigate:tasks", ());
        }
        "openStats" | "toggleStats" => {
            let stats_exists = app.get_webview_window("stats").is_some();
            if msg_type == "toggleStats" && stats_exists {
                if let Some(w) = app.get_webview_window("stats") { let _ = w.close(); }
            } else {
                if let Some(window) = app.get_webview_window("main") {
                    let _ = window.show(); let _ = window.unminimize(); let _ = window.set_focus();
                }
                let _ = app.emit("navigate:stats", ());
            }
        }
        "closeStats" => {
            if let Some(w) = app.get_webview_window("stats") { let _ = w.close(); }
        }
        "openStreak" => {
            if let Some(window) = app.get_webview_window("main") {
                let _ = window.show(); let _ = window.unminimize(); let _ = window.set_focus();
            }
            let _ = app.emit("navigate:streak", ());
        }
        "setSetting" => {
            if let (Some(key), Some(value)) = (
                msg.get("key").and_then(|k| k.as_str()),
                msg.get("value").and_then(|v| v.as_str()),
            ) {
                if let Some(db) = app.try_state::<crate::db::DbState>() {
                    if let Ok(conn) = db.lock() {
                        let _ = crate::settings::save_setting(&conn, key, value);
                        if let Ok(s) = crate::settings::load(&conn) {
                            if let Some(timer) = app.try_state::<TimerController>() {
                                timer.apply_settings(s.clone());
                                let snap = timer.get_snapshot();
                                if !snap.is_running {
                                    let _ = app.emit("timer:reset", &snap);
                                    // Broadcast state for duration changes so the plugin
                                    // widget sees the new total_secs immediately on dial wind
                                    if key.starts_with("time_") {
                                        let _ = _broadcast_tx.send(WsEvent::State { payload: snap });
                                    }
                                }
                            }
                            if let Some(audio) = app.try_state::<Arc<crate::audio::AudioManager>>() {
                                audio.apply_settings(&s);
                            }
                            let _ = app.emit("settings:changed", &s);
                        }
                    }
                }
                log::debug!("[ws] setSetting key={} value={}", 
                    msg.get("key").and_then(|k| k.as_str()).unwrap_or("?"),
                    msg.get("value").and_then(|v| v.as_str()).unwrap_or("?"));
            }
        }
        "getTasks" => {
            if let Some(db) = app.try_state::<crate::db::DbState>() {
                if let Ok(conn) = db.lock() {
                    if let Ok(tasks) = crate::db::queries::task_list(&conn) {
                        let payload = serde_json::to_value(&tasks).unwrap_or_default();
                        let _ = _broadcast_tx.send(WsEvent::TasksChanged { payload });
                    }
                }
            }
        }
        "setActiveTask" => {
            if let Some(id) = msg.get("id").and_then(|v| v.as_i64()) {
                if let Some(db) = app.try_state::<crate::db::DbState>() {
                    if let Ok(conn) = db.lock() {
                        let _ = crate::db::queries::task_set_active(&conn, id);
                        let _ = app.emit("tasks:changed", ());
                        if let Ok(tasks) = crate::db::queries::task_list(&conn) {
                            let payload = serde_json::to_value(&tasks).unwrap_or_default();
                            let _ = _broadcast_tx.send(WsEvent::TasksChanged { payload });
                        }
                    }
                }
            }
        }
        "completeTask" => {
            if let Some(id) = msg.get("id").and_then(|v| v.as_i64()) {
                if let Some(db) = app.try_state::<crate::db::DbState>() {
                    if let Ok(conn) = db.lock() {
                        let _ = crate::db::queries::complete_task(&conn, id);
                        let _ = app.emit("tasks:changed", ());
                        // Don't play TaskDone here — the plugin plays it locally
                        // when connected. Only the IPC command (from app UI) plays it.
                        if let Ok(tasks) = crate::db::queries::task_list(&conn) {
                            let payload = serde_json::to_value(&tasks).unwrap_or_default();
                            let _ = _broadcast_tx.send(WsEvent::TasksChanged { payload });
                        }
                    }
                }
            }
        }
        "getJar" => {
            if let Some(db) = app.try_state::<crate::db::DbState>() {
                if let Ok(conn) = db.lock() {
                    if let Ok(entries) = crate::db::queries::jar_today(&conn) {
                        let payload = serde_json::json!({ "tomatoes": entries });
                        let _ = _broadcast_tx.send(WsEvent::JarChanged { payload });
                    }
                }
            }
        }
        "setSkin" => {
            if let Some(id) = msg.get("id").and_then(|v| v.as_str()) {
                let payload = serde_json::json!({ "id": id });
                let _ = _broadcast_tx.send(WsEvent::SkinChanged { payload });
            }
        }
        _ => { log::debug!("[ws] unknown message type: {msg_type}"); }
    }
}

// ---------------------------------------------------------------------------
// Public API for broadcasting from the timer event listener
// ---------------------------------------------------------------------------

/// Broadcast a `started` event to all connected WebSocket clients.
pub fn broadcast_started(state: &Arc<WsState>, total_secs: u32) {
    let _ = state.broadcast_tx.send(WsEvent::Started { payload: StartedPayload { total_secs } });
}

/// Broadcast a `roundChange` event to all connected WebSocket clients.
pub fn broadcast_round_change(state: &Arc<WsState>, snapshot: TimerSnapshot) {
    let _ = state.broadcast_tx.send(WsEvent::RoundChange { payload: snapshot });
}

/// Broadcast a `paused` event to all connected WebSocket clients.
pub fn broadcast_paused(state: &Arc<WsState>, elapsed_secs: u32) {
    let _ = state.broadcast_tx.send(WsEvent::Paused { payload: ElapsedPayload { elapsed_secs } });
}

/// Broadcast a `resumed` event to all connected WebSocket clients.
pub fn broadcast_resumed(state: &Arc<WsState>, elapsed_secs: u32) {
    let _ = state.broadcast_tx.send(WsEvent::Resumed { payload: ElapsedPayload { elapsed_secs } });
}

/// Broadcast a `reset` event to all connected WebSocket clients.
pub fn broadcast_reset(state: &Arc<WsState>) {
    let _ = state.broadcast_tx.send(WsEvent::Reset);
}

pub fn broadcast_flow(state: &Arc<WsState>, snapshot: crate::flow::FlowSnapshot) {
    let _ = state.broadcast_tx.send(WsEvent::FlowChanged { payload: snapshot });
}

pub fn broadcast_jar(state: &Arc<WsState>, payload: serde_json::Value) {
    let _ = state.broadcast_tx.send(WsEvent::JarChanged { payload });
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn ws_state_can_be_created() {
        let state = WsState::new();
        // broadcast_tx should have 0 receivers initially.
        assert_eq!(state.broadcast_tx.receiver_count(), 0);
    }

    #[test]
    fn ws_event_serializes_correctly() {
        use crate::timer::TimerSnapshot;
        let snap = TimerSnapshot {
            round_type: "work".into(),
            previous_round_type: "short-break".into(),
            elapsed_secs: 60,
            total_secs: 1500,
            is_running: true,
            is_paused: false,
            work_round_number: 1,
            work_rounds_total: 4,
            session_work_count: 1,
        };
        let event = WsEvent::RoundChange { payload: snap };
        let json = serde_json::to_string(&event).unwrap();
        assert!(json.contains("\"type\":\"roundChange\""));
        assert!(json.contains("\"elapsed_secs\":60"));
    }

    #[test]
    fn ws_event_started_serializes_correctly() {
        let event = WsEvent::Started { payload: StartedPayload { total_secs: 1500 } };
        let json = serde_json::to_string(&event).unwrap();
        assert!(json.contains("\"type\":\"started\""));
        assert!(json.contains("\"total_secs\":1500"));
    }

    #[test]
    fn ws_event_paused_serializes_correctly() {
        let event = WsEvent::Paused { payload: ElapsedPayload { elapsed_secs: 300 } };
        let json = serde_json::to_string(&event).unwrap();
        assert!(json.contains("\"type\":\"paused\""));
        assert!(json.contains("\"elapsed_secs\":300"));
    }

    #[test]
    fn ws_event_resumed_serializes_correctly() {
        let event = WsEvent::Resumed { payload: ElapsedPayload { elapsed_secs: 180 } };
        let json = serde_json::to_string(&event).unwrap();
        assert!(json.contains("\"type\":\"resumed\""));
        assert!(json.contains("\"elapsed_secs\":180"));
    }

    #[test]
    fn ws_event_reset_serializes_correctly() {
        let event = WsEvent::Reset;
        let json = serde_json::to_string(&event).unwrap();
        assert_eq!(json, r#"{"type":"reset"}"#);
    }
}
