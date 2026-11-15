using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces;

namespace NINA.Plugin.InternalGuider {
    /// <summary>Résultat du gain automatique.</summary>
    public class AutoGainResult {
        public int OldGain;
        public int Gain;
        public int StarsBefore;
        public int StarsAfter;
        public double Exposure;
        public bool DarkMissing;
        public string Summary;
    }

    /// <summary>
    /// Gain automatique : cherche le gain le plus élevé qui ne sature aucune étoile, pour voir le plus d'étoiles utilisables.
    /// 1. Trouve un gain de référence sans étoile saturée (en baissant le gain tant qu'il y en a).
    /// 2. Mesure la loi de gain de la caméra avec un second gain (rapport des flux des mêmes étoiles).
    /// 3. Vise un pic de l'étoile la plus brillante à 78 % de la pleine échelle, puis vérifie ce gain et ses voisins
    ///    et garde celui qui donne le plus d'étoiles utilisables sans saturation.
    /// </summary>
    public partial class PreviewViewModel {
        public bool CanAutoGain => CanSetGain && !MonoMode && !Simulation && !IsCalibrating && !IsGuiding;

        private class GainShot {
            public int Gain;
            public List<StarInfo> Stars = new List<StarInfo>();
            public int Usable;
            public int Saturated;
            public double Background;
            public double SatLevel;
            public double MaxPeak;      // pic (au-dessus du fond) de l'étoile non saturée la plus brillante
            public int Width, Height;
        }

        private const double AutoGainTarget = 0.78;

