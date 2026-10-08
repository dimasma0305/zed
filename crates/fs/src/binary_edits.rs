use std::{
    io::{self, Read, Write},
    path::Path,
};

use anyhow::{Context as _, Result, ensure};

pub const MAX_BINARY_EDIT_BYTES: usize = 64 * 1024;

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct ByteEdit {
    pub offset: u64,
    pub original: Vec<u8>,
    pub replacement: Vec<u8>,
}

pub fn validate_byte_edits(file_size: u64, edits: &[ByteEdit]) -> Result<()> {
    ensure!(
        !edits.is_empty() && edits.len() <= MAX_BINARY_EDIT_BYTES,
        "Invalid number of byte edits"
    );
    let mut previous_end = 0;
    let mut total = 0usize;
    for edit in edits {
        ensure!(
            !edit.original.is_empty() && edit.original.len() == edit.replacement.len(),
            "Hex editing must preserve file length"
        );
        let end = edit
            .offset
            .checked_add(edit.original.len() as u64)
            .context("Byte edit offset overflows")?;
        ensure!(
            edit.offset >= previous_end && end <= file_size,
            "Byte edits overlap or exceed the file"
        );
        total = total
            .checked_add(edit.original.len())
            .context("Too many byte edits")?;
        ensure!(
            total <= MAX_BINARY_EDIT_BYTES,
            "Save after changing 64 KiB of bytes"
        );
        previous_end = end;
    }
    Ok(())
}

pub(crate) fn copy_with_byte_edits(
    reader: &mut impl Read,
    writer: &mut impl Write,
    file_size: u64,
    edits: &[ByteEdit],
) -> Result<()> {
    validate_byte_edits(file_size, edits)?;
    let mut position = 0;
    for edit in edits {
        let unchanged = edit.offset - position;
        ensure!(
            io::copy(&mut (&mut *reader).take(unchanged), writer)? == unchanged,
            "File changed while saving"
        );
        let mut original = vec![0; edit.original.len()];
        reader.read_exact(&mut original)?;
        // Accepting an already-applied edit makes a retry after an SSH disconnect safe.
        ensure!(
            original == edit.original || original == edit.replacement,
            "File bytes changed on disk at 0x{:X}. Reload before editing again",
            edit.offset
        );
        writer.write_all(&edit.replacement)?;
        position = edit.offset + edit.original.len() as u64;
    }
    let remaining = file_size - position;
    ensure!(
        io::copy(&mut (&mut *reader).take(remaining), writer)? == remaining,
        "File changed while saving"
    );
    ensure!(
        reader.read(&mut [0])? == 0,
        "File size changed while saving"
    );
    Ok(())
}

pub(crate) fn save_byte_edits(path: &Path, file_size: u64, edits: &[ByteEdit]) -> Result<()> {
    validate_byte_edits(file_size, edits)?;
    let canonical_path = std::fs::canonicalize(path)?;
    let mut source = std::fs::File::open(&canonical_path)?;
    let before = source.metadata()?;
    ensure!(
        before.is_file() && before.len() == file_size,
        "File size or type changed. Reload before editing again"
    );
    ensure!(!before.permissions().readonly(), "File is read-only");
    let directory = canonical_path
        .parent()
        .context("File has no parent folder")?;
    let mut temporary = tempfile::NamedTempFile::new_in(directory)?;
    copy_with_byte_edits(&mut source, temporary.as_file_mut(), file_size, edits)?;
    temporary.as_file().set_permissions(before.permissions())?;
    #[cfg(unix)]
    preserve_unix_metadata(&source, temporary.as_file(), &before)?;
    temporary.as_file().sync_all()?;
    let after = std::fs::metadata(&canonical_path)?;
    let unchanged = before.len() == after.len()
        && before.modified()? == after.modified()?
        && source.metadata()?.modified()? == before.modified()?
        && std::fs::canonicalize(path)? == canonical_path;
    #[cfg(unix)]
    let unchanged = {
        use std::os::unix::fs::MetadataExt as _;
        unchanged && before.ino() == after.ino() && before.dev() == after.dev()
    };
    ensure!(
        unchanged,
        "File changed while saving. Reload before editing again"
    );
    // Close handles before replacement on Windows. The original is untouched until this point.
    drop(source);
    let temporary = temporary.into_temp_path();
    #[cfg(windows)]
    super::atomic_replace(canonical_path.as_path(), temporary.as_ref())?;
    #[cfg(not(windows))]
    temporary
        .persist(&canonical_path)
        .map_err(|error| error.error)?;
    Ok(())
}

