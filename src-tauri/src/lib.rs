use std::{
    ffi::c_char,
    io::{Cursor, Read},
    path::{Path, PathBuf},
    sync::Once,
};

use netcorehost::{hostfxr::AssemblyDelegateLoader, pdcstr};
use reqwest::Client;
use std::ffi::CString;
use sysinfo::{ProcessRefreshKind, RefreshKind, System, UpdateKind};
use tauri::State;
use tokio::sync::Mutex;
use uuid::Uuid;
use zip::ZipArchive;

// Learn more about Tauri commands at https://tauri.app/develop/calling-rust/
#[tauri::command]
fn check_for_process(name: &str) -> bool {
    let system = System::new_with_specifics(
        RefreshKind::new().with_processes(ProcessRefreshKind::new().with_exe(UpdateKind::Always)),
    );

    return system
        .processes()
        .values()
        .flat_map(|process| process.exe())
        .flat_map(|path| path.file_name())
        .flat_map(|file| file.to_str())
        .any(|file| file == name);
}

fn xl_path() -> Option<PathBuf> {
    let dir = match dirs::config_dir() {
        Some(d) => d,
        None => return None,
    };

    Some(dir.join("XIVLauncher"))
}

fn dalamud_config_path() -> Option<PathBuf> {
    xl_path().map(|p| p.join("dalamudConfig.json"))
}

#[tauri::command]
fn dalamud_config_present() -> bool {
    dalamud_config_path()
        .map(|p| p.exists())
        .unwrap_or_default()
}

#[tauri::command]
async fn get_dalamud_config_json() -> Option<String> {
    let config_path = dalamud_config_path()?;
    tokio::fs::read_to_string(&config_path).await.ok()
}

#[tauri::command]
async fn write_dalamud_config_json(json: &str) -> Result<(), String> {
    let config_path = dalamud_config_path()
        .ok_or_else(|| "could not determine dalamud config path".to_string())?;
    tokio::fs::write(&config_path, json)
        .await
        .map_err(|e| format!("{e:#}"))
}

fn plugin_config_path(internal_name: &str) -> Option<PathBuf> {
    xl_path().map(|path| {
        path.join("pluginConfigs")
            .join(&format!("{internal_name}.json"))
    })
}

#[tauri::command]
async fn get_plugin_config_json(internal_name: &str) -> Result<String, String> {
    let config_path =
        plugin_config_path(internal_name).ok_or_else(|| "could not get config path".to_string())?;
    tokio::fs::read_to_string(&config_path)
        .await
        .map_err(|e| format!("failed to write config: {e:#}"))
}

#[tauri::command]
async fn write_plugin_config_json(internal_name: &str, json: &str) -> Result<(), String> {
    let config_path = plugin_config_path(internal_name)
        .ok_or_else(|| "could not determine plugin config path".to_string())?;
    if let Some(parent) = config_path.parent() {
        tokio::fs::create_dir_all(parent)
            .await
            .map_err(|e| format!("could not create parent: {e:#}"))?;
    }
    tokio::fs::write(&config_path, json)
        .await
        .map_err(|e| format!("could not write config: {e:#}"))
}

#[tauri::command]
async fn create_plugin(
    internal_name: &str,
    working_id: &str,
    state: State<'_, AppState>,
) -> Result<String, String> {
    let loader = state.delegate_loader.lock().await;

    set_cstr_stuff(&*loader);
    let make_plugin = loader
        .get_function_with_unmanaged_callers_only::<fn(*const u8, i32, *const u8, i32) -> *mut c_char>(
            pdcstr!("HeliosphereInstaller.Installer, heliosphere-installer"),
            pdcstr!("MakePlugin"),
        )
        .unwrap();

    let json_ptr = make_plugin(
        internal_name.as_ptr(),
        internal_name.len() as i32,
        working_id.as_ptr(),
        working_id.len() as i32,
    );

    let json = unsafe { CString::from_raw(json_ptr) };

    json.to_str()
        .map(ToOwned::to_owned)
        .map_err(|e| format!("invalid string: {e:#}"))
}

