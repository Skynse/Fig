//! Where the window serves MCP and how to get in: Streamable HTTP on `127.0.0.1`, with a bearer token. Both are
//! written to `mcp.json` in Fig's data folder (readable only by this user), which `fig mcp` and any HTTP client
//! configured by hand read.

use std::path::PathBuf;

use serde::{Deserialize, Serialize};

/// The port the window listens on unless `FIG_MCP_PORT` says otherwise. Fixed, so a client configured with the URL
/// keeps working across restarts.
pub const DEFAULT_PORT: u16 = 47331;

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct Endpoint {
    pub url: String,
    pub token: String,
}

/// `~/.fig` (`%USERPROFILE%\.fig` on Windows), or `FIG_DATA_DIR`: the same folder as the window's settings.
fn data_dir() -> PathBuf {
    if let Some(dir) = std::env::var_os("FIG_DATA_DIR") {
        return dir.into();
    }
    let home = std::env::var_os("HOME").or_else(|| std::env::var_os("USERPROFILE")).unwrap_or_else(|| ".".into());
    PathBuf::from(home).join(".fig")
}

pub fn info_path() -> PathBuf {
    data_dir().join("mcp.json")
}

pub fn port() -> u16 {
    std::env::var("FIG_MCP_PORT").ok().and_then(|p| p.parse().ok()).unwrap_or(DEFAULT_PORT)
}

/// What `mcp.json` says, if it is there.
pub fn read() -> Option<Endpoint> {
    serde_json::from_str(&std::fs::read_to_string(info_path()).ok()?).ok()
}

/// The endpoint for `port`, keeping the token already saved (so clients set up with it keep working), and saves it.
pub fn publish(port: u16) -> std::io::Result<Endpoint> {
    let token = read().map(|e| e.token).filter(|t| t.len() >= 32).unwrap_or_else(new_token);
    let endpoint = Endpoint { url: format!("http://127.0.0.1:{port}/mcp"), token };
    let path = info_path();
    if let Some(dir) = path.parent() {
        std::fs::create_dir_all(dir)?;
    }
    write_private(&path, &serde_json::to_vec_pretty(&endpoint).map_err(std::io::Error::other)?)?;
    Ok(endpoint)
}

fn new_token() -> String {
    format!("{}{}", uuid::Uuid::new_v4().simple(), uuid::Uuid::new_v4().simple())
}

#[cfg(unix)]
fn write_private(path: &std::path::Path, bytes: &[u8]) -> std::io::Result<()> {
    use std::io::Write;
    use std::os::unix::fs::{OpenOptionsExt, PermissionsExt};
    let mut f = std::fs::OpenOptions::new().write(true).create(true).truncate(true).mode(0o600).open(path)?;
    f.set_permissions(std::fs::Permissions::from_mode(0o600))?;
    f.write_all(bytes)
}

/// The user's profile folder is already private to them on Windows.
#[cfg(not(unix))]
fn write_private(path: &std::path::Path, bytes: &[u8]) -> std::io::Result<()> {
    std::fs::write(path, bytes)
}
