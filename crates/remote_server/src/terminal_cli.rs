use crate::HeadlessProject;
use anyhow::{Context as _, Result};
use clap::Parser;
use futures::{AsyncReadExt as _, AsyncWriteExt as _, FutureExt as _};
use gpui::{Context, Task};
use net::async_net::{UnixListener, UnixStream};
use std::{
    io::{Read, Write},
    path::{Path, PathBuf},
    time::Duration,
};
use tempfile::TempDir;
use util::ResultExt as _;

const MAX_MESSAGE_BYTES: usize = 64 * 1024;
const REQUEST_TIMEOUT: Duration = Duration::from_secs(30);

#[derive(Parser)]
#[command(
    name = "zed",
    about = "Open a project in this Zed window. With no path, open the current folder."
)]
struct Args {
    /// Add folders to the connected project instead of opening a separate project.
    #[arg(short, long)]
    add: bool,
    #[arg(value_name = "FOLDERS", value_hint = clap::ValueHint::DirPath)]
    paths: Vec<PathBuf>,
}

#[derive(serde::Serialize, serde::Deserialize)]
#[serde(deny_unknown_fields)]
struct Request {
    paths: Vec<String>,
    add: bool,
}

pub fn run() -> Result<()> {
    let args = Args::parse();
    let socket = std::env::var_os("ZED_REMOTE_CLI_SOCKET")
        .context("run this command in a connected Zed SSH terminal")?;
    let paths = resolve_folders(args.paths, &std::env::current_dir()?)?;
    let request = serde_json::to_vec(&Request {
        paths,
        add: args.add,
    })?;
    anyhow::ensure!(
        request.len() <= MAX_MESSAGE_BYTES,
        "folder paths are too long"
    );
    let mut stream = std::os::unix::net::UnixStream::connect(socket)
        .context("cannot reach Zed; reconnect the project and open a new terminal")?;
    stream.set_read_timeout(Some(REQUEST_TIMEOUT))?;
    stream.set_write_timeout(Some(REQUEST_TIMEOUT))?;
    stream.write_all(&(request.len() as u32).to_be_bytes())?;
    stream.write_all(&request)?;
    let mut length = [0; 4];
    stream.read_exact(&mut length)?;
    let length = u32::from_be_bytes(length) as usize;
    anyhow::ensure!(length <= MAX_MESSAGE_BYTES, "invalid response size");
    let mut response = vec![0; length];
    stream.read_exact(&mut response)?;
    let response: std::result::Result<(), String> = serde_json::from_slice(&response)?;
    response.map_err(anyhow::Error::msg)
}

fn resolve_folders(mut paths: Vec<PathBuf>, cwd: &Path) -> Result<Vec<String>> {
    if paths.is_empty() {
        paths.push(cwd.to_path_buf());
    }
    anyhow::ensure!(paths.len() <= 64, "expected at most 64 folders");
    paths
        .into_iter()
        .map(|path| {
            let path = cwd.join(path);
            let path = path
                .canonicalize()
                .with_context(|| format!("cannot open {}", path.display()))?;
            anyhow::ensure!(path.is_dir(), "{} is not a folder", path.display());
            path.into_os_string()
                .into_string()
                .map_err(|_| anyhow::anyhow!("folder path is not UTF-8"))
        })
        .collect()
}

pub(crate) struct TerminalCli {
    directory: TempDir,
    _task: Task<()>,
}