#[tauri::command]
async fn create_repo(url: &str, state: State<'_, AppState>) -> Result<String, String> {
    let loader = state.delegate_loader.lock().await;

    set_cstr_stuff(&*loader);
    let make_repo = loader
        .get_function_with_unmanaged_callers_only::<fn(*const u8, i32) -> *mut c_char>(
            pdcstr!("HeliosphereInstaller.Installer, heliosphere-installer"),
            pdcstr!("MakeRepo"),
        )
        .unwrap();

    let json_ptr = make_repo(url.as_ptr(), url.len() as i32);
    let json = unsafe { CString::from_raw(json_ptr) };

    json.to_str()
        .map(ToOwned::to_owned)
        .map_err(|e| format!("invalid string: {e:#}"))
}

fn set_cstr_stuff(delegate_loader: &AssemblyDelegateLoader) {
    SET_FUNC_ONCE.call_once(|| {
        let set_copy_to_c_string = delegate_loader
            .get_function_with_unmanaged_callers_only::<fn(f: unsafe extern "system" fn(*const u16, i32) -> *mut c_char)>(
                pdcstr!("HeliosphereInstaller.Installer, heliosphere-installer"),
                pdcstr!("SetCopyToCStringFunctionPtr"),
            )
            .unwrap();
        set_copy_to_c_string(copy_to_c_string);
    });
}

#[tauri::command]
async fn install_plugin_from_url(
    internal_name: &str,
    url: &str,
    repo_url: &str,
    state: State<'_, AppState>,
) -> Result<String, String> {
    // make request for the plugin zip
    let resp = state
        .http
        .get(url)
        .send()
        .await
        .map_err(|e| format!("could not request plugin: {e:#}"))?;

    // create a working plugin id
    let id = Uuid::new_v4().to_string();

    // download and extract the zip
    let bytes = resp
        .bytes()
        .await
        .map_err(|e| format!("could not download plugin: {e:#}"))?;
    let cursor = Cursor::new(&*bytes);
    let mut zip =
        ZipArchive::new(cursor).map_err(|e| format!("could not process zip archive: {e:#}"))?;
    let mut manifest_json = String::new();
    {
        let mut entry = zip
            .by_name(&format!("{internal_name}.json"))
            .map_err(|e| format!("could not get manifest: {e:#}"))?;
        entry
            .read_to_string(&mut manifest_json)
            .map_err(|e| format!("could not extract manifest: {e:#}"))?;
    }

    let loader = state.delegate_loader.lock().await;

    set_cstr_stuff(&*loader);
    let fill_out = loader
        .get_function_with_unmanaged_callers_only::<fn(
            *const u8,
            i32,
            *const u8,
            i32,
            *const u8,
            i32,
            *mut *mut c_char,
            *mut *mut c_char,
        ) -> *mut c_char>(
            pdcstr!("HeliosphereInstaller.Installer, heliosphere-installer"),
            pdcstr!("FillOutManifest"),
        )
        .unwrap();

    let (filled_out_json, version) = {
        let mut filled_out_json_ptr: *mut c_char = std::ptr::null_mut();
        let mut version_ptr: *mut c_char = std::ptr::null_mut();
        fill_out(
            manifest_json.as_ptr(),
            manifest_json.len() as i32,
            id.as_ptr(),
            id.len() as i32,
            repo_url.as_ptr(),
            repo_url.len() as i32,
            &mut filled_out_json_ptr as *mut _,
            &mut version_ptr as &mut _,
        );

        if filled_out_json_ptr.is_null() || version_ptr.is_null() {
            return Err("failed to fill out manifest".into());
        }

        let filled_out_json = unsafe { CString::from_raw(filled_out_json_ptr) };
        let version = unsafe { CString::from_raw(version_ptr) };

        let filled_out_json = filled_out_json
            .to_str()
            .map_err(|e| format!("json was invalid: {e:#}"))?
            .to_string();
        let version = version
            .to_str()
            .map_err(|e| format!("json was invalid: {e:#}"))?
            .to_string();

        (filled_out_json, version)
    };

    let dir = xl_path().ok_or_else(|| format!("could not determine xivlauncher config path"))?;
    let install_dir = dir
        .join("installedPlugins")
        .join(internal_name)
        .join(version);

    match tokio::fs::remove_dir_all(&install_dir).await {
        Err(e) if e.kind() != std::io::ErrorKind::NotFound => {
            return Err(format!("could not remove install dir: {e:#}"));
        }
        _ => {}
    }

    tokio::fs::create_dir_all(&install_dir)
        .await
        .map_err(|e| format!("could not create install dir: {e:#}"))?;
    zip.extract(&install_dir)
        .map_err(|e| format!("could not extract zip: {e:#}"))?;

    let manifest_path = install_dir.join(&format!("{internal_name}.json"));
    tokio::fs::write(manifest_path, filled_out_json)
        .await
        .map_err(|e| format!("could not write manifest: {e:#}"))?;

    Ok(id)
}

