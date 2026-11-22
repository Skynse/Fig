//! The window's side: an MCP server per connection on the socket, on a thread of its own.

use std::os::unix::fs::{DirBuilderExt, PermissionsExt};
use std::sync::Arc;

use fig_api::FigApi;
use rmcp::ServiceExt;
use tokio::net::{UnixListener, UnixStream};

use crate::tools::FigServer;

pub fn spawn(api: Arc<dyn FigApi>) {
    let started = std::thread::Builder::new().name("fig-mcp".into()).spawn(move || {
        let runtime = match tokio::runtime::Builder::new_current_thread().enable_all().build() {
            Ok(r) => r,
            Err(e) => return eprintln!("fig mcp: no runtime: {e}"),
        };
        runtime.block_on(listen(api));
    });
    if let Err(e) = started {
        eprintln!("fig mcp: couldn't start: {e}");
    }
}

async fn listen(api: Arc<dyn FigApi>) {
    let path = crate::socket_path();
    if let Some(dir) = path.parent() {
        let _ = std::fs::DirBuilder::new().recursive(true).mode(0o700).create(dir);
    }
    if path.exists() {
        // another Fig answering there keeps it; one that died left the file behind
        if UnixStream::connect(&path).await.is_ok() {
            return eprintln!("fig mcp: another Fig already serves {}", path.display());
        }
        let _ = std::fs::remove_file(&path);
    }
    let listener = match UnixListener::bind(&path) {
        Ok(l) => l,
        Err(e) => return eprintln!("fig mcp: can't listen on {}: {e}", path.display()),
    };
    let _ = std::fs::set_permissions(&path, std::fs::Permissions::from_mode(0o600));
    loop {
        let stream = match listener.accept().await {
            Ok((stream, _)) => stream,
            Err(e) => {
                eprintln!("fig mcp: {e}");
                continue;
            }
        };
        let api = api.clone();
        tokio::spawn(async move {
            match FigServer::new(api).serve(stream).await {
                Ok(service) => {
                    let _ = service.waiting().await;
                }
                Err(e) => eprintln!("fig mcp: a client didn't connect: {e}"),
            }
        });
    }
}
