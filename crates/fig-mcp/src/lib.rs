//! Fig's MCP extension. The window serves MCP on a Unix socket; `fig mcp` is the stdio side an MCP client starts,
//! which joins that socket (starting Fig when it isn't running). Everything it does goes through `fig_api`.
//!
//! `FIG_MCP=0` keeps the window from serving; `FIG_MCP_SOCKET` puts the socket somewhere else.

mod bridge;
mod server;
mod tools;

use std::path::PathBuf;
use std::sync::Arc;

use fig_api::{Extension, FigApi};

pub struct Mcp;

impl Extension for Mcp {
    fn name(&self) -> &'static str {
        "mcp"
    }

    fn run_cli(&self, args: &[String]) -> Option<i32> {
        (args.first().map(String::as_str) == Some("mcp")).then(bridge::run)
    }

    fn start(&self, api: Arc<dyn FigApi>) {
        if std::env::var_os("FIG_MCP").is_some_and(|v| v == "0") {
            return;
        }
        server::spawn(api);
    }
}

/// Where the window listens: `$XDG_RUNTIME_DIR/fig/mcp.sock`, a folder only this user can open.
fn socket_path() -> PathBuf {
    if let Some(path) = std::env::var_os("FIG_MCP_SOCKET") {
        return path.into();
    }
    let base = std::env::var_os("XDG_RUNTIME_DIR")
        .map(PathBuf::from)
        .unwrap_or_else(|| std::env::temp_dir().join(format!("fig-{}", std::env::var("USER").unwrap_or_default())));
    base.join("fig").join("mcp.sock")
}
