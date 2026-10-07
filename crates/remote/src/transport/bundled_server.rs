use crate::RemotePlatform;
use anyhow::{Context as _, Result, ensure};
use release_channel::ReleaseChannel;
use semver::Version;
use serde::Deserialize;
use sha2::{Digest as _, Sha256};
use std::{
    fs,
    io::Read as _,
    path::{Path, PathBuf},
};

const MAX_MANIFEST_BYTES: u64 = 64 * 1024;
const MAX_ARCHIVE_BYTES: u64 = 256 * 1024 * 1024;

#[derive(Deserialize)]
struct Manifest {
    format_version: u32,
    release_channel: String,
    package_version: Version,
    source_commit: String,
    protocol_version: u32,
    servers: Vec<Server>,
}

#[derive(Deserialize)]
struct Server {
    platform: String,
    archive: String,
    sha256: String,
}

pub(super) fn select_archive(
    executable_directory: &Path,
    platform: RemotePlatform,
    channel: ReleaseChannel,
    mut version: Version,
    commit: &str,
) -> Result<PathBuf> {
    let directory = executable_directory.join("remote-servers");
    let manifest_path = directory.join("manifest.json");
    let metadata = fs::metadata(&manifest_path).context(
        "The matching remote server package is missing. Extract or install the complete fork package",
    )?;
    ensure!(
        metadata.is_file() && metadata.len() <= MAX_MANIFEST_BYTES,
        "Invalid remote server manifest size"
    );
    let mut manifest_bytes = Vec::new();
    fs::File::open(&manifest_path)?
        .take(MAX_MANIFEST_BYTES + 1)
        .read_to_end(&mut manifest_bytes)?;
    ensure!(
        manifest_bytes.len() as u64 <= MAX_MANIFEST_BYTES,
        "Remote server manifest exceeds its size limit"
    );
    let manifest: Manifest =
        serde_json::from_slice(&manifest_bytes).context("Invalid remote server manifest")?;
    version.build = semver::BuildMetadata::EMPTY;
    ensure!(
        manifest.format_version == 1,
        "Unsupported remote server manifest format"
    );
    ensure!(
        manifest.release_channel == channel.dev_name(),
        "Remote server release channel does not match the client"
    );
    ensure!(
        manifest.package_version == version,
        "Remote server package version does not match the client"
    );
    ensure!(
        commit.len() == 40 && commit.bytes().all(|byte| byte.is_ascii_hexdigit()),
        "Client source commit is unavailable"
    );
    ensure!(
        manifest.source_commit == commit,
        "Remote server source commit does not match the client"
    );
    ensure!(
        manifest.protocol_version == rpc::PROTOCOL_VERSION,
        "Remote server protocol does not match the client"
    );
    ensure!(
        manifest.servers.len() <= 8,
        "Invalid remote server platform list"
    );
    let platform_name = format!("{}-{}", platform.os, platform.arch);
    let mut servers = manifest
        .servers
        .iter()
        .filter(|server| server.platform == platform_name);
    let server = servers.next().with_context(|| {
        format!("This fork package has no matching remote server for {platform_name}")
    })?;
    ensure!(
        servers.next().is_none(),
        "Duplicate remote server platform entries"
    );
    let expected_archive = format!("zed-remote-server-{platform_name}.gz");
    ensure!(
        server.archive == expected_archive,
        "Invalid remote server archive filename"
    );
    ensure!(
        server.sha256.len() == 64 && server.sha256.bytes().all(|byte| byte.is_ascii_hexdigit()),
        "Invalid remote server checksum"
    );
    let archive_path = directory
        .join(&expected_archive)
        .canonicalize()
        .context("Matching remote server archive is missing")?;
    let canonical_directory = directory.canonicalize()?;
    ensure!(
        archive_path.parent() == Some(canonical_directory.as_path()),
        "Remote server archive resolves outside the package"
    );
    let mut archive = fs::File::open(&archive_path)?;
    let metadata = archive.metadata()?;
    ensure!(
        metadata.is_file() && metadata.len() > 0 && metadata.len() <= MAX_ARCHIVE_BYTES,
        "Invalid remote server archive size"
    );
    let mut digest = Sha256::new();
    let mut buffer = [0u8; 64 * 1024];
    let mut bytes_read = 0u64;
    loop {
        let count = archive.read(&mut buffer)?;
        if count == 0 {
            break;
        }
        bytes_read += count as u64;
        ensure!(
            bytes_read <= MAX_ARCHIVE_BYTES,
            "Remote server archive exceeds its size limit"
        );
        digest.update(&buffer[..count]);
    }
    ensure!(
        format!("{:x}", digest.finalize()) == server.sha256.to_ascii_lowercase(),
        "Remote server archive checksum does not match the package"
    );
    // Windows OpenSSH treats a verbatim drive prefix as a remote SCP hostname.
    Ok(util::paths::SanitizedPath::new(&archive_path)
        .as_path()
        .to_path_buf())
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{RemoteArch, RemoteOs};
    use serde_json::json;

    const COMMIT: &str = "1234567890abcdef1234567890abcdef12345678";

    fn fixture() -> (tempfile::TempDir, serde_json::Value) {
        let temporary_directory = tempfile::tempdir().unwrap();
        let directory = temporary_directory.path().join("remote-servers");
        fs::create_dir(&directory).unwrap();
        let bytes = b"original local fixture; selection does not execute it";
        fs::write(directory.join("zed-remote-server-linux-x86_64.gz"), bytes).unwrap();
        let manifest = json!({
            "format_version": 1,
            "release_channel": "stable",
            "package_version": "1.24.0",
            "source_commit": COMMIT,
            "protocol_version": rpc::PROTOCOL_VERSION,
            "servers": [{
                "platform": "linux-x86_64",
                "archive": "zed-remote-server-linux-x86_64.gz",
                "sha256": format!("{:x}", Sha256::digest(bytes)),
            }],
        });
        (temporary_directory, manifest)
    }

    fn select(directory: &Path, manifest: &serde_json::Value, arch: RemoteArch) -> Result<PathBuf> {
        fs::write(
            directory.join("remote-servers/manifest.json"),
            serde_json::to_vec(manifest)?,
        )?;
        select_archive(
            directory,
            RemotePlatform {
                os: RemoteOs::Linux,
                arch,
            },
            ReleaseChannel::Stable,
            Version::parse("1.24.0+stable.local").unwrap(),
            COMMIT,
        )
    }

    #[test]
    fn selects_matching_source_version_protocol_and_platform() {
        let (directory, manifest) = fixture();
        assert!(
            select(directory.path(), &manifest, RemoteArch::X86_64)
                .unwrap()
                .is_file()
        );
        assert!(select(directory.path(), &manifest, RemoteArch::Aarch64).is_err());
    }

    #[cfg(windows)]
    #[test]
    fn returns_a_windows_upload_path_without_a_verbatim_drive_prefix() {
        let (directory, manifest) = fixture();
        let archive = select(directory.path(), &manifest, RemoteArch::X86_64).unwrap();
        assert!(matches!(
            archive.components().next(),
            Some(std::path::Component::Prefix(prefix))
                if matches!(prefix.kind(), std::path::Prefix::Disk(_))
        ));
        assert_eq!(
            fs::read(archive).unwrap(),
            b"original local fixture; selection does not execute it"
        );
    }

    #[test]
    fn rejects_mismatched_or_incomplete_package() {
        let (directory, manifest) = fixture();
        for (field, value) in [
            ("format_version", json!(2)),
            ("release_channel", json!("preview")),
            ("package_version", json!("1.22.0")),
            (
                "source_commit",
                json!("abcdef1234567890abcdef1234567890abcdef12"),
            ),
            ("protocol_version", json!(rpc::PROTOCOL_VERSION + 1)),
        ] {
            let mut mismatched = manifest.clone();
            mismatched[field] = value;
            assert!(select(directory.path(), &mismatched, RemoteArch::X86_64).is_err());
        }
        fs::remove_file(directory.path().join("remote-servers/manifest.json")).unwrap();
        assert!(
            select_archive(
                directory.path(),
                RemotePlatform {
                    os: RemoteOs::Linux,
                    arch: RemoteArch::X86_64
                },
                ReleaseChannel::Stable,
                Version::new(1, 24, 0),
                COMMIT
            )
            .is_err()
        );
    }

    #[test]
    fn rejects_corrupted_archive_and_invalid_platform_entries() {
        let (directory, mut manifest) = fixture();
        manifest["servers"][0]["archive"] = json!("../different.gz");
        assert!(select(directory.path(), &manifest, RemoteArch::X86_64).is_err());
        let (directory, mut manifest) = fixture();
        let duplicate = manifest["servers"][0].clone();
        manifest["servers"].as_array_mut().unwrap().push(duplicate);
        assert!(select(directory.path(), &manifest, RemoteArch::X86_64).is_err());
        let (directory, manifest) = fixture();
        fs::write(
            directory
                .path()
                .join("remote-servers/zed-remote-server-linux-x86_64.gz"),
            b"changed fixture",
        )
        .unwrap();
        assert!(select(directory.path(), &manifest, RemoteArch::X86_64).is_err());
    }
}