#[cfg(unix)]
fn preserve_unix_metadata(
    source: &std::fs::File,
    target: &std::fs::File,
    metadata: &std::fs::Metadata,
) -> Result<()> {
    use rustix::fs::{Gid, Uid, XattrFlags, fchown, fgetxattr, flistxattr, fsetxattr};
    use std::{ffi::CStr, os::unix::fs::MetadataExt as _};
    ensure!(
        metadata.mode() & 0o6000 == 0,
        "Byte editing files with setuid or setgid permissions is unsupported"
    );
    let target_metadata = target.metadata()?;
    if metadata.uid() != target_metadata.uid() || metadata.gid() != target_metadata.gid() {
        fchown(
            target,
            Some(Uid::from_raw(metadata.uid())),
            Some(Gid::from_raw(metadata.gid())),
        )
        .context("Cannot preserve file ownership")?;
    }
    let mut names = vec![0; 65_536];
    let length = match flistxattr(source, names.as_mut_slice()) {
        Ok(length) => length,
        Err(rustix::io::Errno::NOTSUP) => return Ok(()),
        Err(error) => return Err(error).context("Cannot preserve extended file attributes"),
    };
    for name in names[..length].split_inclusive(|byte| *byte == 0) {
        let name = CStr::from_bytes_with_nul(name)?;
        ensure!(
            name.to_bytes() != b"security.capability",
            "Byte editing files with executable capabilities is unsupported"
        );
        let mut value = vec![0; 65_536];
        let length = fgetxattr(source, name, value.as_mut_slice())
            .context("Cannot read extended file attributes")?;
        fsetxattr(target, name, &value[..length], XattrFlags::empty())
            .context("Cannot preserve extended file attributes")?;
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn edit(offset: u64, original: &[u8], replacement: &[u8]) -> ByteEdit {
        ByteEdit {
            offset,
            original: original.to_vec(),
            replacement: replacement.to_vec(),
        }
    }

    #[test]
    fn binary_save_is_atomic_preserves_unedited_bytes_and_allows_retry() {
        let directory = tempfile::tempdir().expect("temporary directory");
        let path = directory.path().join("bytes.bin");
        let original = [0, 255, 3, 4, 5, 6, 7, 128];
        std::fs::write(&path, original).expect("fixture");
        let edits = [edit(1, &[255], &[17]), edit(6, &[7, 128], &[0, 255])];
        save_byte_edits(&path, 8, &edits).expect("save");
        save_byte_edits(&path, 8, &edits).expect("idempotent retry");
        assert_eq!(
            std::fs::read(&path).expect("saved bytes"),
            [0, 17, 3, 4, 5, 6, 0, 255]
        );
        assert_eq!(
            std::fs::read_dir(directory.path())
                .expect("entries")
                .count(),
            1
        );
    }

    #[cfg(unix)]
    #[test]
    fn binary_save_preserves_unix_mode_and_extended_attributes() {
        use std::os::unix::fs::{MetadataExt as _, PermissionsExt as _};
        let directory = tempfile::tempdir().expect("temporary directory");
        let path = directory.path().join("bytes.bin");
        std::fs::write(&path, [1, 2, 3]).expect("fixture");
        std::fs::set_permissions(&path, std::fs::Permissions::from_mode(0o640)).expect("mode");
        rustix::fs::setxattr(
            &path,
            "user.zed-test",
            b"preserve",
            rustix::fs::XattrFlags::empty(),
        )
        .expect("attribute");
        let before = std::fs::metadata(&path).expect("metadata");
        save_byte_edits(&path, 3, &[edit(1, &[2], &[8])]).expect("save");
        let after = std::fs::metadata(&path).expect("metadata");
        assert_eq!(
            (before.uid(), before.gid(), before.mode()),
            (after.uid(), after.gid(), after.mode())
        );
        let mut value = [0; 8];
        assert_eq!(
            rustix::fs::getxattr(&path, "user.zed-test", &mut value).expect("attribute"),
            8
        );
        assert_eq!(&value, b"preserve");
    }

    #[test]
    fn conflict_and_invalid_edits_leave_original_unchanged() {
        let directory = tempfile::tempdir().expect("temporary directory");
        let path = directory.path().join("bytes.bin");
        let original = [1, 2, 3, 4];
        for edits in [
            vec![edit(0, &[1], &[9]), edit(2, &[99], &[8])],
            vec![edit(0, &[1, 2], &[9])],
            vec![edit(3, &[4, 5], &[9, 9])],
            vec![edit(1, &[2, 3], &[9, 9]), edit(2, &[3], &[8])],
            vec![edit(u64::MAX, &[1], &[9])],
        ] {
            std::fs::write(&path, original).expect("fixture");
            assert!(save_byte_edits(&path, 4, &edits).is_err());
            assert_eq!(std::fs::read(&path).expect("original preserved"), original);
            assert_eq!(
                std::fs::read_dir(directory.path())
                    .expect("entries")
                    .count(),
                1
            );
        }
    }

    #[test]
    fn byte_edit_limits_are_checked_before_reading() {
        let edits = [edit(
            0,
            &vec![0; MAX_BINARY_EDIT_BYTES + 1],
            &vec![1; MAX_BINARY_EDIT_BYTES + 1],
        )];
        assert!(validate_byte_edits(u64::MAX, &edits).is_err());
        assert!(validate_byte_edits(1, &[]).is_err());
    }
}
