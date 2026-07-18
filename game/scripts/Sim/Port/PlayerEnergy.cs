namespace OpenSwos.Sim.Port;

using OpenSwos.SwosVm;

// ============================================================================
// OpenSWOS in-match player energy / fatigue model (SIM SIDE).
//
// This is an OpenTTD-style OPTIONAL enhancement — the original SWOS has NO
// in-match fitness/stamina mechanic (only a dead, unreferenced string
// 'FITNESS*****' at external/swos-port/swos/swos.asm:186013 with no XREF). So
// the speed penalty is gated behind EffectEnabled, which is set once at match
// setup (career match OR the master OPTIONS toggle) and never toggled
// mid-match, keeping the lockstep-netplay sim tick fully deterministic.
//
// INTEGER-ONLY, no float, no engine calls. Energy lives in the deterministic
// Memory pool (slot padding bytes 110..127, see PlayerSprite), so it participates
// in save/replay/netplay state identically to every other sprite field. Memory
// word reads are unsigned (Memory.ReadWord => ushort) and energy is always in
// 0..4096, so it fits a word with room to spare. The event drains at the bottom
// use the DETERMINISTIC sim Rng (lockstep-safe — see the note there); the
// per-tick DrainSlot is pure integer with no RNG.
// ============================================================================
public static class PlayerEnergy
{
    public const int Max = 4096;

    // OpenTTD-style OPTIONAL enhancement. The original SWOS has NO in-match
    // fitness/stamina mechanic (only a dead, unreferenced string
    // 'FITNESS*****' at external/swos-port/swos/swos.asm:186013). So the speed
    // penalty is gated: set once at match setup (career match OR the master
    // OPTIONS toggle), never toggled mid-match, keeping the sim deterministic.
    public static bool EffectEnabled;

    // Per-tick effort added to the drain accumulator while a player is moving.
    // The drain rate is (effort / divisor); a player loses 1 energy point each time
    // the accumulator reaches `divisor`. effort 100 keeps the integer math coarse
    // enough that the stamina spread below stays smooth.
    private const int kMoveEffort    = 100;  // outfield
    private const int kKeeperEffort  = 20;   // keeper drains ~5x slower
    // Stamina→drain (durability). divisor = (kStaminaFloor + stamina) * kDivisorScale;
    // higher divisor = slower drain = more durability. Tuned to the user's spec:
    //   - stamina-7 vs stamina-1 durability ratio = 1.67:
    //     (8+7)/(8+1) = 15/9 = 1.667.
    //   - whole reserve ×0.9 vs the 1.40 tune (players ran out too easily-late):
    //     mid (stamina4) (8+4)*24 = 288 vs the old (14+4)*18 = 324 → 0.89×.
    private const int kStaminaFloor  = 8;
    private const int kDivisorScale  = 24;

    // Reset before a new match's team load. Energy itself is (re)seeded per
    // player by SeedSlot during TeamDataLoader.WritePlayerInfos.
    public static void ResetForNewMatch() { EffectEnabled = false; }

    // Seed one physical sprite slot (0..21) at match start from the player's
    // career stamina (0..7) and carried between-match fatigue (0..100).
    // Non-career players pass stamina=7, fatigueCarry=0 => full energy.
    public static void SeedSlot(int globalSlot, int stamina, int fatigueCarry)
    {
        if (globalSlot < 0 || globalSlot >= OpenSwos.SwosVm.PlayerSprite.TotalSlots) return;
        int s = System.Math.Clamp(stamina, 0, 7);
        int fc = System.Math.Clamp(fatigueCarry, 0, 100);
        // Carried fatigue and low stamina reduce starting freshness; floor 40%.
        int initial = Max - fc * (Max * 6 / 10) / 100 - (7 - s) * 24;
        initial = System.Math.Clamp(initial, Max * 4 / 10, Max);
        int b = OpenSwos.SwosVm.PlayerSprite.Base(globalSlot);
        OpenSwos.SwosVm.Memory.WriteWord(b + OpenSwos.SwosVm.PlayerSprite.OffEnergy, initial);
        OpenSwos.SwosVm.Memory.WriteWord(b + OpenSwos.SwosVm.PlayerSprite.OffEnergyAcc, 0);
        OpenSwos.SwosVm.Memory.WriteByte(b + OpenSwos.SwosVm.PlayerSprite.OffStamina, s);
    }

    // Per-player drain, called from UpdatePlayers per-team-tick loop. Always
    // runs (so the energy bar shows drain even when the speed EFFECT is off);
    // only integer reads/writes into Memory, fully deterministic.
    public static void DrainSlot(int spriteAddr)
    {
        int isMoving = OpenSwos.SwosVm.Memory.ReadByte(spriteAddr + OpenSwos.SwosVm.PlayerSprite.OffIsMoving);
        if (isMoving == 0) return;   // no drain while stationary; no in-match recovery

        int gslot = (spriteAddr - OpenSwos.SwosVm.PlayerSprite.SpritePoolBase) / OpenSwos.SwosVm.PlayerSprite.SlotStride;
        bool keeper = gslot == OpenSwos.SwosVm.PlayerSprite.SlotGoalie1 || gslot == OpenSwos.SwosVm.PlayerSprite.SlotGoalie2;
        int effort = keeper ? kKeeperEffort : kMoveEffort;

        int stamina = OpenSwos.SwosVm.Memory.ReadByte(spriteAddr + OpenSwos.SwosVm.PlayerSprite.OffStamina);
        int divisor = (kStaminaFloor + System.Math.Clamp(stamina, 0, 7)) * kDivisorScale;   // flattened stamina spread

        int acc = OpenSwos.SwosVm.Memory.ReadWord(spriteAddr + OpenSwos.SwosVm.PlayerSprite.OffEnergyAcc) + effort;
        int energy = OpenSwos.SwosVm.Memory.ReadWord(spriteAddr + OpenSwos.SwosVm.PlayerSprite.OffEnergy);
        while (acc >= divisor && energy > 0) { acc -= divisor; energy--; }
        if (energy < 0) energy = 0;
        OpenSwos.SwosVm.Memory.WriteWord(spriteAddr + OpenSwos.SwosVm.PlayerSprite.OffEnergyAcc, acc);
        OpenSwos.SwosVm.Memory.WriteWord(spriteAddr + OpenSwos.SwosVm.PlayerSprite.OffEnergy, energy);
    }

