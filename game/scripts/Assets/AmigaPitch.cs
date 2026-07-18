using Godot;
using OpenSwos.Tools.PitchDecode;
using OpenSwos.Tools.RncDecode;
using OpenSwos.Tools.SpriteDecode;

namespace OpenSwos.Assets;

// Loads an Amiga pitch map (SWCPICH*.MAP). Auto-detects RNC v2 compression.
// Produces a 672×880 palette-indexed grid + a Godot Image baked with the
// canonical SWOS palette. Every map cell references a real tile (offset 0 == a
// real tile, not transparent). Only palette *index* 0 inside a decoded tile
// gets the grass-green fallback below.
public sealed class AmigaPitch
{
    public byte[,] Indices { get; }
    public byte[] PaletteRgb { get; }

    public int Width => PitchFile.PixelWidth;
    public int Height => PitchFile.PixelHeight;

    private AmigaPitch(byte[,] indices, byte[] paletteRgb)
    {
        Indices = indices;
        PaletteRgb = paletteRgb;
    }

    public static AmigaPitch Load(string path)
    {
        byte[] raw = System.IO.File.ReadAllBytes(path);
        byte[] payload = (raw.Length >= 4 && raw[0] == 'R' && raw[1] == 'N' && raw[2] == 'C')
            ? Rnc.Decode(raw)
            : raw;
        var pixels = PitchFile.Render(payload);
        return new AmigaPitch(pixels, Palette.SwosAmigaDefault());
    }

    // Bake into a Godot Image. Pixels with palette index 0 (idx=0 pixels inside
    // drawn tiles) become grass green —
    // because in actual SWOS Amiga gameplay palette[0] is the screen-clear colour, and
    // before the pitch is drawn the screen is cleared to grass. Stand-area gaps will
    // look slightly greenish; we accept that to keep the field looking like a football
    // pitch instead of an ocean.
    public Image ToImage()
    {
        var grass = (r: (byte)0x1F, g: (byte)0x73, b: (byte)0x2E);
        var bytes = new byte[Width * Height * 4];
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                byte idx = Indices[y, x];
                int o = (y * Width + x) * 4;
                if (idx == 0)
                {
                    bytes[o + 0] = grass.r;
                    bytes[o + 1] = grass.g;
                    bytes[o + 2] = grass.b;
                    bytes[o + 3] = 255;
                }
                else
                {
                    int p = idx * 3;
                    bytes[o + 0] = PaletteRgb[p + 0];
                    bytes[o + 1] = PaletteRgb[p + 1];
                    bytes[o + 2] = PaletteRgb[p + 2];
                    bytes[o + 3] = 255;
                }
            }
        }
        return Image.CreateFromData(Width, Height, false, Image.Format.Rgba8, bytes);
    }
}
