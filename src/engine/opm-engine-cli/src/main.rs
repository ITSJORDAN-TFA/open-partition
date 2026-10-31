//! `opm-engine`: drives the journaled mover against a disk.
//!
//! Writes only to file-backed virtual disks (mounted VHD/VHDX), or to real disks on an Azure VM
//! when `--snapshot-confirmed NAME` says a snapshot of the disk was taken; see the write guard in
//! `opm-platform-win32`. Output is `key=value` lines so the GUI and test harness can parse it.

use std::collections::HashMap;
use std::path::PathBuf;
use std::process::ExitCode;
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use opm_core::journal::load_newest;
use opm_core::mover::{default_chunk_size, validate};
use opm_core::{
    recover_with_progress, roll_back, start_move_with_progress, BlockDevice, CopyMap, MoveError,
    MoveJob, MoveRecord, PartitionTable, Phase, Progress, Recovery, Stage,
};
use opm_platform_win32::{
    copy_map, is_extended_type, lock_volumes_in, snapshot_records, FileJournal, PhysicalDisk,
    PlatformError, WriteAccess,
};

const USAGE: &str = "\
usage:
  opm-engine info    --disk N
  opm-engine move    --disk N --from BYTES --to BYTES --journal PATH [--chunk BYTES] [--no-verify] [--full-copy] [--delay-ms MS]
  opm-engine recover --disk N --journal PATH [--delay-ms MS]
  opm-engine rollback --disk N --journal PATH
  opm-engine compare --disk N --journal PATH
  opm-engine status  --journal PATH
  opm-engine grow-extended --disk N --offset BYTES --length BYTES
  opm-engine hash    --disk N --offset BYTES --length BYTES

Every command that opens a disk also takes --snapshot-confirmed NAME. Without it only VHD/VHDX
test disks can be opened; with it, real disks too, but only on an Azure VM.";

fn main() -> ExitCode {
    let args: Vec<String> = std::env::args().skip(1).collect();
    let Some(command) = args.first().cloned() else {
        eprintln!("{USAGE}");
        return ExitCode::from(2);
    };
    let options = match parse_options(&args[1..]) {
        Ok(o) => o,
        Err(e) => {
            eprintln!("error: {e}\n{USAGE}");
            return ExitCode::from(2);
        }
    };
    let result = match command.as_str() {
        "info" => info(&options),
        "move" => move_partition(&options),
        "recover" => recover(&options),
        "rollback" => rollback(&options),
        "compare" => compare(&options),
        "hash" => hash(&options),
        "status" => status(&options),
        "grow-extended" => grow_extended(&options),
        _ => Err(Failure::Usage(format!("unknown command '{command}'"))),
    };
    match result {
        Ok(()) => ExitCode::SUCCESS,
        Err(Failure::Usage(e)) => {
            eprintln!("error: {e}\n{USAGE}");
            ExitCode::from(2)
        }
        Err(Failure::Platform(e)) => {
            println!("result=failed");
            println!("error={e}");
            eprintln!("error: {e}");
            ExitCode::FAILURE
        }
    }
}

enum Failure {
    Usage(String),
    Platform(PlatformError),
}

impl<E: Into<PlatformError>> From<E> for Failure {
    fn from(e: E) -> Self {
        Failure::Platform(e.into())
    }
}

struct Options(HashMap<String, Option<String>>);

fn parse_options(args: &[String]) -> Result<Options, String> {
    let mut map = HashMap::new();
    let mut i = 0;
    while i < args.len() {
        let name = args[i]
            .strip_prefix("--")
            .ok_or_else(|| format!("unexpected argument '{}'", args[i]))?;
        if name == "no-verify" || name == "full-copy" {
            map.insert(name.to_owned(), None);
            i += 1;
        } else {
            let value = args
                .get(i + 1)
                .ok_or_else(|| format!("--{name} needs a value"))?;
            map.insert(name.to_owned(), Some(value.clone()));
            i += 2;
        }
    }
    Ok(Options(map))
}