impl TerminalCli {
    pub(crate) fn new(cx: &mut Context<HeadlessProject>) -> Result<Self> {
        // A private, short-lived directory prevents other users from sending requests
        // and avoids leaving a launcher that targets a later, unrelated session.
        let directory = tempfile::Builder::new().prefix("zed-cli-").tempdir()?;
        let socket = directory.path().join("cli.sock");
        let listener = UnixListener::bind(&socket)?;
        std::os::unix::fs::symlink(std::env::current_exe()?, directory.path().join("zed"))?;
        let task = cx.spawn(async move |project, cx| {
            loop {
                let (mut stream, _) = match listener.accept().await {
                    Ok(connection) => connection,
                    Err(error) => {
                        log::error!("Zed terminal launcher: {error:#}");
                        break;
                    }
                };
                let request = async {
                    let result = async {
                        let request = read_request(&mut stream).await?;
                        let project = project.upgrade().context("remote project was closed")?;
                        HeadlessProject::open_terminal_folders(
                            project,
                            request.paths,
                            request.add,
                            cx.clone(),
                        )
                        .await
                    }
                    .await;
                    write_response(&mut stream, result).await
                }
                .fuse();
                let timeout = cx.background_executor().timer(REQUEST_TIMEOUT).fuse();
                futures::pin_mut!(request, timeout);
                let result = futures::select! {
                    result = request => result,
                    _ = timeout => Err(anyhow::anyhow!("opening folders timed out")),
                };
                result.log_err();
            }
        });
        Ok(Self {
            directory,
            _task: task,
        })
    }

    pub(crate) fn directory(&self) -> &Path {
        self.directory.path()
    }

    pub(crate) fn socket(&self) -> PathBuf {
        self.directory.path().join("cli.sock")
    }
}

async fn read_request(stream: &mut UnixStream) -> Result<Request> {
    let mut length = [0; 4];
    stream.read_exact(&mut length).await?;
    let length = u32::from_be_bytes(length) as usize;
    anyhow::ensure!(length <= MAX_MESSAGE_BYTES, "folder request is too large");
    let mut request = vec![0; length];
    stream.read_exact(&mut request).await?;
    let request: Request = serde_json::from_slice(&request)?;
    anyhow::ensure!(
        !request.paths.is_empty() && request.paths.len() <= 64,
        "expected 1 to 64 folders"
    );
    Ok(request)
}

async fn write_response(stream: &mut UnixStream, result: Result<()>) -> Result<()> {
    let response = serde_json::to_vec(&result.map_err(|error| format!("{error:#}")))?;
    anyhow::ensure!(response.len() <= MAX_MESSAGE_BYTES, "response is too large");
    stream
        .write_all(&(response.len() as u32).to_be_bytes())
        .await?;
    stream.write_all(&response).await?;
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn resolves_current_and_explicit_folders() -> Result<()> {
        let temporary = tempfile::tempdir()?;
        let folder = temporary.path().join("folder with spaces");
        std::fs::create_dir(&folder)?;
        let expected = vec![folder.canonicalize()?.to_string_lossy().into_owned()];
        assert_eq!(resolve_folders(vec![], &folder)?, expected);
        assert_eq!(resolve_folders(vec![".".into()], &folder)?, expected);
        assert_eq!(
            resolve_folders(vec!["folder with spaces".into()], temporary.path())?,
            expected
        );
        assert_eq!(
            resolve_folders(vec![folder.clone()], temporary.path())?,
            expected
        );
        std::fs::write(temporary.path().join("file"), "data")?;
        assert!(resolve_folders(vec!["file".into()], temporary.path()).is_err());
        assert!(resolve_folders(vec!["missing".into()], temporary.path()).is_err());
        assert!(Args::try_parse_from(["zed", "--new"]).is_err());
        assert!(Args::try_parse_from(["zed", "--add", "."]).is_ok());
        Ok(())
    }

    #[test]
    fn rejects_oversized_and_invalid_requests() -> Result<()> {
        smol::block_on(async {
            for body in [
                b"[]".to_vec(),
                b"not json".to_vec(),
                serde_json::to_vec(&Request {
                    paths: vec!["/tmp".into(); 65],
                    add: false,
                })?,
                serde_json::to_vec(&Request {
                    paths: vec![],
                    add: true,
                })?,
            ] {
                let (mut sender, mut receiver) = UnixStream::pair()?;
                sender.write_all(&(body.len() as u32).to_be_bytes()).await?;
                sender.write_all(&body).await?;
                assert!(read_request(&mut receiver).await.is_err());
            }
            let (mut sender, mut receiver) = UnixStream::pair()?;
            sender
                .write_all(&((MAX_MESSAGE_BYTES + 1) as u32).to_be_bytes())
                .await?;
            assert!(read_request(&mut receiver).await.is_err());
            Ok(())
        })
    }
}