#[tauri::command]
async fn check_path_validity(
    path: &str,
    create: bool,
    state: State<'_, AppState>,
) -> Result<bool, ()> {
    let path = Path::new(path);

    if create {
        if tokio::fs::create_dir_all(path).await.is_err() {
            return Ok(false);
        }
    }

    let meta = match tokio::fs::metadata(path).await {
        Ok(m) => m,
        Err(_) => return Ok(false),
    };

    if meta.permissions().readonly() || !meta.is_dir() {
        return Ok(false);
    }

    let loader = state.delegate_loader.lock().await;

    set_cstr_stuff(&*loader);
    let check_valid = loader
        .get_function_with_unmanaged_callers_only::<fn(*const u8, i32) -> u8>(
            pdcstr!("HeliosphereInstaller.Installer, heliosphere-installer"),
            pdcstr!("IsPathValid"),
        )
        .unwrap();

    let path_str = match path.to_str() {
        Some(p) => p,
        None => return Ok(false),
    };

    Ok(check_valid(path_str.as_ptr(), path_str.len() as i32) != 0)
}

struct AppState {
    delegate_loader: Mutex<AssemblyDelegateLoader>,
    http: Client,
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    let host = netcorehost::nethost::load_hostfxr().unwrap();
    let ctx = host
        .initialize_for_runtime_config(pdcstr!(
            "../csharp/bin/Release/net8.0/heliosphere-installer.runtimeconfig.json"
        ))
        .unwrap();
    let delegate_loader = ctx
        .get_delegate_loader_for_assembly(pdcstr!(
            "../csharp/bin/Release/net8.0/heliosphere-installer.dll"
        ))
        .unwrap();

    tauri::Builder::default()
        .plugin(tauri_plugin_dialog::init())
        .plugin(tauri_plugin_shell::init())
        .invoke_handler(tauri::generate_handler![
            check_for_process,
            dalamud_config_present,
            get_dalamud_config_json,
            write_dalamud_config_json,
            get_plugin_config_json,
            write_plugin_config_json,
            create_plugin,
            create_repo,
            install_plugin_from_url,
            check_path_validity,
        ])
        .manage(AppState {
            delegate_loader: Mutex::new(delegate_loader),
            http: Client::new(),
        })
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}

const SET_FUNC_ONCE: Once = Once::new();

unsafe extern "system" fn copy_to_c_string(ptr: *const u16, length: i32) -> *mut c_char {
    let wide_chars = unsafe { std::slice::from_raw_parts(ptr, length as usize) };
    let string = String::from_utf16_lossy(wide_chars);
    let c_string = match CString::new(string) {
        Ok(c_string) => c_string,
        Err(_) => return std::ptr::null_mut(),
    };
    c_string.into_raw()
}
