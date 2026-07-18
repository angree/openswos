using System.Buffers.Binary;
using OpenSwos.Tools.SpriteDecode;

namespace OpenSwos.Tools.PitchDecode;

// SWOS Amiga SWCPICH*.MAP (after RNC v2 decompression).
//
// Layout (no header magic, no prefix, no suffix):
//   [0     .. 9239]      42 × 55 × 4-byte big-endian cells.
//                        Each cell = byte-offset into the tile-graphics block below.
//                        Cell value 0 means "transparent / no draw" (tile 0).
//                        Map iteration order is row-major: file[(y*42 + x)*4].
//   [9240  .. EOF]       Variable N tile graphics, 128 bytes each.
//                        Each tile = 16×16 px, 4 bitplanes, row-interleaved planar.
//                        For tile T: file offset = 9240 + T*128.
//
// Tile cell format (128 bytes per tile):
//   For row 0..15:
//     For plane 0..3:
//       2 bytes (16 pixels wide, MSB-first)
//
// Verified math against all six SWCPICH files (no modulo, exact tile counts):
//   SWCPICH1.bin: 45592 = 9240 + 284 × 128   (vertical stripes pitch)
//   SWCPICH2.bin: 39448 = 9240 + 236 × 128   (horizontal stripes)
//   SWCPICH3.bin: 43032 = 9240 + 264 × 128   (rhombus)
//   SWCPICH4.bin: 39832 = 9240 + 239 × 128   (squares)
//   SWCPICH5.bin: 44312 = 9240 + 274 × 128   (diagonal stripes)
//   SWCPICH6.bin: 31384 = 9240 + 173 × 128   (training pitch — fewest tiles)
//
// Authoritative reference: benbaker76/SwosGfx (C#). zlatkok/swos-port confirms
// pitch dimensions; the Amiga m68k disassembly has the filename strings but no
// reachable XREFs to the loader.

public sealed class PitchFileException : Exception
{
    public PitchFileException(string message) : base(message) { }
}

public static class PitchFile
{
    public const int Cols = 42;
    public const int Rows = 55;                        // rows in the on-disk map
    public const int VisibleRows = 53;                 // rows actually drawn (1..53; 0 and 54 are off-screen padding)
    public const int TileSize = 16;
    public const int Planes = 4;
    public const int BytesPerTile = 128;
    public const int HeaderSize = Cols * Rows * 4;     // 9240
    public const int PixelWidth = Cols * TileSize;     // 672
    public const int PixelHeight = VisibleRows * TileSize;  // 848 — matches what original SWOS displays

    public static int TileCount(int fileSize) => (fileSize - HeaderSize) / BytesPerTile;

    // Decode a decompressed SWCPICH*.MAP into a palette-index pixel grid sized
    // [Rows*TileSize, Cols*TileSize] = [880, 672]. Every cell references a real
    // tile by byte-offset (offset 0 == tile #0, a real light-grass tile — NOT a
    // transparent marker; see the note in the loop below).
    public static byte[,] Render(byte[] decompressed)
    {
        if (decompressed.Length < HeaderSize)
            throw new PitchFileException(
                $"file too short ({decompressed.Length} < {HeaderSize}) — not a valid pitch map");

        int tileBlockBytes = decompressed.Length - HeaderSize;
        if (tileBlockBytes % BytesPerTile != 0)
            throw new PitchFileException(
                $"tile block size {tileBlockBytes} is not a multiple of {BytesPerTile}");

        var pixels = new byte[PixelHeight, PixelWidth];

        // Render only rows 1..53 from the map — rows 0 and 54 are off-screen padding
        // in original SWOS Amiga (swos-port iterates the same range). Output row
        // index = srcRow - 1, so the visible portion starts flush at the top of the image.
        for (int srcRow = 1; srcRow <= VisibleRows; srcRow++)
        {
            int dstRow = srcRow - 1;
            for (int col = 0; col < Cols; col++)
            {
                int cellOffset = (srcRow * Cols + col) * 4;
                uint tileByteOffset = BinaryPrimitives.ReadUInt32BigEndian(
                    decompressed.AsSpan(cellOffset, 4));

                // NOTE: offset 0 is NOT "transparent" — it is a valid reference to
                // tile #0 (the first tile in the block). SWCPICH1-5 never use it in
                // visible rows, but SWCPICH6 (training pitch) builds every mowed
                // light stripe from tile-#0 columns; skipping them cut each stripe
                // in half (untextured green gaps — user "dziwne pasy przeciete").
                // Always draw the tile; the bounds check below still guards the read.
                int tileFileOffset = HeaderSize + (int)tileByteOffset;
                if (tileFileOffset + BytesPerTile > decompressed.Length)
                    throw new PitchFileException(
                        $"cell ({col},{srcRow}) references tile at file offset {tileFileOffset} " +
                        $"which overflows file ({decompressed.Length} bytes)");

                var tile = Bitplane.Decode(
                    decompressed, tileFileOffset, TileSize, TileSize, Planes,
                    BitplaneLayout.Interleaved);

                int destX = col * TileSize;
                int destY = dstRow * TileSize;
                for (int dy = 0; dy < TileSize; dy++)
                    for (int dx = 0; dx < TileSize; dx++)
                        pixels[destY + dy, destX + dx] = tile[dy, dx];
            }
        }

        return pixels;
    }
}
