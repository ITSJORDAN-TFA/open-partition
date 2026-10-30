//! `opm-engine`: drives the journaled mover against a disk.
//!
//! This build can only write to file-backed virtual disks (mounted VHD/VHDX); see the write
//! guard in `opm-platform-win32`. Output is `key=value` lines so the test harness can parse it.

use std::collections::HashMap;
use std::path::PathBuf;
use std::process::ExitCode;
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use opm_core::journal::load_newest;
use opm_core::mover::{default_chunk_size, validate};
use opm_core::{
    recover_with_progress, start_move_with_progress, BlockDevice, MoveJob, PartitionTable, Phase,
    Progress, RangeDigest, Recovery,
};
use opm_platform_win32::{find_volume, FileJournal, LockedVolume, PhysicalDisk, PlatformError};

const USAGE: &str = "\
usage:
  opm-engine info    --disk N
  opm-engine move    --disk N --from BYTES --to BYTES --journal PATH [--chunk BYTES] [--no-verify] [--delay-ms MS]
  opm-engine recover --disk N --journal PATH [--delay-ms MS]
  opm-engine hash    --disk N --offset BYTES --length BYTES

Only VHD/VHDX disks can be opened by this build.";

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
        "hash" => hash(&options),
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
        if name == "no-verify" {
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

    fn disk(&self) -> Result<u32, Failure> {
        u32::try_from(self.number("disk")?)
            .map_err(|_| Failure::Usage("--disk is out of range".into()))
    }
}

fn info(o: &Options) -> Result<(), Failure> {
    let disk = PhysicalDisk::open(o.disk()?)?;
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
    let mut disk = PhysicalDisk::open(o.disk()?)?;
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
        println!(
            "progress chunks={}/{} bytes={}/{}",
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

    let mut disk = PhysicalDisk::open(number)?;
    let layout = disk.layout()?;
    let entry = layout
        .entries()
        .into_iter()
        .find(|e| e.offset == from)
        .ok_or_else(|| PlatformError::Refused(format!("no partition starts at byte {from}")))?;
    let key = layout
        .key_of(&entry)
        .ok_or_else(|| PlatformError::Refused("MBR logical partitions cannot be moved".into()))?;
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

    let mut journal = FileJournal::open(&o.path("journal")?)?;
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
    let lock = match find_volume(number, from)? {
        Some(path) => Some(LockedVolume::lock(&path)?),
        None => None,
    };
    if let Some(l) = &lock {
        println!("locked={}", l.path());
    }

    let identity = disk.identity()?;
    println!(
        "moving offset={from} to={to} length={} chunk={chunk}",
        entry.length
    );
    let record = start_move_with_progress(
        &mut disk,
        &mut journal,
        identity,
        &job,
        &[],
        &mut reporter(delay),
    )
    .map_err(PlatformError::Move)?;
    drop(lock);
    if let Some(digest) = record.source_digest {
        println!("verified_sha256={}", hex(&digest));
    }
    println!("result=done phase={:?}", record.phase);
    Ok(())
}

fn recover(o: &Options) -> Result<(), Failure> {
    let number = o.disk()?;
    let delay = Duration::from_millis(o.optional_number("delay-ms")?.unwrap_or(0));
    let mut disk = PhysicalDisk::open(number)?;
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
    let lock = match in_table {
        Some(offset) if offset == record.source_offset => match find_volume(number, offset)? {
            Some(path) => {
                println!("locked={path}");
                Some(LockedVolume::lock(&path)?)
            }
            None => None,
        },
        _ => None,
    };

    let identity = disk.identity()?;
    let outcome = recover_with_progress(&mut disk, &mut journal, identity, &mut reporter(delay))
        .map_err(PlatformError::Move)?;
    drop(lock);
    match outcome {
        Recovery::NothingToDo => println!("result=nothing-to-do"),
        Recovery::Completed(r) => {
            println!("result=done phase={:?} target={}", r.phase, r.target_offset)
        }
    }
    Ok(())
}
