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
// INTEGER-ONLY: no float, no RNG, no engine calls. Energy lives in the
// deterministic Memory pool (slot padding bytes 110..127, see PlayerSprite),
// so it participates in save/replay/netplay state identically to every other
// sprite field. Memory word reads are unsigned (Memory.ReadWord => ushort) and
// energy is always in 0..4096, so it fits a word with room to spare.
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
    // Calibrated so a busy outfielder in a full 90-game-minute match ends around
    // 20-30% (some visibly gassed), while a fixture that barely moves stays high.
    // History: 10 = far too fast (30-40% by min 15); 2 = far too gentle (>65% at
    // min 82). 6 is the middle ground.
    private const int kMoveEffort   = 6;   // outfield
    private const int kKeeperEffort = 2;   // keeper: drains much slower

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
        int initial = Max - fc * (Max * 6 / 10) / 100 - (7 - s) * 64;
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
        int divisor = 8 + System.Math.Clamp(stamina, 0, 7);   // 8..15: fitter drains slower

        int acc = OpenSwos.SwosVm.Memory.ReadWord(spriteAddr + OpenSwos.SwosVm.PlayerSprite.OffEnergyAcc) + effort;
        int energy = OpenSwos.SwosVm.Memory.ReadWord(spriteAddr + OpenSwos.SwosVm.PlayerSprite.OffEnergy);
        while (acc >= divisor && energy > 0) { acc -= divisor; energy--; }
        if (energy < 0) energy = 0;
        OpenSwos.SwosVm.Memory.WriteWord(spriteAddr + OpenSwos.SwosVm.PlayerSprite.OffEnergyAcc, acc);
        OpenSwos.SwosVm.Memory.WriteWord(spriteAddr + OpenSwos.SwosVm.PlayerSprite.OffEnergy, energy);
    }

    // Speed-step reduction for a tired player, using the port's one-skill-point
    // speed step (46, per the kPlayerSpeedsGameInProgress table stride in
    // PlayerActions.cs). Caller multiplies by 46 and subtracts from newSpeed.
    // Finer, harsher curve than the old 3-tier version so a near-empty player
    // visibly labours (a speed-7 sprinter on an empty tank drops ~4 skill points
    // to speed-3 pace):
    //   >70%   -> 0   (fresh)
    //   50-70% -> 1   (-46,  ~1 speed pt)
    //   35-50% -> 2   (-92,  ~2 pts)
    //   20-35% -> 3   (-138, ~3 pts)
    //   <20%   -> 4   (-184, ~4 pts — labouring)
    public static int SpeedStep(int spriteAddr)
    {
        int energy = OpenSwos.SwosVm.Memory.ReadWord(spriteAddr + OpenSwos.SwosVm.PlayerSprite.OffEnergy);
        if (energy > Max * 70 / 100) return 0;
        if (energy > Max * 50 / 100) return 1;
        if (energy > Max * 35 / 100) return 2;
        if (energy > Max * 20 / 100) return 3;
        return 4;
    }

    public static int ReadEnergy(int globalSlot)
    {
        if (globalSlot < 0 || globalSlot >= OpenSwos.SwosVm.PlayerSprite.TotalSlots) return Max;
        return OpenSwos.SwosVm.Memory.ReadWord(
            OpenSwos.SwosVm.PlayerSprite.Base(globalSlot) + OpenSwos.SwosVm.PlayerSprite.OffEnergy);
    }
}
