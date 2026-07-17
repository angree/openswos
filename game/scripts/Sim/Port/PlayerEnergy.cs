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
    // Calibrated so a full ~90-game-minute match leaves an active outfielder
    // around 40-55% (was 10/2 — that drained to 30-40% by minute 15, way too
    // fast; user report). ~5x gentler.
    private const int kMoveEffort   = 2;   // outfield
    private const int kKeeperEffort = 1;   // keeper: drains slower still

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
    // Returns 0 above 50% energy, 1 below 50%, 2 below 20%.
    public static int SpeedStep(int spriteAddr)
    {
        int energy = OpenSwos.SwosVm.Memory.ReadWord(spriteAddr + OpenSwos.SwosVm.PlayerSprite.OffEnergy);
        if (energy > Max / 2) return 0;
        if (energy > Max / 5) return 1;
        return 2;
    }

    public static int ReadEnergy(int globalSlot)
    {
        if (globalSlot < 0 || globalSlot >= OpenSwos.SwosVm.PlayerSprite.TotalSlots) return Max;
        return OpenSwos.SwosVm.Memory.ReadWord(
            OpenSwos.SwosVm.PlayerSprite.Base(globalSlot) + OpenSwos.SwosVm.PlayerSprite.OffEnergy);
    }
}
