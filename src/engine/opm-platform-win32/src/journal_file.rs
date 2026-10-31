use std::fs::{File, OpenOptions};
use std::os::windows::fs::{FileExt, OpenOptionsExt};
use std::path::Path;

use opm_core::journal::RECORD_SIZE;
use opm_core::{IoError, JournalStore};
use windows_sys::Win32::Storage::FileSystem::FILE_FLAG_WRITE_THROUGH;

/// The journal as an ordinary file on a volume that is not being moved (plan gap G5).
/// Opened write-through and flushed with `FlushFileBuffers` on every commit.
#[derive(Debug)]
pub struct FileJournal {
    file: File,
}

fn io(e: &std::io::Error) -> IoError {
    IoError::Os(e.raw_os_error().unwrap_or(-1))
}

impl FileJournal {
    pub fn open(path: &Path) -> Result<Self, IoError> {
        if let Some(dir) = path.parent() {
            std::fs::create_dir_all(dir).map_err(|e| io(&e))?;
        }
        let file = OpenOptions::new()
            .read(true)
            .write(true)
            .create(true)
            .truncate(false)
            .custom_flags(FILE_FLAG_WRITE_THROUGH)
            .open(path)
            .map_err(|e| io(&e))?;
        let size = 2 * RECORD_SIZE as u64;
        if file.metadata().map_err(|e| io(&e))?.len() < size {
            file.set_len(size).map_err(|e| io(&e))?;
            file.sync_all().map_err(|e| io(&e))?;
        }
        Ok(Self { file })
    }
}

impl JournalStore for FileJournal {
    fn read_slot(&mut self, slot: u8, buf: &mut [u8; RECORD_SIZE]) -> Result<(), IoError> {
        let offset = u64::from(slot & 1) * RECORD_SIZE as u64;
        let mut done = 0;
        while done < RECORD_SIZE {
            let n = self
                .file
                .seek_read(&mut buf[done..], offset + done as u64)
                .map_err(|e| io(&e))?;
            if n == 0 {
                return Err(IoError::OutOfRange);
            }
            done += n;
        }
        Ok(())
    }

    fn write_slot(&mut self, slot: u8, buf: &[u8; RECORD_SIZE]) -> Result<(), IoError> {
        let offset = u64::from(slot & 1) * RECORD_SIZE as u64;
        let mut done = 0;
        while done < RECORD_SIZE {
            let n = self
                .file
                .seek_write(&buf[done..], offset + done as u64)
                .map_err(|e| io(&e))?;
            done += n;
        }
        Ok(())
    }

    fn flush(&mut self) -> Result<(), IoError> {
        self.file.sync_all().map_err(|e| io(&e))
    }
}

#[cfg(test)]
mod tests {
    use opm_core::journal::{commit, load_newest};
    use opm_core::{Direction, MoveRecord, PartitionEntry, PartitionKey, Phase, ENTRY_SIZE};

    use super::*;

    #[test]
    fn records_survive_reopening() {
        let dir = std::env::temp_dir().join(format!("opm-journal-test-{}", std::process::id()));
        let path = dir.join("journal.bin");
        let record = MoveRecord {
            sequence: 3,
            operation_id: [1; 16],
            disk_id: [2; 16],
            disk_size: 1 << 30,
            sector_size: 512,
            partition: PartitionKey([3; 16]),
            entry: PartitionEntry([4; ENTRY_SIZE]),
            source_offset: 1 << 20,
            target_offset: 2 << 20,
            length: 1 << 20,
            chunk_size: 1 << 20,
            direction: Direction::Backward,
            phase: Phase::Copying,
            chunks_done: 0,
            verify: true,
            source_digest: Some([7; 32]),
            map_digest: None,
        };
        {
            let mut journal = FileJournal::open(&path).unwrap();
            assert_eq!(load_newest(&mut journal).unwrap(), None);
            commit(&mut journal, &record).unwrap();
        }
        let mut journal = FileJournal::open(&path).unwrap();
        assert_eq!(load_newest(&mut journal).unwrap(), Some(record));
        drop(journal);
        std::fs::remove_dir_all(dir).unwrap();
    }
}