impl Options {
    fn number(&self, name: &str) -> Result<u64, Failure> {
        self.optional_number(name)?
            .ok_or_else(|| Failure::Usage(format!("--{name} is required")))
    }

    fn optional_number(&self, name: &str) -> Result<Option<u64>, Failure> {
        match self.0.get(name) {
            Some(Some(v)) => v
                .parse()
                .map(Some)
                .map_err(|_| Failure::Usage(format!("--{name} must be a number"))),
            _ => Ok(None),
        }
    }

    fn path(&self, name: &str) -> Result<PathBuf, Failure> {
        match self.0.get(name) {
            Some(Some(v)) => Ok(PathBuf::from(v)),
            _ => Err(Failure::Usage(format!("--{name} is required"))),
        }
    }

    fn flag(&self, name: &str) -> bool {
        self.0.contains_key(name)
    }

    fn access(&self) -> WriteAccess {
        match self.0.get("snapshot-confirmed") {
            Some(Some(name)) => WriteAccess::AzureWithSnapshot(name.clone()),
            _ => WriteAccess::VirtualOnly,
        }
    }

    fn open_disk(&self) -> Result<PhysicalDisk, Failure> {
        let access = self.access();
        let disk = PhysicalDisk::open_with(self.disk()?, &access)?;
        if let WriteAccess::AzureWithSnapshot(name) = &access {
            println!("snapshot_confirmed={name}");
        }
        Ok(disk)
    }

    fn disk(&self) -> Result<u32, Failure> {
        u32::try_from(self.number("disk")?)
            .map_err(|_| Failure::Usage("--disk is out of range".into()))
    }
}

fn info(o: &Options) -> Result<(), Failure> {
    let disk = o.open_disk()?;
    let layout = disk.layout()?;
    println!("disk={}", disk.number());
    println!("sector_size={}", disk.sector_size());
    println!("size={}", disk.size_bytes());
    println!("style={:?}", layout.style);
    let (start, end) = layout.usable_range(disk.size_bytes(), disk.sector_size());
    println!("usable={start}..{end}");
    for e in layout.entries() {
        println!(
            "partition number={} offset={} length={} movable={}",
            e.number,
            e.offset,
            e.length,
            layout.key_of(&e).is_some()
        );
    }
    Ok(())
}

fn hex(digest: &[u8]) -> String {
    digest.iter().map(|b| format!("{b:02X}")).collect()
}

fn hash(o: &Options) -> Result<(), Failure> {
    let mut disk = o.open_disk()?;
    println!(
        "sha256={}",
        hex(&disk.range_digest(o.number("offset")?, o.number("length")?)?)
    );
    Ok(())
}

fn operation_id() -> [u8; 16] {
    let nanos = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_or(0, |d| d.as_nanos());
    let mut id = [0u8; 16];
    id[..12].copy_from_slice(&nanos.to_le_bytes()[..12]);
    id[12..].copy_from_slice(&std::process::id().to_le_bytes());
    id
}

fn reporter(delay: Duration) -> impl FnMut(Progress) {
    move |p: Progress| {
        let stage = match p.stage {
            Stage::Hashing => "hashing",
            Stage::Copying => "copying",
            Stage::Verifying => "verifying",
        };
        println!(
            "progress stage={stage} chunks={}/{} bytes={}/{}",
            p.chunks_done, p.chunks_total, p.bytes_done, p.bytes_total
        );
        if !delay.is_zero() {
            std::thread::sleep(delay);
        }
    }
}

