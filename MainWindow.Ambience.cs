using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HueReka
{
    sealed partial class MainWindow
    {
        readonly Panel lightPage = new Panel { BackColor = Color.Transparent };
        GlassButton lightTab, ambienceTab;
        AmbiencePanel ambiencePanel;
        AmbienceRunner ambience;
        CancellationTokenSource ambienceStop;

        void BuildAmbience()
        {
            lightTab = new GlassButton { Text = "Light", AccessibleName = "Light controls" }; lightTab.SetBounds(340, 163, 100, 40); root.Controls.Add(lightTab);
            ambienceTab = new GlassButton { Text = "Ambience", AccessibleName = "Ambience" }; ambienceTab.SetBounds(446, 163, 120, 40); root.Controls.Add(ambienceTab);
            tips.SetToolTip(lightTab, "Control the selected lights directly.");
            tips.SetToolTip(ambienceTab, "Slowly fade your lights through a palette of colors and whites.");
            ambiencePanel = new AmbiencePanel(tips) { Bounds = lightPage.Bounds, Anchor = lightPage.Anchor, Visible = false };
            controls.Controls.Add(ambiencePanel);
            lightTab.Click += (sender, args) => ShowTab(false);
            ambienceTab.Click += (sender, args) => ShowTab(true);
            ambiencePanel.StartRequested += async (sender, args) => await StartAmbience(null);
            ambiencePanel.StopRequested += (sender, args) => StopAmbience("Ambience stopped. Your lights keep their current colors.");
            ambiencePanel.ChoicesChanged += async (sender, args) => { if (ambience != null) await StartAmbience(ambience.LightIds); };
            ShowTab(false);
        }

        // Shows the saved choices. Out-of-range values are clamped by the sliders, and a missing palette falls back to the first one.
        void ShowAmbienceChoices()
        {
            ambiencePanel.ShowPalettes(Palettes.All(saved), saved.AmbiencePalette);
            ambiencePanel.Mode = saved.AmbienceTogether ? AmbienceMode.Together : AmbienceMode.Drift;
            ambiencePanel.SpeedSeconds = saved.AmbienceSpeedSeconds;
            ambiencePanel.BrightnessPercent = saved.AmbienceBrightness;
            UpdateAmbienceTargets();
        }

        void ShowTab(bool showAmbience)
        {
            lightPage.Visible = !showAmbience; ambiencePanel.Visible = showAmbience;
            lightTab.Primary = !showAmbience; ambienceTab.Primary = showAmbience;
            lightTab.Invalidate(); ambienceTab.Invalidate();
            UpdateAmbienceTargets();
        }

        static string LightCount(int count) { return count == 1 ? "1 light" : count + " lights"; }

        // The given lights, else the selected lights, else every light; only reachable ones that can fade.
        List<Light> AmbienceCandidates(List<string> ids)
        {
            var all = lights.Items.Cast<Light>().ToList();
            var pool = ids != null ? all.Where(light => ids.Contains(light.Id)).ToList() : Selection.Count > 0 ? Selection : all;
            return pool.Where(light => light.Reachable && AmbiencePlanner.CanJoin(light)).ToList();
        }

        void UpdateAmbienceTargets()
        {
            if (ambiencePanel == null) return;
            ambiencePanel.CanStart = bridge != null;
            if (ambience != null) { ambiencePanel.Targets = "Running on " + LightCount(ambience.LightCount) + "."; return; }
            var usable = AmbienceCandidates(null);
            ambiencePanel.Targets = bridge == null ? "Connect your bridge to start an ambience."
                : usable.Count == 0 ? "None of these lights can change color or brightness."
                : Selection.Count == 0 ? "Plays on all " + LightCount(usable.Count) + "."
                : "Plays on " + LightCount(usable.Count) + " you selected. Ctrl+A selects all.";
        }

        async Task StartAmbience(List<string> ids)
        {
            var palette = ambiencePanel.SelectedPalette;
            if (bridge == null || palette == null) return;
            var targets = AmbienceCandidates(ids);
            if (targets.Count == 0) { status.Text = "None of these lights can change color or brightness, so there's nothing to fade."; return; }
            var options = new AmbienceOptions { Palette = palette, Mode = ambiencePanel.Mode, SpeedSeconds = ambiencePanel.SpeedSeconds, BrightnessPercent = ambiencePanel.BrightnessPercent };
            AmbienceRunner runner;
            try { runner = new AmbienceRunner(bridge, options, targets); }
            catch (ArgumentException error) { status.Text = error.Message; return; } // this runs from an event handler; never let it crash the app
            StopAmbience(null);
            var stop = new CancellationTokenSource();
            ambience = runner; ambienceStop = stop;
            ambiencePanel.Running = true; UpdateAmbienceTargets();
            status.Text = "Ambience: " + palette.Name + " on " + LightCount(targets.Count) + ", " + AmbienceSpeed.Describe(options.SpeedSeconds) + "." + RememberAmbienceChoices(options);
            try { await runner.Run(new Reporter(message => { if (ambience == runner) status.Text = message; }), stop.Token); }
            catch (Exception error) { if (ambience == runner) status.Text = error.Message; }
            finally
            {
                stop.Dispose();
                if (ambience == runner)
                {
                    ambience = null; ambienceStop = null;
                    if (!IsDisposed) { ambiencePanel.Running = false; UpdateAmbienceTargets(); }
                }
            }
        }

        // Returns an extra status sentence when the choices could not be saved; the ambience runs either way.
        string RememberAmbienceChoices(AmbienceOptions options)
        {
            saved.AmbiencePalette = options.Palette.Name; saved.AmbienceTogether = options.Mode == AmbienceMode.Together;
            saved.AmbienceSpeedSeconds = options.SpeedSeconds; saved.AmbienceBrightness = options.BrightnessPercent;
            try { SaveSettings(saved); return ""; }
            catch (Exception error) { return " (Couldn't save these choices: " + error.Message + ")"; }
        }

        void StopAmbience(string message)
        {
            if (ambience == null) return;
            ambienceStop.Cancel();
            ambience = null; ambienceStop = null;
            if (!IsDisposed) { ambiencePanel.Running = false; UpdateAmbienceTargets(); }
            if (message != null) status.Text = message;
        }

        // Lights changed by hand leave the ambience; when none are left it ends right away.
        void ExcludeFromAmbience(IEnumerable<Light> changed)
        {
            if (ambience == null) return;
            ambience.Exclude(changed.Select(light => light.Id));
            if (ambience.LightCount == 0) StopAmbience(null); else UpdateAmbienceTargets();
        }
    }
}
