using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace HueReka
{
    static class AmbienceTests
    {
        internal const string ColorBulb = "{\"on\":true,\"bri\":100,\"hue\":0,\"sat\":0,\"ct\":300,\"reachable\":true}";
        internal const string ColorOnly = "{\"on\":true,\"bri\":100,\"hue\":0,\"sat\":0,\"reachable\":true}";
        internal const string WhiteBulb = "{\"on\":true,\"bri\":100,\"ct\":300,\"reachable\":true}";
        internal const string Dimmable = "{\"on\":true,\"bri\":100,\"reachable\":true}";
        internal const string Plug = "{\"on\":true,\"reachable\":true}";

        static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
        internal static Light MakeLight(string id, string stateJson) { return new Light(id, Json.Map(Json.Parse("{\"name\":\"Light " + id + "\",\"state\":" + stateJson + "}"))); }

        public static void Run()
        {
            PaletteTests();
        }

        static void PaletteTests()
        {
            var none = new string[0];
            Assert(Palettes.Presets.Select(p => p.Name).SequenceEqual(new[] { "Sunset", "Ocean", "Forest", "Candlelight", "Aurora", "Daylight" }), "Built-in presets in order");
            Assert(Palettes.Presets.All(p => p.Problem(Palettes.Presets.Where(other => other != p).Select(other => other.Name)) == null), "Every preset is valid");
            var two = new[] { PaletteEntry.Rgb(255, 0, 0), PaletteEntry.White(2700) };
            Assert(new Palette("Cozy", two).Problem(none) == null, "Valid custom palette");
            Assert(new Palette("  ", two).Problem(none) != null, "Blank name rejected");
            Assert(new Palette(new string('x', 41), two).Problem(none) != null, "41-character name rejected");
            Assert(new Palette(new string('x', 40), two).Problem(none) == null, "40-character name accepted");
            Assert(new Palette(" sunset ", two).Problem(new[] { "Sunset" }) != null, "Duplicate names compare trimmed and ignoring case");
            Assert(new Palette("Solo", PaletteEntry.Rgb(1, 2, 3)).Problem(none) != null, "One entry is too few");
            Assert(new Palette("Big", Enumerable.Repeat(PaletteEntry.White(3000), 13).ToArray()).Problem(none) != null, "Thirteen entries are too many");
            Assert(new Palette("Big", Enumerable.Repeat(PaletteEntry.White(3000), 12).ToArray()).Problem(none) == null, "Twelve entries are allowed");
            Assert(new Palette("Hot", PaletteEntry.White(1999), PaletteEntry.White(3000)).Problem(none) != null, "Too-warm white rejected");
            Assert(new Palette("Cold", PaletteEntry.White(6501), PaletteEntry.White(3000)).Problem(none) != null, "Too-cool white rejected");
            Assert(new Palette("Bad", PaletteEntry.Rgb(256, 0, 0), PaletteEntry.White(3000)).Problem(none) != null, "RGB above 255 rejected");
            Assert(new Palette("Neg", new PaletteEntry { R = 1, Kelvin = -5 }, PaletteEntry.White(3000)).Problem(none) != null, "Negative Kelvin rejected");
            Assert(new Palette("Hole", PaletteEntry.White(3000), null).Problem(none) != null, "Empty entry rejected");
            Assert(new Palette { Name = "Nothing", Entries = null }.Problem(none) != null, "Missing entry list rejected");
            var original = new Palette("Cozy", two); var copy = original.Copy(); copy.Entries[0].R = 9; copy.Name = "Other";
            Assert(original.Entries[0].R == 255 && original.Name == "Cozy", "Copy never changes the original");
            Assert(PaletteEntry.White(2200).Label() == "2200 K white" && PaletteEntry.Rgb(255, 136, 0).Label() == "#FF8800", "Entry labels");

            Assert(HueColor.Mired(2000, 153, 500) == 500 && HueColor.Mired(6500, 153, 500) == 154 && HueColor.Mired(4000, 153, 500) == 250, "Kelvin to mired");
            Assert(HueColor.Mired(6500, 200, 454) == 200 && HueColor.Mired(2000, 153, 454) == 454, "Mired clamps to the bulb's range");
            var red = HueColor.FromRgb(255, 0, 0);
            Assert(red.Hue == 0 && red.Sat == 254 && red.Bri == 254, "Red to Hue HSV");
            Assert(HueColor.FromRgb(0, 0, 255).Hue == 43690 && HueColor.FromRgb(0, 0, 0).Sat == 0, "Blue hue and black");
            var warm = HueColor.FromKelvin(2000); var cool = HueColor.FromKelvin(6500);
            Assert(warm.R == 255 && warm.B < 40 && cool.B > 240, "Kelvin preview runs from amber to near white");
            Assert(PaletteEntry.Rgb(300, -4, 10).PreviewColor().R == 255, "Preview clamps bad RGB instead of throwing");

            var settings = new Settings { CustomPalettes = new List<Palette> {
                new Palette("Cozy", two), new Palette("Broken", PaletteEntry.White(9000), PaletteEntry.White(3000)), null,
                new Palette("COZY", two), new Palette("Ocean", two) } };
            Assert(Palettes.Custom(settings).Select(p => p.Name).SequenceEqual(new[] { "Cozy" }), "Invalid, duplicate and preset-named saved palettes are skipped");
            Assert(Palettes.All(settings).Count == 7 && Palettes.Find(settings, " cozy ").Name == "Cozy" && Palettes.Find(settings, "Nope") == null && Palettes.Find(settings, null) == null, "Find palettes by name");
            Assert(Palettes.IsPreset("aurora") && !Palettes.IsPreset("Cozy"), "Preset detection ignores case");
            Assert(Palettes.Custom(new Settings { CustomPalettes = null }).Count == 0, "A missing palette list loads as empty");
        }
    }
}