fn move_partition(o: &Options) -> Result<(), Failure> {
    let number = o.disk()?;
    let from = o.number("from")?;
    let to = o.number("to")?;
    let delay = Duration::from_millis(o.optional_number("delay-ms")?.unwrap_or(0));

    let mut disk = o.open_disk()?;
    let layout = disk.layout()?;
    let entry = layout
        .entries()
        .into_iter()
        .find(|e| e.offset == from)
        .ok_or_else(|| PlatformError::Refused(format!("no partition starts at byte {from}")))?;
    let key = layout.key_of(&entry).ok_or_else(|| {
        PlatformError::Refused(
            "a logical drive cannot be moved on its own; move its extended partition".into(),
        )
    })?;
    if layout.is_container(&entry) {
        let logicals = layout
            .entries()
            .iter()
            .filter(|e| {
                e.index >= 4
                    && e.mbr_type
                        .is_some_and(|t| !opm_platform_win32::is_extended_type(t))
            })
            .count();
        println!("extended_partition logical_drives={logicals}");
    }
    disk.check_move_target(key, to, entry.length)?;

    let chunk = o
        .optional_number("chunk")?
        .unwrap_or_else(|| default_chunk_size(from, to, disk.sector_size()));
    let job = MoveJob {
        operation_id: operation_id(),
        partition: key,
        source_offset: from,
        target_offset: to,
        length: entry.length,
        chunk_size: chunk,
        verify: !o.flag("no-verify"),
    };
    validate(&job, &disk, &[]).map_err(PlatformError::Move)?;

    let journal_path = o.path("journal")?;
    let mut journal = FileJournal::open(&journal_path)?;
    if load_newest(&mut journal)
        .map_err(|_| PlatformError::Refused("journal unreadable".into()))?
        .is_some_and(|r| r.phase != Phase::Done)
    {
        return Err(PlatformError::Refused(
            "an unfinished move is in the journal; run recover first".into(),
        )
        .into());
    }

    // Exclusive access before anything is read for hashing or written.
    let locks = lock_volumes_in(number, from, from + entry.length)?;
    for l in &locks {
        match l.free_unknown() {
            None => println!(
                "locked={} free_bytes={} free_source={}",
                l.path(),
                l.free_runs().iter().map(|r| r.1).sum::<u64>(),
                l.free_source().replace(' ', "_")
            ),
            Some(why) => println!(
                "locked={} free_bytes=unknown reason={}",
                l.path(),
                why.replace(' ', "_")
            ),
        }
    }

    // Only the parts that hold data are moved: free NTFS clusters (read under the lock) are
    // left out. The map is saved before the journal refers to it.
    let map = if o.flag("full-copy") {
        CopyMap::full(entry.length)
    } else {
        let free: Vec<(u64, u64)> = locks
            .iter()
            .flat_map(|l| l.free_runs().iter().copied())
            .collect();
        let map = copy_map::build(from, entry.length, u64::from(disk.sector_size()), &free);
        copy_map::save(&copy_map::path_for(&journal_path), &map)?;
        map
    };

    let identity = disk.identity()?;
    println!(
        "moving offset={from} to={to} length={} chunk={chunk} copy_bytes={}",
        entry.length,
        map.bytes()
    );
    let result = start_move_with_progress(
        &mut disk,
        &mut journal,
        identity,
        &job,
        &[],
        &map,
        &mut reporter(delay),
    );
    drop(locks);
    let record = match result {
        Ok(record) => record,
        // The copy can't be trusted, and the source was never written: put everything back.
        Err(e @ (MoveError::SourceChanged { .. } | MoveError::VerifyFailed { .. }))
            if from.abs_diff(to) >= entry.length =>
        {
            return Err(match roll_back(&mut disk, &mut journal, identity) {
                Ok(_) => {
                    println!("rolled_back=yes");
                    PlatformError::Undone(format!(
                        "{e}. The move was undone: the partition is back where it was and nothing was lost"
                    ))
                }
                Err(r) => PlatformError::Undone(format!(
                    "{e}. Undoing the move also failed ({r}); it is journaled, run rollback"
                )),
            }
            .into());
        }
        Err(e) => return Err(PlatformError::Move(e).into()),
    };
    if let Some(digest) = record.source_digest {
        println!("verified_sha256={}", hex(&digest));
    }
    println!("result=done phase={:?}", record.phase);
    Ok(())
}

/// Grow an MBR extended partition into free space after it, so its last logical drive can then
/// be extended with Windows' own resize. A single table write; no data moves, so no journal.
fn grow_extended(o: &Options) -> Result<(), Failure> {
    let mut disk = o.open_disk()?;
    let offset = o.number("offset")?;
    let length = o.number("length")?;
    disk.grow_extended(offset, length)?;
    println!("result=done offset={offset} length={length}");
    Ok(())
}

