/// Web blocker — PAC proxy + intercepting proxy + focus page.
///
/// Architecture:
///   - PAC server (port 9998): serves PAC file routing blocked domains to our proxy
///   - Intercepting proxy (port 9999): handles both HTTP and HTTPS CONNECT requests,
///     returns the focus page for all blocked traffic
///   - Focus page: branded "Stay Focused" HTML served for any blocked request
///   - Windows registry: sets AutoConfigURL to point to our PAC server
///   - WinInet notification: forces browsers to re-read proxy settings instantly

use std::sync::Arc;
use tokio::sync::Mutex;
use tokio::io::AsyncWriteExt;

pub struct WebBlockerState {
    pub active: Mutex<bool>,
    pub sites: Mutex<Vec<String>>,
    servers_started: Mutex<bool>,
}

impl WebBlockerState {
    pub fn new() -> Arc<Self> {
        Arc::new(Self {
            active: Mutex::new(false),
            sites: Mutex::new(Vec::new()),
            servers_started: Mutex::new(false),
        })
    }
}

pub async fn start(state: &Arc<WebBlockerState>, sites: Vec<String>) {
    *state.sites.lock().await = sites;
    *state.active.lock().await = true;

    let mut started = state.servers_started.lock().await;
    if !*started {
        let s1 = Arc::clone(state);
        tokio::spawn(async move { run_pac_server(s1).await });
        tokio::spawn(async move { run_proxy_server().await });
        *started = true;
    }

    set_proxy_pac("http://127.0.0.1:9998/proxy.pac");
    notify_wininet();
    log::info!("[web-blocker] activated");
}

pub async fn stop(state: &Arc<WebBlockerState>) {
    *state.active.lock().await = false;
    clear_proxy_pac();
    notify_wininet();
    log::info!("[web-blocker] deactivated");
}

pub fn emergency_cleanup() {
    clear_proxy_pac();
    notify_wininet();
    log::info!("[web-blocker] emergency cleanup");
}

/// Re-set the PAC URL and notify browsers. Used for hot-reload after site list changes.
pub fn set_and_notify() {
    set_proxy_pac("http://127.0.0.1:9998/proxy.pac");
    notify_wininet();
}

// --- PAC server (port 9998) ---

async fn run_pac_server(state: Arc<WebBlockerState>) {
    use axum::{routing::get, Router};
    let app = Router::new().route("/proxy.pac", get(serve_pac)).with_state(state);
    let addr: std::net::SocketAddr = ([127, 0, 0, 1], 9998).into();
    let listener = match tokio::net::TcpListener::bind(addr).await {
        Ok(l) => l, Err(e) => { log::error!("[web-blocker] PAC bind failed: {e}"); return; }
    };
    log::info!("[web-blocker] PAC server on :9998");
    let _ = axum::serve(listener, app).await;
}

