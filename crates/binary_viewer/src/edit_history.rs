use std::collections::{BTreeMap, VecDeque};

use anyhow::{Result, ensure};
use fs::{ByteEdit, MAX_BINARY_EDIT_BYTES};

#[derive(Clone)]
struct Change {
    offset: u64,
    before: u8,
    after: u8,
}

#[derive(Default)]
pub(super) struct EditHistory {
    pending: BTreeMap<u64, (u8, u8)>,
    undo: VecDeque<Vec<Change>>,
    redo: Vec<Vec<Change>>,
    history_bytes: usize,
}

impl EditHistory {
    pub fn is_dirty(&self) -> bool {
        !self.pending.is_empty()
    }
    pub fn changed_bytes(&self) -> usize {
        self.pending.len()
    }
    pub fn can_undo(&self) -> bool {
        !self.undo.is_empty()
    }
    pub fn can_redo(&self) -> bool {
        !self.redo.is_empty()
    }

    pub fn overlay(&self, offset: u64, bytes: &mut [u8]) {
        for (index, byte) in bytes.iter_mut().enumerate() {
            if let Some((_, replacement)) = self.pending.get(&(offset + index as u64)) {
                *byte = *replacement;
            }
        }
    }

    pub fn replace(&mut self, offset: u64, before: &[u8], after: &[u8]) -> Result<()> {
        ensure!(
            before.len() == after.len() && before.len() <= MAX_BINARY_EDIT_BYTES,
            "Paste must overwrite at most 64 KiB of existing bytes"
        );
        ensure!(
            offset.checked_add(before.len() as u64).is_some(),
            "Byte offset overflows"
        );
        let changes = before
            .iter()
            .zip(after)
            .enumerate()
            .filter_map(|(index, (&before, &after))| {
                (before != after).then_some(Change {
                    offset: offset + index as u64,
                    before,
                    after,
                })
            })
            .collect::<Vec<_>>();
        if changes.is_empty() {
            return Ok(());
        }
        self.apply(&changes, false)?;
        self.redo.clear();
        self.history_bytes = self.undo.iter().map(Vec::len).sum::<usize>() + changes.len();
        self.undo.push_back(changes);
        while self.history_bytes > MAX_BINARY_EDIT_BYTES * 8 {
            if let Some(oldest) = self.undo.pop_front() {
                self.history_bytes -= oldest.len();
            }
        }
        Ok(())
    }

    fn apply(&mut self, changes: &[Change], reverse: bool) -> Result<()> {
        let mut pending = self.pending.clone();
        for change in changes {
            let (before, after) = if reverse {
                (change.after, change.before)
            } else {
                (change.before, change.after)
            };
            let original = match pending.get(&change.offset) {
                Some(&(original, current)) => {
                    ensure!(current == before, "Byte edits changed unexpectedly");
                    original
                }
                None => before,
            };
            if original == after {
                pending.remove(&change.offset);
            } else {
                pending.insert(change.offset, (original, after));
            }
        }
        ensure!(
            pending.len() <= MAX_BINARY_EDIT_BYTES,
            "Save after changing 64 KiB of bytes"
        );
        self.pending = pending;
        Ok(())
    }

    pub fn undo(&mut self) -> Result<()> {
        if let Some(changes) = self.undo.back().cloned() {
            self.apply(&changes, true)?;
            self.undo.pop_back();
            self.redo.push(changes);
        }
        Ok(())
    }

    pub fn redo(&mut self) -> Result<()> {
        if let Some(changes) = self.redo.last().cloned() {
            self.apply(&changes, false)?;
            self.redo.pop();
            self.undo.push_back(changes);
        }
        Ok(())
    }

    pub fn saved(&mut self) {
        self.pending.clear();
    }

    pub fn edits(&self) -> Vec<ByteEdit> {
        let mut edits: Vec<ByteEdit> = Vec::new();
        for (&offset, &(original, replacement)) in &self.pending {
            if let Some(last) = edits.last_mut()
                && last.offset + last.original.len() as u64 == offset
            {
                last.original.push(original);
                last.replacement.push(replacement);
            } else {
                edits.push(ByteEdit {
                    offset,
                    original: vec![original],
                    replacement: vec![replacement],
                });
            }
        }
        edits
    }
}

pub(super) fn parse_hex(text: &str) -> Result<Vec<u8>> {
    ensure!(
        text.len() <= MAX_BINARY_EDIT_BYTES * 4,
        "Paste at most 64 KiB of hexadecimal bytes"
    );
    let digits = text
        .chars()
        .filter(|character| !character.is_ascii_whitespace())
        .collect::<Vec<_>>();
    ensure!(
        !digits.is_empty() && digits.len() % 2 == 0 && digits.len() <= MAX_BINARY_EDIT_BYTES * 2,
        "Paste complete hexadecimal byte pairs, for example 00 FF 2A"
    );
    digits
        .chunks_exact(2)
        .map(|pair| {
            let high = pair[0]
                .to_digit(16)
                .filter(|_| pair[0].is_ascii())
                .ok_or_else(|| anyhow::anyhow!("Clipboard contains non-hexadecimal characters"))?;
            let low = pair[1]
                .to_digit(16)
                .filter(|_| pair[1].is_ascii())
                .ok_or_else(|| anyhow::anyhow!("Clipboard contains non-hexadecimal characters"))?;
            Ok(((high << 4) | low) as u8)
        })
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn undo_redo_and_save_keep_original_bytes_for_conflict_detection() {
        let mut history = EditHistory::default();
        history.replace(0x10000, &[0, 255], &[1, 2]).expect("edit");
        history.replace(0x10001, &[2], &[3]).expect("second edit");
        history.undo().expect("undo");
        assert_eq!(history.edits()[0].replacement, [1, 2]);
        history.redo().expect("redo");
        assert_eq!(history.edits()[0].original, [0, 255]);
        history.saved();
        assert!(!history.is_dirty());
        history.undo().expect("undo saved edit");
        assert_eq!(
            history.edits()[0],
            ByteEdit {
                offset: 0x10001,
                original: vec![3],
                replacement: vec![2]
            }
        );
        history.redo().expect("return to saved state");
        assert!(!history.is_dirty());
    }

    #[test]
    fn invalid_pastes_do_not_become_bytes() {
        assert_eq!(parse_hex("00 ff\n2A").expect("hex"), [0, 255, 42]);
        for text in ["", "0", "0x12", "GG", "12 , 34", "１２"] {
            assert!(parse_hex(text).is_err());
        }
    }

    #[test]
    fn returning_to_original_clears_dirty_state_and_overlay_is_page_local() {
        let mut history = EditHistory::default();
        history.replace(65_536, &[1, 2], &[8, 9]).expect("edit");
        let mut bytes = [0, 1, 2, 3];
        history.overlay(65_535, &mut bytes);
        assert_eq!(bytes, [0, 8, 9, 3]);
        history.replace(65_536, &[8, 9], &[1, 2]).expect("restore");
        assert!(!history.is_dirty());
    }
}
