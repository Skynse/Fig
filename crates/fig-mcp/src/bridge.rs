//! `fig mcp`: for MCP clients that only start stdio servers. It pipes the client's stdio to the window's HTTP
//! endpoint (rmcp's Streamable HTTP client does the sessions), starting Fig when nothing answers there.
//! Nothing but the protocol may reach stdout.

use std::time::{Duration, Instant};

use rmcp::service::{RoleClient, RoleServer};
use rmcp::transport::streamable_http_client::StreamableHttpClientTransportConfig;
use rmcp::transport::{IntoTransport, StreamableHttpClientTransport, Transport};

use crate::endpoint::{self, Endpoint};

pub fn run() -> i32 {
    let runtime = match tokio::runtime::Builder::new_multi_thread().worker_threads(2).enable_all().build() {
        Ok(r) => r,
        Err(e) => {
            eprintln!("fig mcp: {e}");
            return 1;
        }
    };
    runtime.block_on(async {
        let endpoint = match reach_or_launch().await {
            Ok(e) => e,
            Err(e) => {
                eprintln!("fig mcp: {e}");
                return 1;
            }
        };
        pipe(endpoint).await;
        0
    })
}

/// Copies messages both ways until either side closes.
async fn pipe(endpoint: Endpoint) {
    let config = StreamableHttpClientTransportConfig::with_uri(endpoint.url).auth_header(endpoint.token);
    let mut fig: StreamableHttpClientTransport<reqwest::Client> = StreamableHttpClientTransport::from_config(config);
    let mut client = IntoTransport::<RoleServer, _, _>::into_transport((tokio::io::stdin(), tokio::io::stdout()));
    loop {
        tokio::select! {
            msg = Transport::<RoleServer>::receive(&mut client) => match msg {
                Some(msg) => if Transport::<RoleClient>::send(&mut fig, msg).await.is_err() { break },
                None => break,
            },
            msg = Transport::<RoleClient>::receive(&mut fig) => match msg {
                Some(msg) => if Transport::<RoleServer>::send(&mut client, msg).await.is_err() { break },
                None => break,
            },
        }
    }
    let _ = Transport::<RoleClient>::close(&mut fig).await;
}

/// The window's endpoint once something listens on its port, starting Fig first when nothing does.
async fn reach_or_launch() -> Result<Endpoint, String> {
    let port = endpoint::port();
    let listening = || async move { tokio::net::TcpStream::connect(("127.0.0.1", port)).await.is_ok() };
    if !listening().await {
        if std::env::var_os("FIG_MCP_NO_LAUNCH").is_some() {
            return Err(format!("Fig isn't running (nothing on 127.0.0.1:{port})"));
        }
        launch()?;
        let until = Instant::now() + Duration::from_secs(30);
        while !listening().await {
            if Instant::now() > until {
                return Err(format!("started Fig, but nothing answered on 127.0.0.1:{port} (is FIG_MCP=0 set?)"));
            }
            tokio::time::sleep(Duration::from_millis(250)).await;
        }
    }
    endpoint::read().ok_or_else(|| format!("Fig is listening but {} is missing", endpoint::info_path().display()))
}

/// Starts Fig's window on its own, so it outlives this bridge.
fn launch() -> Result<(), String> {
    use std::process::{Command, Stdio};
    let exe = std::env::current_exe().map_err(|e| format!("can't find Fig: {e}"))?;
    let mut cmd = Command::new(exe);
    cmd.stdin(Stdio::null()).stdout(Stdio::null()).stderr(Stdio::null());
    #[cfg(unix)]
    {
        use std::os::unix::process::CommandExt;
        cmd.process_group(0);
    }
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        const DETACHED_PROCESS: u32 = 0x0000_0008;
        const CREATE_NEW_PROCESS_GROUP: u32 = 0x0000_0200;
        cmd.creation_flags(DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP);
    }
    cmd.spawn().map(|_| ()).map_err(|e| format!("couldn't start Fig: {e}"))
}
