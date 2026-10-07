use anyhow::{Result, anyhow, ensure};
use collections::HashMap;
use futures::channel::oneshot;
use parking_lot::Mutex;
use rpc::proto;
use std::sync::{
    Arc,
    atomic::{AtomicU64, Ordering},
};

use crate::ProjectPath;

pub(super) static NEXT_FILE_ID: AtomicU64 = AtomicU64::new(1);
pub(super) const MAX_CONCURRENT_READS: usize = 4;
pub(super) type PendingReads = Arc<Mutex<HashMap<u64, PendingRead>>>;

pub(super) struct PendingRead {
    path: ProjectPath,
    limit: u64,
    size: Option<u64>,
    bytes: Vec<u8>,
    completion: oneshot::Sender<Result<Vec<u8>>>,
}

impl PendingRead {
    pub(super) fn new(
        path: ProjectPath,
        limit: u64,
        completion: oneshot::Sender<Result<Vec<u8>>>,
    ) -> Self {
        Self {
            path,
            limit,
            size: None,
            bytes: Vec::new(),
            completion,
        }
    }

    fn receive(&mut self, message: &proto::create_file_for_peer::Variant) -> Result<bool> {
        use proto::create_file_for_peer::Variant;
        match message {
            Variant::State(state) => {
                ensure!(self.size.is_none(), "Remote file sent duplicate metadata");
                let file = state
                    .file
                    .as_ref()
                    .ok_or_else(|| anyhow!("Remote file metadata is missing"))?;
                ensure!(
                    file.worktree_id == self.path.worktree_id.to_proto()
                        && file.path == self.path.path.as_unix_str(),
                    "Remote file metadata does not match the requested path"
                );
                ensure!(
                    state.content_size <= self.limit,
                    "Remote file exceeds the {} byte limit",
                    self.limit
                );
                self.size = Some(state.content_size);
                Ok(state.content_size == 0)
            }
            Variant::Chunk(chunk) => {
                let size = self
                    .size
                    .ok_or_else(|| anyhow!("Remote file data arrived before metadata"))?;
                ensure!(
                    !chunk.data.is_empty() && chunk.data.len() <= 1024 * 1024,
                    "Remote file sent an invalid chunk"
                );
                let length = self.bytes.len() as u64 + chunk.data.len() as u64;
                ensure!(
                    length <= size && length <= self.limit,
                    "Remote file sent more bytes than declared"
                );
                self.bytes.extend_from_slice(&chunk.data);
                Ok(length == size)
            }
        }
    }
}

/// Returning true means the message belonged to an in-memory read.
pub(super) fn receive(
    reads: &PendingReads,
    message: &proto::create_file_for_peer::Variant,
) -> bool {
    use proto::create_file_for_peer::Variant;
    let id = match message {
        Variant::State(state) => state.id,
        Variant::Chunk(chunk) => chunk.file_id,
    };
    let mut reads = reads.lock();
    let Some(read) = reads.get_mut(&id) else {
        return false;
    };
    let result = read.receive(message);
    if matches!(result, Ok(false)) {
        return true;
    }
    if let Some(read) = reads.remove(&id) {
        let bytes = result.map(|_| read.bytes);
        if read.completion.send(bytes).is_err() {
            log::trace!("Bounded file read recipient was released");
        }
    }
    true
}

/// Removes partial bytes even if the task is dropped before its first poll.
pub(super) struct ReadGuard {
    reads: PendingReads,
    id: u64,
}

impl ReadGuard {
    pub(super) fn new(reads: PendingReads, id: u64) -> Self {
        Self { reads, id }
    }
}

impl Drop for ReadGuard {
    fn drop(&mut self) {
        self.reads.lock().remove(&self.id);
    }
}

pub(super) fn next_id() -> u64 {
    NEXT_FILE_ID.fetch_add(1, Ordering::Relaxed)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::WorktreeId;
    use proto::create_file_for_peer::Variant;
    use util::rel_path::rel_path;

    fn setup(limit: u64) -> (PendingReads, oneshot::Receiver<Result<Vec<u8>>>) {
        let (tx, rx) = oneshot::channel();
        let path = ProjectPath {
            worktree_id: WorktreeId::from_proto(1),
            path: rel_path("safe.pdf").into(),
        };
        let reads = Arc::new(Mutex::new(HashMap::default()));
        reads.lock().insert(7, PendingRead::new(path, limit, tx));
        (reads, rx)
    }
    fn state(size: u64) -> Variant {
        Variant::State(proto::FileState {
            id: 7,
            content_size: size,
            file: Some(proto::File {
                worktree_id: 1,
                path: "safe.pdf".into(),
                ..Default::default()
            }),
        })
    }
    fn chunk(bytes: &[u8]) -> Variant {
        Variant::Chunk(proto::FileChunk {
            file_id: 7,
            data: bytes.to_vec(),
        })
    }

    #[test]
    fn bounded_read_completes_only_at_declared_length() {
        let (reads, mut rx) = setup(4);
        assert!(receive(&reads, &state(4)));
        assert!(receive(&reads, &chunk(&[0, 255])));
        assert!(rx.try_recv().unwrap().is_none());
        assert!(receive(&reads, &chunk(&[1, 2])));
        assert_eq!(rx.try_recv().unwrap().unwrap().unwrap(), vec![0, 255, 1, 2]);
        assert!(reads.lock().is_empty());
        assert!(!receive(&reads, &chunk(&[3])));
    }
    #[test]
    fn bounded_read_rejects_invalid_metadata_and_chunks() {
        for messages in [
            vec![state(5)],
            vec![chunk(&[1])],
            vec![state(2), chunk(&[1, 2, 3])],
            vec![state(2), state(2)],
            vec![state(2), chunk(&[])],
        ] {
            let (reads, mut rx) = setup(4);
            for message in messages {
                receive(&reads, &message);
            }
            assert!(rx.try_recv().unwrap().unwrap().is_err());
            assert!(reads.lock().is_empty());
        }
        let (reads, mut rx) = setup(4);
        let mut message = state(1);
        if let Variant::State(state) = &mut message {
            state.file.as_mut().unwrap().path = "other.pdf".into();
        }
        receive(&reads, &message);
        assert!(rx.try_recv().unwrap().unwrap().is_err());
    }
    #[test]
    fn bounded_read_empty_file_and_cancellation_release_state() {
        let (reads, mut rx) = setup(4);
        receive(&reads, &state(0));
        assert!(rx.try_recv().unwrap().unwrap().unwrap().is_empty());
        let (reads, mut rx) = setup(4);
        let guard = ReadGuard::new(reads.clone(), 7);
        receive(&reads, &state(4));
        receive(&reads, &chunk(&[1]));
        drop(guard);
        assert!(reads.lock().is_empty());
        assert!(rx.try_recv().is_err());
        assert!(!receive(&reads, &chunk(&[2])));
    }
}
