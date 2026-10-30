use opm_core::{BlockDevice, IoError};

use crate::rng::XorShift64;

/// What happens to writes that were not yet flushed when power is lost.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum LossModel {
    /// Every unflushed write is lost.
    DropAll,
    /// Every unflushed write reached the media.
    KeepAll,
    /// Each sector of each unflushed write independently survives or not. This covers lost,
    /// reordered and torn writes, which is what a volatile disk cache is allowed to do.
    Random { seed: u64 },
}

/// An in-memory disk that tracks which bytes are durable.
///
/// `current` is what reads see (the disk's cache view). `durable` is what survives power loss.
/// `pending` holds writes issued since the last successful flush, in order.
#[derive(Debug, Clone)]
pub struct SimDisk {
    sector_size: u32,
    current: Vec<u8>,
    durable: Vec<u8>,
    pending: Vec<(u64, Vec<u8>)>,
    ops: u64,
    crash_at: Option<u64>,
    crashed: bool,
}

impl SimDisk {
    pub fn new(sector_size: u32, size_bytes: u64) -> Self {
        assert!(sector_size.is_power_of_two() && sector_size >= 512);
        assert_eq!(size_bytes % u64::from(sector_size), 0);
        let len = usize::try_from(size_bytes).expect("simulated disk must fit in memory");
        Self {
            sector_size,
            current: vec![0; len],
            durable: vec![0; len],
            pending: Vec::new(),
            ops: 0,
            crash_at: None,
            crashed: false,
        }
    }

    /// Build a disk whose initial contents are already durable.
    pub fn from_image(sector_size: u32, image: Vec<u8>) -> Self {
        let mut disk = Self::new(sector_size, image.len() as u64);
        disk.durable.clone_from(&image);
        disk.current = image;
        disk
    }

    /// Lose power when the disk operation with this zero-based index (counting writes and
    /// flushes) is issued. A write that triggers the crash still lands in the volatile cache,
    /// so it may be torn by the loss model.
    pub fn arm_crash(&mut self, op_index: u64) {
        self.crash_at = Some(op_index);
    }

    /// Number of writes and flushes issued so far.
    pub fn ops(&self) -> u64 {
        self.ops
    }

    pub fn has_crashed(&self) -> bool {
        self.crashed
    }

    /// The bytes reads currently return.
    pub fn contents(&self) -> &[u8] {
        &self.current
    }

    /// Simulate the power coming back: resolve unflushed writes with `model` and return a
    /// fresh disk with no crash armed. Can be called whether or not the crash fired.
    pub fn power_cycle(&self, model: LossModel) -> SimDisk {
        let mut image = self.durable.clone();
        let sector = self.sector_size as usize;
        match model {
            LossModel::DropAll => {}
            LossModel::KeepAll => {
                for (offset, data) in &self.pending {
                    let start = *offset as usize;
                    image[start..start + data.len()].copy_from_slice(data);
                }
            }
            LossModel::Random { seed } => {
                let mut rng = XorShift64::new(seed);
                for (offset, data) in &self.pending {
                    for (i, chunk) in data.chunks(sector).enumerate() {
                        if rng.coin() {
                            let start = *offset as usize + i * sector;
                            image[start..start + sector].copy_from_slice(chunk);
                        }
                    }
                }
            }
        }
        SimDisk::from_image(self.sector_size, image)
    }

    /// Count an operation and decide whether this is the one that loses power.
    fn tick(&mut self) -> Result<bool, IoError> {
        if self.crashed {
            return Err(IoError::Crashed);
        }
        let index = self.ops;
        self.ops += 1;
        if self.crash_at == Some(index) {
            self.crashed = true;
            return Ok(true);
        }
        Ok(false)
    }
}

impl BlockDevice for SimDisk {
    fn sector_size(&self) -> u32 {
        self.sector_size
    }

    fn size_bytes(&self) -> u64 {
        self.current.len() as u64
    }

    fn read_at(&mut self, offset: u64, buf: &mut [u8]) -> Result<(), IoError> {
        if self.crashed {
            return Err(IoError::Crashed);
        }
        self.check_request(offset, buf.len())?;
        let start = offset as usize;
        buf.copy_from_slice(&self.current[start..start + buf.len()]);
        Ok(())
    }

    fn write_at(&mut self, offset: u64, data: &[u8]) -> Result<(), IoError> {
        self.check_request(offset, data.len())?;
        let crash_now = self.tick()?;
        let start = offset as usize;
        self.current[start..start + data.len()].copy_from_slice(data);
        self.pending.push((offset, data.to_vec()));
        if crash_now {
            Err(IoError::Crashed)
        } else {
            Ok(())
        }
    }

    fn flush(&mut self) -> Result<(), IoError> {
        if self.tick()? {
            // Power lost before the flush completed: nothing pending is guaranteed durable.
            return Err(IoError::Crashed);
        }
        for (offset, data) in self.pending.drain(..) {
            let start = offset as usize;
            self.durable[start..start + data.len()].copy_from_slice(&data);
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const S: usize = 512;

    #[test]
    fn flushed_writes_survive_every_model() {
        for model in [LossModel::DropAll, LossModel::KeepAll, LossModel::Random { seed: 7 }] {
            let mut disk = SimDisk::new(512, 8 * 512);
            disk.write_at(0, &[0xAA; S]).unwrap();
            disk.flush().unwrap();
            let after = disk.power_cycle(model);
            assert_eq!(&after.contents()[..S], &[0xAA; S][..], "{model:?}");
        }
    }

    #[test]
    fn unflushed_writes_follow_the_model() {
        let mut disk = SimDisk::new(512, 8 * 512);
        disk.write_at(0, &[0xBB; S]).unwrap();
        assert_eq!(&disk.power_cycle(LossModel::DropAll).contents()[..S], &[0; S][..]);
        assert_eq!(&disk.power_cycle(LossModel::KeepAll).contents()[..S], &[0xBB; S][..]);
    }

    #[test]
    fn random_model_can_tear_a_multi_sector_write() {
        let mut disk = SimDisk::new(512, 64 * 512);
        disk.write_at(0, &[0xCC; 64 * S]).unwrap();
        let torn = (0..32).any(|seed| {
            let after = disk.power_cycle(LossModel::Random { seed });
            let kept = after.contents().chunks(S).filter(|c| c[0] == 0xCC).count();
            kept > 0 && kept < 64
        });
        assert!(torn);
    }

    #[test]
    fn armed_crash_stops_all_further_io() {
        let mut disk = SimDisk::new(512, 8 * 512);
        disk.arm_crash(1);
        disk.write_at(0, &[1; S]).unwrap();
        assert_eq!(disk.flush(), Err(IoError::Crashed));
        assert_eq!(disk.write_at(512, &[2; S]), Err(IoError::Crashed));
        let mut buf = [0; S];
        assert_eq!(disk.read_at(0, &mut buf), Err(IoError::Crashed));
        // The flush never completed, so the first write is not durable.
        assert_eq!(&disk.power_cycle(LossModel::DropAll).contents()[..S], &[0; S][..]);
    }

    #[test]
    fn rejects_misaligned_and_out_of_range_requests() {
        let mut disk = SimDisk::new(4096, 4 * 4096);
        assert_eq!(disk.write_at(512, &[0; 4096]), Err(IoError::Misaligned));
        assert_eq!(disk.write_at(0, &[0; 512]), Err(IoError::Misaligned));
        assert_eq!(disk.write_at(4 * 4096, &[0; 4096]), Err(IoError::OutOfRange));
    }
}
