//! The window's side: MCP over Streamable HTTP on `127.0.0.1`, on a thread of its own.
//!
//! As the MCP spec asks of a local HTTP server: it listens on loopback only, rmcp turns away any `Host` that isn't
//! loopback (DNS rebinding) and any request a web page sends (one with an `Origin`), and every request must carry
//! the bearer token from `mcp.json`.

use std::convert::Infallible;
use std::sync::Arc;

use bytes::Bytes;
use fig_api::FigApi;
use http_body_util::{BodyExt, Full, combinators::BoxBody};
use hyper::{Request, Response, StatusCode, body::Incoming};
use hyper_util::rt::{TokioExecutor, TokioIo};
use rmcp::transport::streamable_http_server::{
    StreamableHttpServerConfig, StreamableHttpService, session::local::LocalSessionManager,
};
use tower_service::Service;

use crate::endpoint;
use crate::tools::FigServer;

pub fn spawn(api: Arc<dyn FigApi>) {
    let started = std::thread::Builder::new().name("fig-mcp".into()).spawn(move || {
        let runtime = match tokio::runtime::Builder::new_multi_thread().worker_threads(2).enable_all().build() {
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
    let port = endpoint::port();
    let listener = match tokio::net::TcpListener::bind(("127.0.0.1", port)).await {
        Ok(l) => l,
        Err(e) => return eprintln!("fig mcp: can't listen on 127.0.0.1:{port} (another Fig may serve it): {e}"),
    };
    let token = match endpoint::publish(port) {
        Ok(e) => Arc::new(format!("Bearer {}", e.token)),
        Err(e) => return eprintln!("fig mcp: can't write {}: {e}", endpoint::info_path().display()),
    };
    let config = StreamableHttpServerConfig::default().enforce_origin_validation();
    let mcp = StreamableHttpService::new(
        move || Ok(FigServer::new(api.clone())),
        Arc::new(LocalSessionManager::default()),
        config,
    );
    loop {
        let stream = match listener.accept().await {
            Ok((stream, _)) => stream,
            Err(e) => {
                eprintln!("fig mcp: {e}");
                continue;
            }
        };
        let (mcp, token) = (mcp.clone(), token.clone());
        tokio::spawn(async move {
            let service = hyper::service::service_fn(move |req: Request<Incoming>| {
                let (mut mcp, token) = (mcp.clone(), token.clone());
                async move {
                    let authorised = req
                        .headers()
                        .get(hyper::header::AUTHORIZATION)
                        .is_some_and(|v| constant_time_eq(v.as_bytes(), token.as_bytes()));
                    if !authorised {
                        return Ok::<_, Infallible>(status(StatusCode::UNAUTHORIZED, "missing or wrong bearer token"));
                    }
                    match mcp.call(req).await {
                        Ok(resp) => Ok(resp.map(|b| b.map_err(|e| -> Box<dyn std::error::Error + Send + Sync> { e.into() }).boxed())),
                        Err(e) => Ok(status(StatusCode::INTERNAL_SERVER_ERROR, &e.to_string())),
                    }
                }
            });
            let conn = hyper_util::server::conn::auto::Builder::new(TokioExecutor::new());
            if let Err(e) = conn.serve_connection(TokioIo::new(stream), service).await {
                eprintln!("fig mcp: connection: {e}");
            }
        });
    }
}

type Body = BoxBody<Bytes, Box<dyn std::error::Error + Send + Sync>>;

fn status(code: StatusCode, why: &str) -> Response<Body> {
    let body = Full::new(Bytes::from(why.to_owned())).map_err(|never| match never {}).boxed();
    let mut resp = Response::new(body);
    *resp.status_mut() = code;
    resp
}

/// Compares the token without leaking how much of it matched through timing.
fn constant_time_eq(a: &[u8], b: &[u8]) -> bool {
    a.len() == b.len() && a.iter().zip(b).fold(0u8, |acc, (x, y)| acc | (x ^ y)) == 0
}