async fn serve_pac(
    axum::extract::State(state): axum::extract::State<Arc<WebBlockerState>>,
) -> impl axum::response::IntoResponse {
    let active = *state.active.lock().await;
    let sites = state.sites.lock().await.clone();
    let body = if active && !sites.is_empty() {
        let conds: Vec<String> = sites.iter().map(|s| {
            let d = s.trim().to_lowercase();
            format!(r#"    if (dnsDomainIs(host, "{d}") || dnsDomainIs(host, ".{d}")) return "PROXY 127.0.0.1:9999";"#)
        }).collect();
        format!("function FindProxyForURL(url, host) {{\n{}\n    return \"DIRECT\";\n}}", conds.join("\n"))
    } else {
        "function FindProxyForURL(url, host) { return \"DIRECT\"; }".to_string()
    };
    ([
        ("content-type", "application/x-ns-proxy-autoconfig"),
        ("cache-control", "no-cache, no-store, must-revalidate"),
    ], body)
}

// --- Intercepting proxy (port 9999) ---
// Handles both HTTP GET/POST and HTTPS CONNECT requests.
// For CONNECT: sends 200 OK, then serves focus page as HTTP over the raw socket.
// For HTTP: serves focus page directly.

async fn run_proxy_server() {
    let addr: std::net::SocketAddr = ([127, 0, 0, 1], 9999).into();
    let listener = match tokio::net::TcpListener::bind(addr).await {
        Ok(l) => l, Err(e) => { log::error!("[web-blocker] proxy bind failed: {e}"); return; }
    };
    log::info!("[web-blocker] proxy on :9999");

    loop {
        let (stream, _) = match listener.accept().await {
            Ok(s) => s, Err(_) => continue,
        };
        tokio::spawn(async move {
            if let Err(e) = handle_proxy_connection(stream).await {
                log::debug!("[web-blocker] proxy conn error: {e}");
            }
        });
    }
}

async fn handle_proxy_connection(mut stream: tokio::net::TcpStream) -> Result<(), Box<dyn std::error::Error>> {
    use tokio::io::AsyncReadExt;

    let mut buf = vec![0u8; 4096];
    let n = stream.read(&mut buf).await?;
    if n == 0 { return Ok(()); }

    let request = String::from_utf8_lossy(&buf[..n]);
    let first_line = request.lines().next().unwrap_or("");

    if first_line.starts_with("CONNECT") {
        // HTTPS: reject the tunnel with 403 + focus page HTML.
        // The browser shows "proxy refused connection" but our HTML is in the response body.
        let html = FOCUS_HTML;
        let response = format!(
            "HTTP/1.1 403 Blocked by PomoDeck\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{}",
            html.len(), html
        );
        stream.write_all(response.as_bytes()).await?;
    } else {
        // HTTP: serve focus page directly
        let html = FOCUS_HTML;
        let response = format!(
            "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{}",
            html.len(), html
        );
        stream.write_all(response.as_bytes()).await?;
    }

    let _ = stream.shutdown().await;
    Ok(())
}

const FOCUS_HTML: &str = r#"<!DOCTYPE html>
<html><head><meta charset="utf-8"><title>Stay Focused — PomoDeck</title>
<style>
*{margin:0;padding:0;box-sizing:border-box}
body{background:#1a1d23;color:#e0e0e0;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif;
display:flex;align-items:center;justify-content:center;height:100vh;text-align:center}
.c{max-width:500px;padding:40px}
.logo{font-size:64px;margin-bottom:8px}
h1{font-size:32px;color:#00d4aa;margin-bottom:6px;font-weight:700;letter-spacing:-0.02em}
.sub{font-size:14px;color:#00d4aa;opacity:0.6;margin-bottom:24px}
p{font-size:16px;color:#666;line-height:1.6}
.hint{margin-top:32px;font-size:12px;color:#444;border-top:1px solid #2a2d33;padding-top:16px}
.bar{width:60px;height:3px;background:#00d4aa;border-radius:2px;margin:20px auto;opacity:0.3;
animation:pulse 2s ease-in-out infinite}
@keyframes pulse{0%,100%{opacity:0.2;width:60px}50%{opacity:0.5;width:100px}}
</style></head>
<body><div class="c">
<div class="logo">🍅</div>
<h1>Stay Focused</h1>
<p class="sub">PomoDeck</p>
<div class="bar"></div>
<p>This site is blocked during your focus session.</p>
<p class="hint">Blocking ends automatically when your break starts.<br>Close this tab and get back to what matters.</p>
</div></body></html>"#;

// --- Windows proxy registry ---

#[cfg(target_os = "windows")]
fn set_proxy_pac(url: &str) {
    match winreg::RegKey::predef(winreg::enums::HKEY_CURRENT_USER)
        .open_subkey_with_flags(r"Software\Microsoft\Windows\CurrentVersion\Internet Settings", winreg::enums::KEY_SET_VALUE) {
        Ok(key) => {
            match key.set_value("AutoConfigURL", &url) {
                Ok(_) => log::info!("[web-blocker] set AutoConfigURL={url}"),
                Err(e) => log::error!("[web-blocker] failed to set AutoConfigURL: {e}"),
            }
        }
        Err(e) => log::error!("[web-blocker] failed to open registry: {e}"),
    }
}

#[cfg(target_os = "windows")]
fn clear_proxy_pac() {
    if let Ok(key) = winreg::RegKey::predef(winreg::enums::HKEY_CURRENT_USER)
        .open_subkey_with_flags(r"Software\Microsoft\Windows\CurrentVersion\Internet Settings", winreg::enums::KEY_SET_VALUE) {
        let _ = key.delete_value("AutoConfigURL");
        log::debug!("[web-blocker] cleared AutoConfigURL");
    }
}

#[cfg(target_os = "windows")]
pub fn notify_wininet() {
    #[link(name = "wininet")]
    extern "system" {
        fn InternetSetOptionW(h: *mut std::ffi::c_void, opt: u32, buf: *mut std::ffi::c_void, len: u32) -> i32;
    }
    unsafe {
        InternetSetOptionW(std::ptr::null_mut(), 39, std::ptr::null_mut(), 0);
        InternetSetOptionW(std::ptr::null_mut(), 37, std::ptr::null_mut(), 0);
    }
}

#[cfg(not(target_os = "windows"))]
fn set_proxy_pac(_url: &str) {}
#[cfg(not(target_os = "windows"))]
fn clear_proxy_pac() {}
#[cfg(not(target_os = "windows"))]
pub fn notify_wininet() {}
