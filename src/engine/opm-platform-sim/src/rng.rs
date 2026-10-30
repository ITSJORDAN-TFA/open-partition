/// Small deterministic PRNG so campaigns are reproducible from a seed, with no dependencies.
#[derive(Debug, Clone)]
pub struct XorShift64(u64);

impl XorShift64 {
    pub fn new(seed: u64) -> Self {
        // Zero is a fixed point of xorshift; mix the seed so every input is usable.
        let mixed = seed.wrapping_mul(0x9E37_79B9_7F4A_7C15) ^ 0xD1B5_4A32_D192_ED03;
        Self(if mixed == 0 { 1 } else { mixed })
    }

    pub fn next_u64(&mut self) -> u64 {
        let mut x = self.0;
        x ^= x << 13;
        x ^= x >> 7;
        x ^= x << 17;
        self.0 = x;
        x
    }

    pub fn coin(&mut self) -> bool {
        self.next_u64() & 1 == 1
    }
}