/// What the journal says, without opening any disk: lets the GUI find and offer to resume an
/// interrupted move after a restart.
fn status(o: &Options) -> Result<(), Failure> {
    let mut journal = FileJournal::open(&o.path("journal")?)?;
    let record = load_newest(&mut journal)
        .map_err(|_| PlatformError::Refused("journal unreadable".into()))?;
    let Some(r) = record else {
        println!("status=empty");
        return Ok(());
    };
    println!(
        "status={}",
        if r.phase == Phase::Done {
            "done"
        } else {
            "unfinished"
        }
    );
    println!("phase={:?}", r.phase);
    println!("disk_id={}", hex(&r.disk_id));
    println!("disk_size={}", r.disk_size);
    println!("source={}", r.source_offset);
    println!("target={}", r.target_offset);
    println!("length={}", r.length);
    println!(
        "chunks={}/{}",
        r.chunks_done,
        r.length.div_ceil(r.chunk_size)
    );
    if let Some(digest) = r.source_digest {
        println!("source_sha256={}", hex(&digest));
    }
    Ok(())
}

/// The copy map the move in `journal` was started with: the saved map file, or the whole
/// partition if it had none.
fn map_of(journal: &std::path::Path, record: &MoveRecord) -> Result<CopyMap, Failure> {
    if record.map_digest.is_none() {
        return Ok(CopyMap::full(record.length));
    }
    let path = copy_map::path_for(journal);
    copy_map::load(&path).map_err(|e| {
        PlatformError::Refused(format!(
            "the copy map {} could not be read ({e}); it must sit next to the journal",
            path.display()
        ))
        .into()
    })
}

/// Undo an unfinished move whose source was never overwritten: put the partition back where it
/// was. For when finishing is impossible, such as a copy that fails verification.
fn rollback(o: &Options) -> Result<(), Failure> {
    let mut disk = o.open_disk()?;
    let mut journal = FileJournal::open(&o.path("journal")?)?;
    let identity = disk.identity()?;
    match roll_back(&mut disk, &mut journal, identity).map_err(PlatformError::Move)? {
        Recovery::NothingToDo => println!("result=nothing-to-do"),
        Recovery::Completed(r) => println!("result=rolled-back offset={}", r.source_offset),
    }
    Ok(())
}