    // Speed-step reduction for a tired player, using the port's one-skill-point
    // speed step (46, per kPlayerSpeedsGameInProgress in PlayerActions.cs). Caller
    // multiplies by 46 and subtracts from newSpeed. Capped at -3 points, and -3
    // only kicks in below 10% (user spec):
    //   >50%   -> 0
    //   25-50% -> 1  (-46)
    //   10-25% -> 2  (-92)
    //   <10%   -> 3  (-138, hard cap)
    public static int SpeedStep(int spriteAddr)
    {
        int energy = OpenSwos.SwosVm.Memory.ReadWord(spriteAddr + OpenSwos.SwosVm.PlayerSprite.OffEnergy);
        if (energy > Max * 50 / 100) return 0;
        if (energy > Max * 25 / 100) return 1;
        if (energy > Max * 10 / 100) return 2;
        return 3;
    }

    // Shot-power penalty (skill points) for an exhausted player: -1 below 10%,
    // else 0. Gated on EffectEnabled. User spec.
    public static int ShotPenalty(int spriteAddr)
    {
        if (!EffectEnabled) return 0;
        int energy = OpenSwos.SwosVm.Memory.ReadWord(spriteAddr + OpenSwos.SwosVm.PlayerSprite.OffEnergy);
        return energy <= Max * 10 / 100 ? 1 : 0;
    }

    // True when a player is exhausted enough (<20% energy) to double their injury
    // risk on a tackle. Gated on EffectEnabled. User spec.
    public static bool InjuryRiskDoubled(int spriteAddr)
    {
        if (!EffectEnabled) return false;
        int energy = OpenSwos.SwosVm.Memory.ReadWord(spriteAddr + OpenSwos.SwosVm.PlayerSprite.OffEnergy);
        return energy < Max * 20 / 100;
    }

    public static int ReadEnergy(int globalSlot)
    {
        if (globalSlot < 0 || globalSlot >= OpenSwos.SwosVm.PlayerSprite.TotalSlots) return Max;
        return OpenSwos.SwosVm.Memory.ReadWord(
            OpenSwos.SwosVm.PlayerSprite.Base(globalSlot) + OpenSwos.SwosVm.PlayerSprite.OffEnergy);
    }

    // ---- event drains (RNG) --------------------------------------------------
    // These consume the DETERMINISTIC sim Rng (same stream as duels/injuries), so
    // they stay lockstep-safe: both netplay peers share the fixed per-match
    // EffectEnabled setting, so they draw identically. Gated on EffectEnabled, so
    // when fatigue is off the RNG stream is untouched.

    // Being tackled costs a random 1..5% of the tackled player's CURRENT energy
    // (user spec). A slide tackle takes it out of you.
    public static void DrainOnTackle(int spriteAddr)
    {
        if (!EffectEnabled) return;
        int energy = OpenSwos.SwosVm.Memory.ReadWord(spriteAddr + OpenSwos.SwosVm.PlayerSprite.OffEnergy);
        if (energy <= 0) return;
        int pct = 1 + (Rng.NextByte() % 5);   // 1..5
        energy = System.Math.Max(0, energy - energy * pct / 100);
        OpenSwos.SwosVm.Memory.WriteWord(spriteAddr + OpenSwos.SwosVm.PlayerSprite.OffEnergy, energy);
    }

    // A keeper tires per ball caught/held: a random 1..3% of current energy (user
    // spec). This is the keeper's main fatigue source (they barely move).
    public static void DrainOnKeeperCatch(int spriteAddr)
    {
        if (!EffectEnabled) return;
        int energy = OpenSwos.SwosVm.Memory.ReadWord(spriteAddr + OpenSwos.SwosVm.PlayerSprite.OffEnergy);
        if (energy <= 0) return;
        int pct = 1 + (Rng.NextByte() % 3);   // 1..3
        energy = System.Math.Max(0, energy - energy * pct / 100);
        OpenSwos.SwosVm.Memory.WriteWord(spriteAddr + OpenSwos.SwosVm.PlayerSprite.OffEnergy, energy);
    }

    // Keeper save-skill penalty from fatigue (user spec): a tired keeper saves
    // worse. -1 skill point at <=50% energy, -2 at <=20%. Gated on EffectEnabled.
    public static int KeeperSkillPenalty(int spriteAddr)
    {
        if (!EffectEnabled) return 0;
        int energy = OpenSwos.SwosVm.Memory.ReadWord(spriteAddr + OpenSwos.SwosVm.PlayerSprite.OffEnergy);
        if (energy <= Max * 20 / 100) return 2;
        if (energy <= Max * 50 / 100) return 1;
        return 0;
    }
}
