using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace HueReka
{
    // One stop in a palette: an RGB color, or a white given in Kelvin (Kelvin > 0).
    // Methods rather than properties, so JavaScriptSerializer saves only the fields.
    sealed class PaletteEntry
    {
        public int R, G, B, Kelvin;
        public static PaletteEntry Rgb(int r, int g, int b) { return new PaletteEntry { R = r, G = g, B = b }; }
        public static PaletteEntry White(int kelvin) { return new PaletteEntry { Kelvin = kelvin }; }
        public bool IsWhite() { return Kelvin > 0; }
        public string Problem()
        {
            if (Kelvin < 0) return "A white needs a temperature between 2000 K and 6500 K.";
            if (IsWhite()) return Kelvin < Palette.MinKelvin || Kelvin > Palette.MaxKelvin ? "Whites must be between 2000 K and 6500 K." : null;
            return InByte(R) && InByte(G) && InByte(B) ? null : "Color values must be between 0 and 255.";
        }
        public Color PreviewColor() { return IsWhite() ? HueColor.FromKelvin(Kelvin) : Color.FromArgb(Byte(R), Byte(G), Byte(B)); }
        public string Label() { return IsWhite() ? Kelvin + " K white" : "#" + Byte(R).ToString("X2") + Byte(G).ToString("X2") + Byte(B).ToString("X2"); }
        static bool InByte(int value) { return value >= 0 && value <= 255; }
        static int Byte(int value) { return Math.Max(0, Math.Min(255, value)); }
    }

    sealed class Palette
    {
        public const int MinEntries = 2, MaxEntries = 12, MaxNameLength = 40, MinKelvin = 2000, MaxKelvin = 6500;
        public string Name = "";
        public List<PaletteEntry> Entries = new List<PaletteEntry>();
        public Palette() { }
        public Palette(string name, params PaletteEntry[] entries) { Name = name; Entries = entries.ToList(); }
        public Palette Copy()
        {
            var entries = (Entries ?? new List<PaletteEntry>()).Select(e => e == null ? null : new PaletteEntry { R = e.R, G = e.G, B = e.B, Kelvin = e.Kelvin });
            return new Palette(Name, entries.ToArray());
        }
        // Why the palette can't be used, or null when it is valid. takenNames are the other palettes' names.
        public string Problem(IEnumerable<string> takenNames)
        {
            string name = (Name ?? "").Trim();
            if (name.Length == 0) return "Give the palette a name.";
            if (name.Length > MaxNameLength) return "Keep the name to " + MaxNameLength + " characters or fewer.";
            if (takenNames.Any(taken => String.Equals((taken ?? "").Trim(), name, StringComparison.OrdinalIgnoreCase))) return "A palette called " + name + " already exists.";
            if (Entries == null || Entries.Count < MinEntries) return "Add at least " + MinEntries + " colors or whites.";
            if (Entries.Count > MaxEntries) return "A palette can hold up to " + MaxEntries + " colors or whites.";
            return Entries.Select(entry => entry == null ? "A palette entry is empty." : entry.Problem()).FirstOrDefault(problem => problem != null);
        }
        public override string ToString() { return Name; }
    }

    static class Palettes
    {
        public const string DefaultName = "Sunset";
        public static readonly IList<Palette> Presets = new List<Palette> {
            new Palette("Sunset", Rgb(255, 147, 41), Rgb(255, 111, 81), Rgb(232, 92, 138), White(2200)),
            new Palette("Ocean", Rgb(20, 60, 190), Rgb(0, 150, 160), Rgb(90, 170, 240)),
            new Palette("Forest", Rgb(40, 150, 60), Rgb(120, 210, 160), White(2700)),
            new Palette("Candlelight", White(2000), White(2200), Rgb(255, 150, 40), White(2400)),
            new Palette("Aurora", Rgb(40, 220, 120), Rgb(0, 180, 170), Rgb(140, 70, 230)),
            new Palette("Daylight", White(2700), White(4000), White(5500), White(6500))
        }.AsReadOnly();

        public static bool IsPreset(string name) { return Presets.Any(p => SameName(p.Name, name)); }
        // Saved palettes that are no longer valid (edited by hand, or clashing with a preset) are skipped, never fatal.
        public static List<Palette> Custom(Settings settings)
        {
            var valid = new List<Palette>();
            foreach (var palette in settings.CustomPalettes ?? new List<Palette>())
                if (palette != null && palette.Problem(Presets.Concat(valid).Select(p => p.Name)) == null) valid.Add(palette);
            return valid;
        }
        public static List<Palette> All(Settings settings) { return Presets.Concat(Custom(settings)).ToList(); }
        public static Palette Find(Settings settings, string name) { return All(settings).FirstOrDefault(p => SameName(p.Name, name)); }
        static bool SameName(string a, string b) { return String.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase); }
        static PaletteEntry Rgb(int r, int g, int b) { return PaletteEntry.Rgb(r, g, b); }
        static PaletteEntry White(int kelvin) { return PaletteEntry.White(kelvin); }
    }

    struct HueHsv { public int Hue, Sat, Bri; }

    static class HueColor
    {
        // RGB to HSV. Hue uses 0-65535; saturation and brightness use 0-254.
        // HSV saturation is needed here; System.Drawing.Color.GetSaturation returns HSL.
        public static HueHsv FromRgb(int red, int green, int blue)
        {
            double r = red / 255.0, g = green / 255.0, b = blue / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), delta = max - min, hue = 0;
            if (delta > 0)
            {
                if (max == r) hue = 60 * ((g - b) / delta);
                else if (max == g) hue = 60 * ((b - r) / delta + 2);
                else hue = 60 * ((r - g) / delta + 4);
                if (hue < 0) hue += 360;
            }
            return new HueHsv { Hue = (int)Math.Round(hue / 360 * 65535), Sat = max == 0 ? 0 : (int)Math.Round(delta / max * 254), Bri = (int)Math.Round(max * 254) };
        }
        // Percent (1-100) to the bridge's brightness (1-254).
        public static int Brightness(int percent) { return Math.Max(1, Math.Min(254, (int)Math.Round(percent * 254.0 / 100))); }
        public static int Mired(int kelvin, int minCt, int maxCt) { return Math.Max(minCt, Math.Min(maxCt, (int)Math.Round(1000000.0 / kelvin))); }
        // Tanner Helland's black-body approximation: on-screen previews of whites, and whites on color-only bulbs.
        public static Color FromKelvin(int kelvin)
        {
            double t = Math.Max(1000, Math.Min(40000, kelvin)) / 100.0;
            double r = t <= 66 ? 255 : 329.698727446 * Math.Pow(t - 60, -0.1332047592);
            double g = t <= 66 ? 99.4708025861 * Math.Log(t) - 161.1195681661 : 288.1221695283 * Math.Pow(t - 60, -0.0755148492);
            double b = t >= 66 ? 255 : t <= 19 ? 0 : 138.5177312231 * Math.Log(t - 10) - 305.0447927307;
            return Color.FromArgb(Byte(r), Byte(g), Byte(b));
        }
        static int Byte(double value) { return (int)Math.Max(0, Math.Min(255, Math.Round(value))); }
    }
}
