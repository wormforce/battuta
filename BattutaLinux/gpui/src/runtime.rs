use serde::Deserialize;
use serde_json::Value;
use std::{
    io::{BufRead, BufReader, Write},
    process::{Command, Stdio},
    sync::mpsc::{self, Receiver, Sender},
};

pub struct Runtime {
    requests: Sender<Value>,
}

impl Runtime {
    pub fn start() -> (Self, async_channel::Receiver<Result<Snapshot, String>>) {
        let (requests, commands) = mpsc::channel();
        let (replies, snapshots) = async_channel::unbounded();
        std::thread::spawn(move || {
            if let Err(error) = run(commands, &replies) {
                let _ = replies.send_blocking(Err(error));
            }
        });
        (Self { requests }, snapshots)
    }

    pub fn send(&self, request: Value) -> bool {
        self.requests.send(request).is_ok()
    }
}

fn run(
    commands: Receiver<Value>,
    replies: &async_channel::Sender<Result<Snapshot, String>>,
) -> Result<(), String> {
    let executable = std::env::var_os("BATTUTA_RUNTIME")
        .map(Into::into)
        .unwrap_or_else(|| {
            std::env::current_exe()
                .unwrap_or_default()
                .with_file_name("Battuta.Linux")
        });
    let mut child = Command::new(&executable)
        .arg("--gpui-runtime")
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .spawn()
        .map_err(|error| format!("{}: {error}", executable.display()))?;
    let mut stdin = child.stdin.take().unwrap();
    let stdout = child.stdout.take().unwrap();
    let reader = replies.clone();
    let read = std::thread::spawn(move || {
        for line in BufReader::new(stdout).lines() {
            let reply = line.map_err(|error| error.to_string()).and_then(|line| {
                serde_json::from_str::<Snapshot>(&line).map_err(|error| error.to_string())
            });
            if reader.send_blocking(reply).is_err() {
                return;
            }
        }
        let _ = reader.send_blocking(Err("Battuta runtime stopped".into()));
    });
    for command in commands {
        if writeln!(stdin, "{command}")
            .and_then(|_| stdin.flush())
            .is_err()
        {
            break;
        }
    }
    drop(stdin);
    let _ = child.wait();
    let _ = read.join();
    Ok(())
}

#[derive(Default, Deserialize, PartialEq)]
#[serde(default, rename_all = "camelCase")]
pub struct Snapshot {
    pub settings: Option<Settings>,
    pub profiles: Option<Vec<Profile>>,
    pub pointer_profiles: Option<Vec<Profile>>,
    pub connected: bool,
    pub activate: bool,
    pub primary: bool,
    pub secondary: bool,
    pub audio_ready: bool,
    pub broker_phase: Option<String>,
    pub broker_detail: Option<String>,
    pub authorization: Option<String>,
    pub sound_pack_error: Option<String>,
    pub update_status: Option<String>,
    pub release_url: Option<String>,
    pub today: Today,
    pub stats: Option<Stats>,
    pub report: Option<Report>,
    pub diy: Option<Diy>,
    pub version: String,
    pub error: Option<String>,
    pub fatal: Option<String>,
}

#[derive(Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct Settings {
    pub is_enabled: bool,
    pub is_pointer_sound_enabled: bool,
    pub is_typing_stats_enabled: bool,
    pub plays_release_sound: bool,
    pub plays_key_repeat_sound: bool,
    pub plays_pointer_release_sound: bool,
    pub uses_pitch_variation: bool,
    pub selected_profile_id: String,
    pub selected_pointer_profile_id: String,
    pub volume: f64,
    pub pointer_volume: f64,
    pub is_launch_at_login_enabled: bool,
    pub automatic_update_check_preference: u8,
}

#[derive(Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct Profile {
    #[serde(alias = "selectionId")]
    pub id: String,
    pub display_name: String,
}

#[derive(Default, Deserialize, PartialEq)]
pub struct Today {
    pub count: u64,
    pub peak: u64,
    pub app: String,
}

#[derive(Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct Stats {
    pub today_active_minutes: u64,
    pub today_physical_presses: u64,
    pub data_source_text: String,
    pub status_message: Option<String>,
    pub range: String,
    pub is_all_time_heatmap_scope: bool,
    pub bars: Vec<Bar>,
    pub apps: Vec<AppUsage>,
    pub history: Vec<Day>,
    pub keys: Vec<Keycap>,
}

#[derive(Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct Bar {
    pub label: String,
    pub count: u64,
    pub height_fraction: f32,
}

#[derive(Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct AppUsage {
    pub display_name: String,
    pub character_count: u64,
    pub share: f32,
}

#[derive(Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct Day {
    pub date_text: String,
    pub character_count: u64,
    pub peak_cps: u64,
    pub top_application_name: Option<String>,
}

#[derive(Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct Keycap {
    pub id: String,
    pub label: String,
    pub key: Option<String>,
    pub x_units: f32,
    pub y_units: f32,
    pub width_units: f32,
    pub height_units: f32,
    pub count: u64,
    pub intensity: f32,
    pub extended: bool,
}

#[derive(Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct Diy {
    pub packs: Vec<Choice>,
    pub selected_pack_id: Option<String>,
    pub has_draft: bool,
    pub is_dirty: bool,
    pub can_delete: bool,
    pub can_export: bool,
    pub status_message: Option<String>,
    pub error: Option<String>,
    pub name: Option<String>,
    pub author: Option<String>,
    pub notes: Option<String>,
    pub selected_key: String,
    pub mode: String,
    pub slot: Option<String>,
    pub slot_name: String,
    pub assets: Vec<Choice>,
    pub press: String,
    pub release: String,
    pub keys: Vec<Keycap>,
    pub split: Option<Split>,
}

#[derive(Deserialize, PartialEq)]
pub struct Choice {
    pub id: String,
    pub name: String,
}

#[derive(Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct Split {
    pub id: String,
    pub duration_seconds: f64,
    pub start: f64,
    pub end: f64,
    pub confidence: f32,
    pub warnings: Vec<String>,
    pub waveform: Vec<WavePoint>,
}

#[derive(Deserialize, PartialEq)]
pub struct WavePoint {
    pub minimum: f32,
    pub maximum: f32,
}

#[derive(Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct Report {
    pub current: u64,
    pub previous: u64,
    pub active_day_count: u32,
    pub longest_active_day_streak: u32,
    pub days: Vec<ReportDay>,
    pub apps: Vec<ReportApp>,
}

#[derive(Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct ReportDay {
    pub date: String,
    pub character_count: u64,
    pub weekday: usize,
}

#[derive(Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct ReportApp {
    pub display_name: String,
    pub character_count: u64,
    pub comparison_character_count: u64,
}