        private async Task<GainShot> MeasureGainAsync(ICamera camera, int gain, CancellationToken token) {
            pendingGain = gain;
            // Première image : sert à installer le gain (certaines caméras la renvoient avec l'ancien) ; seule la seconde est analysée
            await CaptureRawAsync(camera, Exposure, token).ConfigureAwait(false);
            var shot = await CaptureRawAsync(camera, Exposure, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var r = StarDetector.Analyze(shot.Raw, shot.Width, shot.Height, shot.BitDepth);
            int depth = (shot.BitDepth <= 0 || shot.BitDepth > 16) ? 16 : shot.BitDepth;
            double sat = (1 << depth) - 1;
            ushort rawMax = 0;
            for (int i = 0; i < shot.Raw.Length; i += 7) if (shot.Raw[i] > rawMax) rawMax = shot.Raw[i];
            if (rawMax > sat) sat = 65535;
            var g = new GainShot { Gain = gain, Stars = r.Stars, Background = r.Background, SatLevel = sat, Width = shot.Width, Height = shot.Height };
            foreach (var s in r.Stars) {
                if (s.Saturated) { g.Saturated++; continue; }
                if (s.Hfd < 0.8) continue;      // pixel chaud ou parasite
                if (s.Snr >= 8) g.Usable++;
                if (s.Peak > g.MaxPeak) g.MaxPeak = s.Peak;
            }
            Logger.Info($"InternalGuider: gain automatique : gain {gain} → {g.Usable} étoiles utilisables, {g.Saturated} saturée(s), pic max {g.MaxPeak:0}, fond {g.Background:0}");
            return g;
        }

        /// <summary>Pente du logarithme du signal par unité de gain, d'après les mêmes étoiles mesurées à deux gains (0 si indéterminée).</summary>
        private static double GainSlope(GainShot a, GainShot b) {
            var ratios = new List<double>();
            foreach (var s in a.Stars) {
                if (s.Saturated || s.Flux <= 0 || s.Snr < 8) continue;
                StarInfo best = null; double bd = 64;
                foreach (var t in b.Stars) {
                    double d = (t.X - s.X) * (t.X - s.X) + (t.Y - s.Y) * (t.Y - s.Y);
                    if (d < bd) { bd = d; best = t; }
                }
                if (best != null && !best.Saturated && best.Flux > 0) ratios.Add(best.Flux / s.Flux);
            }
            if (ratios.Count < 3 || a.Gain == b.Gain) return 0;
            ratios.Sort();
            double med = ratios[ratios.Count / 2];
            double slope = Math.Log(med) / (b.Gain - a.Gain);
            return slope > 0.0005 && slope < 0.1 ? slope : 0;
        }

        /// <summary>Cherche le meilleur gain. Le Stop (ou CancelCalibration) l'interrompt ; le gain d'origine est alors rétabli.</summary>
        public async Task<AutoGainResult> AutoGainAsync(bool unattended = false) {
            if (calibrating || guiding || cameraRef == null) throw new InvalidOperationException("calibration ou guidage en cours, ou caméra non connectée");
            if (Simulation) throw new InvalidOperationException("indisponible en mode simulation");
            if (MonoMode) throw new InvalidOperationException("indisponible en caméra unique (le gain de la caméra principale ne doit pas changer)");
            var camera = cameraRef;
            if (!GainIsAdjustable(camera)) throw new InvalidOperationException("le gain de cette caméra n'est pas réglable");

            int gMin = camera.GainMin, gMax = camera.GainMax;
            int step = Math.Max(3, (gMax - gMin) / 25);
            int original = GainKey(camera);
            var shots = new Dictionary<int, GainShot>();
            AutoGainResult result = null;
            IsCalibrating = true;
            try {
                await Task.Run(() => Stop());
                calibrationCts = new CancellationTokenSource();
                var token = calibrationCts.Token;

                async Task<GainShot> Get(int gain) {
                    gain = Math.Max(gMin, Math.Min(gMax, gain));
                    if (shots.TryGetValue(gain, out var known)) return known;
                    Publish(null, $"Gain automatique : mesure au gain {gain}…");
                    var m = await MeasureGainAsync(camera, gain, token).ConfigureAwait(false);
                    shots[gain] = m;
                    return m;
                }

                // 1. Gain de référence sans étoile saturée
                int g = original < 0 ? gMin : original;
                var reference = await Get(g);
                int before = reference.Usable;
                for (int i = 0; i < 6 && reference.Saturated > 0 && g > gMin; i++) {
                    g = Math.Max(gMin, g - Math.Max(step * 2, (int)(g * 0.2)));
                    reference = await Get(g);
                }
                if (reference.Saturated > 0 || reference.MaxPeak <= 0) {
                    pendingGain = original;
                    result = new AutoGainResult {
                        OldGain = original, Gain = original, StarsBefore = before, StarsAfter = before, Exposure = Exposure,
                        Summary = reference.MaxPeak <= 0
                            ? "Gain automatique : aucune étoile mesurable. Vérifiez la mise au point, la pose et le champ, puis recommencez."
                            : "Gain automatique : des étoiles restent saturées même au gain minimum. Réduisez la pose, puis recommencez."
                    };
                    return result;
                }

                // 2. Loi de gain
                int second = reference.Gain + step * 3 <= gMax ? reference.Gain + step * 3 : reference.Gain - step * 3;
                double slope = 0;
                if (second != reference.Gain && second >= gMin && second <= gMax) slope = GainSlope(reference, await Get(second));
                string law = "mesurée";
                if (slope <= 0) { slope = Math.Log(10) / 200.0; law = "estimée (0,1 dB par unité)"; }

                // 3. Gain visé : pic de l'étoile la plus brillante à 78 % de la pleine échelle
                double room = AutoGainTarget * reference.SatLevel - reference.Background;
                double factor = room > 0 ? room / reference.MaxPeak : 1;
                int target = reference.Gain + (int)Math.Round(Math.Log(Math.Max(0.05, factor)) / slope);
                target = Math.Max(gMin, Math.Min(gMax, target));

                // 4. Vérification autour du gain visé. Quand une série de darks existe pour cette pose, on ne choisit que parmi ses gains :
                //    chaque gain a alors son dark exact, sans en refaire (les darks ne se mettent pas à l'échelle d'un gain à l'autre :
                //    le niveau peut faire un saut brusque, par exemple au changement de gain de conversion d'une caméra CMOS).
                var grid = GuiderSettings.UseDarkLibrary ? DarkLibrary.GainsFor(camera.Id, Exposure, reference.Width, reference.Height) : new List<int>();
                bool onGrid = grid.Count >= 3;
                int gridIndex = 0;
                List<int> candidates;
                if (onGrid) {
                    gridIndex = Math.Max(0, grid.FindLastIndex(x => x <= target));
                    candidates = new List<int> { grid[gridIndex] };
                    if (gridIndex + 1 < grid.Count) candidates.Add(grid[gridIndex + 1]);
                    if (gridIndex > 0) candidates.Add(grid[gridIndex - 1]);
                } else {
                    candidates = new[] { target - step, target, target + step }.Select(x => Math.Max(gMin, Math.Min(gMax, x))).Distinct().ToList();
                }
                GainShot best = null;
                foreach (int c in candidates) {
                    var m = await Get(c);
                    if (m.Saturated > 0) continue;
                    if (best == null || m.Usable > best.Usable || (m.Usable == best.Usable && m.Gain < best.Gain)) best = m;
                }
                // Tous saturés : on descend dans la série jusqu'à un gain sans saturation
                for (int i = gridIndex - 2; onGrid && best == null && i >= 0; i--) {
                    var m = await Get(grid[i]);
                    if (m.Saturated == 0) best = m;
                }
                // Sans opérateur, on ne peut pas créer de dark : on préfère un gain qui en a déjà un, s'il ne coûte presque aucune étoile
                if (unattended && best != null && GuiderSettings.UseDarkLibrary) {
                    GainShot withDark = null;
                    foreach (var m in shots.Values) {
                        if (m.Saturated > 0 || DarkLibrary.TryLoad(camera.Id, Exposure, m.Gain, m.Width, m.Height) == null) continue;
                        if (withDark == null || m.Usable > withDark.Usable) withDark = m;
                    }
                    if (withDark != null && withDark.Usable >= 0.9 * best.Usable) best = withDark;
                }
                if (best == null || (!onGrid && reference.Saturated == 0 && reference.Usable >= best.Usable && reference.Gain < best.Gain)) best = reference;

                result = new AutoGainResult {
                    OldGain = original, Gain = best.Gain, StarsBefore = before, StarsAfter = best.Usable, Exposure = Exposure
                };
                pendingGain = best.Gain;
                GuideCameraSettings.SetGain(camera.Id, best.Gain);
                gainText = best.Gain.ToString();
                RaisePropertyChanged(nameof(GainText));
                if (GuiderSettings.UseDarkLibrary && DarkLibrary.TryLoad(camera.Id, Exposure, best.Gain, best.Width, best.Height) == null) result.DarkMissing = true;
                result.Summary = $"Gain automatique : gain {original} → {best.Gain} (loi de gain {law}{(onGrid ? ", gain de votre série de darks" : "")}), {before} → {best.Usable} étoiles utilisables, aucune saturée." +
                                 (result.DarkMissing ? $"\n\nIl n'y a pas de dark pour la pose de {Exposure:0.###} s à ce gain." : "");
                Logger.Info("InternalGuider: " + result.Summary.Replace("\n", " "));
            } catch (OperationCanceledException) {
                pendingGain = original;     // interrompu : on remet le gain d'origine
                result = null;
            } finally {
                calibrationCts?.Dispose();
                calibrationCts = null;
                IsCalibrating = false;
                if (!shutdown) Start(camera);
            }
            return result;
        }

        private string autoGainKey;
        private DateTime autoGainTime = DateTime.MinValue;

        /// <summary>Au démarrage d'une calibration ou d'un guidage : gain automatique si l'option est activée (au plus une fois par heure pour une même pose).</summary>
        private async Task MaybeAutoGainAsync() {
            try {
                if (!GuiderSettings.AutoGainAtStart || !CanAutoGain) return;
                string key = cameraRef.Id + "|" + Exposure.ToString("0.###");
                if (key == autoGainKey && (DateTime.UtcNow - autoGainTime).TotalMinutes < 60) return;
                autoGainKey = key;
                autoGainTime = DateTime.UtcNow;
                var r = await AutoGainAsync(true).ConfigureAwait(false);
                if (r != null) Publish(null, r.Summary.Split('\n')[0] + (r.DarkMissing ? " Pas de dark à ce gain (créez-le avec le bouton Auto)." : ""));
            } catch (Exception ex) {
                Logger.Warning("InternalGuider: gain automatique au démarrage impossible : " + ex.Message);
            }
        }

        /// <summary>Affiche un texte dans la ligne d'état de l'aperçu.</summary>
        public void ShowStatus(string text) => Publish(null, text);
    }
}
