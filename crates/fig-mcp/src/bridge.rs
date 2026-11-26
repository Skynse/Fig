//! `fig mcp`: joins an MCP client's stdio to the window's socket, starting Fig when nothing answers there.
//! Nothing but the protocol may reach stdout.

use std::path::Path;
use std::time::{Duration, Instant};

use tokio::io::AsyncWriteExt;
use tokio::net::UnixStream;

pub fn run() -> i32 {
    let runtime = match tokio::runtime::Builder::new_current_thread().enable_all().build() {
        Ok(r) => r,
        Err(e) => {
            eprintln!("fig mcp: {e}");
            return 1;
        }
    };
    runtime.block_on(async {
        let path = crate::socket_path();
        let stream = match connect_or_launch(&path).await {
            Ok(s) => s,
            Err(e) => {
                eprintln!("fig mcp: {e}");
                return 1;
            }
        };
        let (mut from_fig, mut to_fig) = stream.into_split();
        // the client closing stdin closes our half; Fig then closes its, which ends the copy below
        tokio::spawn(async move {
            let _ = tokio::io::copy(&mut tokio::io::stdin(), &mut to_fig).await;
            let _ = to_fig.shutdown().await;
        });
        let mut stdout = tokio::io::stdout();
        let _ = tokio::io::copy(&mut from_fig, &mut stdout).await;
        let _ = stdout.flush().await;
        0
    })
}

async fn connect_or_launch(path: &Path) -> Result<UnixStream, String> {
    if let Ok(s) = UnixStream::connect(path).await {
        return Ok(s);
    }
    if std::env::var_os("FIG_MCP_NO_LAUNCH").is_some() {
        return Err(format!("Fig isn't running (nothing at {})", path.display()));
    }
    launch()?;
    let until = Instant::now() + Duration::from_secs(30);
    while Instant::now() < until {
        tokio::time::sleep(Duration::from_millis(250)).await;
        if let Ok(s) = UnixStream::connect(path).await {
            return Ok(s);
        }
    }
    Err(format!("started Fig, but nothing answered at {} (is FIG_MCP=0 set?)", path.display()))
}

/// Starts Fig's window in its own process group, so it outlives this bridge.
fn launch() -> Result<(), String> {
    use std::os::unix::process::CommandExt;
    use std::process::{Command, Stdio};
    let exe = std::env::current_exe().map_err(|e| format!("can't find Fig: {e}"))?;
    Command::new(exe)
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .process_group(0)
        .spawn()
        .map(|_| ())
        .map_err(|e| format!("couldn't start Fig: {e}"))
}
