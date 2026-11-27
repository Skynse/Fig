//! Fig's MCP extension. The window serves MCP over Streamable HTTP on `127.0.0.1` (port 47331, or `FIG_MCP_PORT`),
//! and writes its URL and bearer token to `mcp.json` in Fig's data folder. `fig mcp` is the stdio side for clients
//! that only start stdio servers: it pipes to that endpoint, starting Fig when it isn't running. Everything it does
//! goes through `fig_api`.
//!
//! `FIG_MCP=0` keeps the window from serving.

mod bridge;
mod server;
mod tools;
mod endpoint;

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
