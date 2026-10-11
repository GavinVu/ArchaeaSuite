//! Optional local AI assistance: a client for an Ollama-compatible server
//! running on this computer. Requests are refused for any host that is not
//! loopback, so code can never be sent off the machine by this feature.

use crate::{err, Res, State};
use serde::Serialize;
use serde_json::{json, Value};
use std::collections::HashMap;
use std::io::{BufRead, BufReader};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Duration;
use tauri::{AppHandle, Emitter, Manager};

#[derive(Default)]
pub struct Chats(Mutex<HashMap<String, Arc<AtomicBool>>>);

#[derive(Clone, Serialize)]
struct Chunk<'a> {
    id: &'a str,
    text: String,
    done: bool,
    error: Option<String>,
}

/// Accepts only `http://` URLs whose host is this computer.
pub fn loopback(base: &str) -> Res<String> {
    let base = base.trim().trim_end_matches('/');
    let rest = base.strip_prefix("http://").ok_or("The AI server address must start with http://")?;
    let authority = rest.split('/').next().unwrap_or("");
    let host = match authority.strip_prefix('[') {
        Some(v6) => v6.split(']').next().unwrap_or(""),
        None => authority.split(':').next().unwrap_or(""),
    };
    if authority.contains('@') || !matches!(host, "127.0.0.1" | "localhost" | "::1") {
        return Err("Only an AI server on this computer (127.0.0.1 or localhost) is allowed.".into());
    }
    Ok(base.to_string())
}

fn agent() -> ureq::Agent {
    ureq::AgentBuilder::new().timeout_connect(Duration::from_secs(3)).redirects(0).build()
}

#[tauri::command(async)]
pub fn ai_models(base_url: String) -> Res<Vec<String>> {
    let url = format!("{}/api/tags", loopback(&base_url)?);
    let body: Value = agent()
        .get(&url)
        .timeout(Duration::from_secs(5))
        .call()
        .map_err(|e| format!("No AI server answered at {base_url} ({e})."))?
        .into_json()
        .map_err(err)?;
    Ok(body["models"]
        .as_array()
        .map(|m| m.iter().filter_map(|x| x["name"].as_str().map(String::from)).collect())
        .unwrap_or_default())
}

/// Streams a chat reply as `ai:chunk` events; the last one has `done: true`.
#[tauri::command(async)]
pub fn ai_chat(app: AppHandle, id: String, base_url: String, model: String, messages: Value) -> Res<()> {
    let url = format!("{}/api/chat", loopback(&base_url)?);
    let cancel = Arc::new(AtomicBool::new(false));
    app.state::<State>().ai.0.lock().unwrap().insert(id.clone(), cancel.clone());
    let finish = |error: Option<String>| {
        app.state::<State>().ai.0.lock().unwrap().remove(&id);
        let _ = app.emit("ai:chunk", Chunk { id: &id, text: String::new(), done: true, error });
    };
    let response = match agent().post(&url).send_json(json!({ "model": model, "messages": messages, "stream": true })) {
        Ok(r) => r,
        Err(ureq::Error::Status(code, r)) => {
            let detail = r.into_string().unwrap_or_default();
            finish(Some(format!("The AI server answered {code}: {detail}")));
            return Ok(());
        }
        Err(e) => {
            finish(Some(format!("Could not reach the AI server ({e}).")));
            return Ok(());
        }
    };
    for line in BufReader::new(response.into_reader()).lines() {
        if cancel.load(Ordering::Relaxed) {
            break;
        }
        let Ok(line) = line else { break };
        let Ok(part) = serde_json::from_str::<Value>(&line) else { continue };
        if let Some(e) = part["error"].as_str() {
            finish(Some(e.to_string()));
            return Ok(());
        }
        if let Some(text) = part["message"]["content"].as_str().filter(|t| !t.is_empty()) {
            let _ = app.emit("ai:chunk", Chunk { id: &id, text: text.to_string(), done: false, error: None });
        }
        if part["done"].as_bool() == Some(true) {
            break;
        }
    }
    finish(None);
    Ok(())
}

#[tauri::command]
pub fn ai_cancel(state: tauri::State<State>, id: String) {
    if let Some(flag) = state.ai.0.lock().unwrap().get(&id) {
        flag.store(true, Ordering::Relaxed);
    }
}

#[cfg(test)]
mod tests {
    use super::loopback;

    #[test]
    fn only_loopback_hosts_are_accepted() {
        for ok in ["http://127.0.0.1:11434", "http://localhost:11434/", "http://[::1]:11434", "http://localhost"] {
            assert!(loopback(ok).is_ok(), "{ok}");
        }
        for bad in [
            "https://127.0.0.1:11434",
            "http://example.com",
            "http://127.0.0.1.evil.com",
            "http://localhost@evil.com",
            "http://evil.com/127.0.0.1",
            "http://192.168.1.5:11434",
        ] {
            assert!(loopback(bad).is_err(), "{bad}");
        }
    }
}