/// Read-only: compare the source and target of the journaled move and list where they differ,
/// with the partition each difference falls in (for a moved MBR extended partition, its
/// logical drives). Explains a copy that failed verification.
fn compare(o: &Options) -> Result<(), Failure> {
    const BLOCK: usize = 4 << 20;
    const SECTOR_RUN_LIMIT: usize = 40;
    let mut disk = o.open_disk()?;
    let mut journal = FileJournal::open(&o.path("journal")?)?;
    let r = load_newest(&mut journal)
        .map_err(|_| PlatformError::Refused("journal unreadable".into()))?
        .ok_or_else(|| PlatformError::Refused("journal is empty".into()))?;
    let starts: Vec<u64> = snapshot_records(&r.entry)
        .map(|records| {
            let first = records.first().map_or(0, |f| f.offset);
            records
                .iter()
                .filter(|x| x.mbr_type.is_none_or(|t| !is_extended_type(t)))
                .map(|x| x.offset - first)
                .collect()
        })
        .unwrap_or_default();
    println!(
        "comparing source={} target={} length={}",
        r.source_offset, r.target_offset, r.length
    );
    let sector = disk.sector_size() as usize;
    let mut a = vec![0u8; BLOCK];
    let mut b = vec![0u8; BLOCK];
    let (mut runs, mut differing, mut open): (usize, u64, Option<(u64, u64)>) = (0, 0, None);
    let report = |run: (u64, u64), runs: &mut usize| {
        *runs += 1;
        if *runs <= SECTOR_RUN_LIMIT {
            let owner = starts.iter().rev().find(|s| **s <= run.0);
            println!(
                "diff at={} source_byte={} length={} partition_start={} offset_in_partition={}",
                run.0,
                r.source_offset + run.0,
                run.1 - run.0,
                owner.map_or("none".into(), |s| (r.source_offset + s).to_string()),
                owner.map_or("-".into(), |s| (run.0 - s).to_string())
            );
        }
    };
    let map = map_of(&o.path("journal")?, &r)?;
    println!("mapped_bytes={}", map.bytes());
    let mut blocks = map
        .ranges
        .iter()
        .flat_map(|&(start, len)| {
            (0..len.div_ceil(BLOCK as u64)).map(move |i| {
                let at = start + i * BLOCK as u64;
                (at, (start + len - at).min(BLOCK as u64))
            })
        })
        .peekable();
    let mut compared = 0u64;
    while let Some((done, n)) = blocks.next() {
        let n = n as usize;
        disk.read_at(r.source_offset + done, &mut a[..n])?;
        disk.read_at(r.target_offset + done, &mut b[..n])?;
        if open.is_some_and(|(_, end)| end != done) {
            if let Some(run) = open.take() {
                report(run, &mut runs);
            }
        }
        if a[..n] != b[..n] {
            for (i, (x, y)) in a[..n].chunks(sector).zip(b[..n].chunks(sector)).enumerate() {
                let at = done + (i * sector) as u64;
                if x == y {
                    if let Some(run) = open.take() {
                        report(run, &mut runs);
                    }
                    continue;
                }
                differing += sector as u64;
                open = match open {
                    Some((start, end)) if end == at => Some((start, at + sector as u64)),
                    Some(run) => {
                        report(run, &mut runs);
                        Some((at, at + sector as u64))
                    }
                    None => Some((at, at + sector as u64)),
                };
            }
        } else if let Some(run) = open.take() {
            report(run, &mut runs);
        }
        compared += n as u64;
        if compared % (1 << 30) < n as u64 || blocks.peek().is_none() {
            println!("progress bytes={compared}/{}", map.bytes());
        }
    }
    if let Some(run) = open.take() {
        report(run, &mut runs);
    }
    println!("result=compared differing_bytes={differing} differing_runs={runs}");
    Ok(())
}

fn recover(o: &Options) -> Result<(), Failure> {
    let number = o.disk()?;
    let delay = Duration::from_millis(o.optional_number("delay-ms")?.unwrap_or(0));
    let mut disk = o.open_disk()?;
    let mut journal = FileJournal::open(&o.path("journal")?)?;
    let record = load_newest(&mut journal)
        .map_err(|_| PlatformError::Refused("journal unreadable".into()))?;
    let Some(record) = record.filter(|r| r.phase != Phase::Done) else {
        println!("result=nothing-to-do");
        return Ok(());
    };
    println!(
        "resuming phase={:?} chunks_done={}",
        record.phase, record.chunks_done
    );

    // If the crash came before the entry was taken out of the table, the volume may be mounted
    // again: lock it before recovery removes the entry. Once the entry is out there is nothing
    // to lock and Windows allows the raw writes (plan gap G10).
    let in_table = disk.partition_start(record.partition)?;
    println!(
        "entry_in_table={}",
        in_table.map_or("no".to_owned(), |o| o.to_string())
    );
    let locks = match in_table {
        Some(offset) if offset == record.source_offset => {
            lock_volumes_in(number, offset, offset + record.length)?
        }
        _ => Vec::new(),
    };
    for l in &locks {
        println!("locked={}", l.path());
    }

    let identity = disk.identity()?;
    let map = map_of(&o.path("journal")?, &record)?;
    let outcome = recover_with_progress(
        &mut disk,
        &mut journal,
        identity,
        &map,
        &mut reporter(delay),
    )
    .map_err(PlatformError::Move)?;
    drop(locks);
    match outcome {
        Recovery::NothingToDo => println!("result=nothing-to-do"),
        Recovery::Completed(r) => {
            println!("result=done phase={:?} target={}", r.phase, r.target_offset)
        }
    }
    Ok(())
}
