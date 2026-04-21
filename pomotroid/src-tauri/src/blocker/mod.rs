/// App Blocker — minimizes distracting applications during focus sessions.
pub mod web;

use std::sync::{Arc, Mutex};
use std::collections::HashSet;
use tauri::{Emitter, Manager};

pub struct AppBlocker { inner: Mutex<BlockerInner> }
struct BlockerInner { active: bool, blocked_processes: HashSet<String> }

#[derive(Clone, serde::Serialize)]
pub struct AppBlockedEvent { pub process_name: String }

impl AppBlocker {
    pub fn new() -> Arc<Self> {
        Arc::new(Self { inner: Mutex::new(BlockerInner { active: false, blocked_processes: HashSet::new() }) })
    }
    pub fn set_blocked_apps(&self, apps: &[String]) {
        let mut inner = self.inner.lock().unwrap();
        inner.blocked_processes.clear();
        for app in apps {
            let mut name = app.trim().to_lowercase();
            // Strip .exe suffix if present — we match on process name without extension
            if name.ends_with(".exe") { name = name[..name.len() - 4].to_string(); }
            if !name.is_empty() { inner.blocked_processes.insert(name); }
        }
    }
    pub fn start(&self) { self.inner.lock().unwrap().active = true; }
    pub fn stop(&self) { self.inner.lock().unwrap().active = false; }
    pub fn is_active(&self) -> bool { self.inner.lock().unwrap().active }
    pub fn check_and_block(&self) -> Option<String> {
        let (_active, blocked) = {
            let inner = self.inner.lock().unwrap();
            if !inner.active || inner.blocked_processes.is_empty() { return None; }
            (inner.active, inner.blocked_processes.clone())
        };
        check_and_minimize(&blocked)
    }
}

#[cfg(target_os = "windows")]
fn check_and_minimize(blocked: &HashSet<String>) -> Option<String> {
    use std::ffi::OsString;
    use std::os::windows::ffi::OsStringExt;
    unsafe {
        let hwnd = windows_sys::Win32::UI::WindowsAndMessaging::GetForegroundWindow();
        if hwnd == std::ptr::null_mut() { return None; }
        let mut pid: u32 = 0;
        windows_sys::Win32::UI::WindowsAndMessaging::GetWindowThreadProcessId(hwnd, &mut pid);
        if pid == 0 { return None; }
        let handle = windows_sys::Win32::System::Threading::OpenProcess(0x1000, 0, pid);
        if handle == std::ptr::null_mut() { return None; }
        let mut buf = [0u16; 512];
        let mut size = buf.len() as u32;
        let ok = windows_sys::Win32::System::Threading::QueryFullProcessImageNameW(handle, 0, buf.as_mut_ptr(), &mut size);
        windows_sys::Win32::Foundation::CloseHandle(handle);
        if ok == 0 { return None; }
        let path = OsString::from_wide(&buf[..size as usize]);
        let path_str = path.to_string_lossy().to_lowercase();
        let exe_name = path_str.rsplit('\\').next().unwrap_or("").trim_end_matches(".exe");
        if exe_name.is_empty() { return None; }
        let is_blocked = blocked.iter().any(|b| exe_name == b.as_str() || exe_name.contains(b.as_str()));
        if is_blocked {
            windows_sys::Win32::UI::WindowsAndMessaging::ShowWindow(hwnd, 6);
            log::info!("[blocker] minimized: {exe_name}");
            return Some(exe_name.to_string());
        }
        None
    }
}

#[cfg(not(target_os = "windows"))]
fn check_and_minimize(_blocked: &HashSet<String>) -> Option<String> { None }

pub fn spawn_poll_thread(blocker: Arc<AppBlocker>, app: tauri::AppHandle) -> std::thread::JoinHandle<()> {
    std::thread::spawn(move || {
        let mut last_fg = String::new();
        loop {
            if !blocker.is_active() { std::thread::sleep(std::time::Duration::from_millis(2000)); continue; }
            std::thread::sleep(std::time::Duration::from_millis(500));
            if let Some(name) = blocker.check_and_block() {
                let _ = app.emit("blocker:app-blocked", AppBlockedEvent { process_name: name });
                if let Some(ft) = app.try_state::<Arc<crate::flow::FlowTracker>>() {
                    let snap = ft.on_distraction();
                    emit_flow(&app, &snap);
                }
            }
            #[cfg(target_os = "windows")]
            {
                if let Some(fg) = get_foreground_process() {
                    if fg != last_fg { last_fg = fg.clone();
                        if let Some(ft) = app.try_state::<Arc<crate::flow::FlowTracker>>() {
                            if let Some(snap) = ft.on_window_change(&fg) { emit_flow(&app, &snap); }
                        }
                    }
                }
                let idle_secs = get_idle_seconds();
                if let Some(ft) = app.try_state::<Arc<crate::flow::FlowTracker>>() {
                    if let Some(snap) = ft.on_idle_update(idle_secs) { emit_flow(&app, &snap); }
                }
            }
        }
    })
}

fn emit_flow(app: &tauri::AppHandle, snap: &crate::flow::FlowSnapshot) {
    let _ = app.emit("flow:changed", snap);
    if let Some(ws) = app.try_state::<Arc<crate::websocket::WsState>>() {
        let _ = ws.broadcast_tx.send(crate::websocket::WsEvent::FlowChanged { payload: snap.clone() });
    }
}

#[cfg(target_os = "windows")]
fn get_foreground_process() -> Option<String> {
    use std::ffi::OsString; use std::os::windows::ffi::OsStringExt;
    unsafe {
        let hwnd = windows_sys::Win32::UI::WindowsAndMessaging::GetForegroundWindow();
        if hwnd == std::ptr::null_mut() { return None; }
        let mut pid: u32 = 0;
        windows_sys::Win32::UI::WindowsAndMessaging::GetWindowThreadProcessId(hwnd, &mut pid);
        if pid == 0 { return None; }
        let handle = windows_sys::Win32::System::Threading::OpenProcess(0x1000, 0, pid);
        if handle == std::ptr::null_mut() { return None; }
        let mut buf = [0u16; 512]; let mut size = buf.len() as u32;
        let ok = windows_sys::Win32::System::Threading::QueryFullProcessImageNameW(handle, 0, buf.as_mut_ptr(), &mut size);
        windows_sys::Win32::Foundation::CloseHandle(handle);
        if ok == 0 { return None; }
        let path = OsString::from_wide(&buf[..size as usize]);
        let name = path.to_string_lossy().to_lowercase().rsplit('\\').next().unwrap_or("").trim_end_matches(".exe").to_string();
        if name.is_empty() { None } else { Some(name) }
    }
}

#[cfg(target_os = "windows")]
fn get_idle_seconds() -> u32 {
    unsafe {
        #[repr(C)] struct LastInputInfo { cb_size: u32, dw_time: u32 }
        #[link(name = "user32")] extern "system" { fn GetLastInputInfo(plii: *mut LastInputInfo) -> i32; fn GetTickCount() -> u32; }
        let mut lii = LastInputInfo { cb_size: 8, dw_time: 0 };
        if GetLastInputInfo(&mut lii) != 0 { (GetTickCount().wrapping_sub(lii.dw_time)) / 1000 } else { 0 }
    }
}
