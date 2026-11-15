using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugin.InternalGuider {
    /// <summary>Résultat de l'assistant : texte du rapport et réglages recommandés (null = pas de recommandation).</summary>
    public class AssistantResult {
        public string Report;
        public double? MinMovePx;
        public int? MaxRaMs;
        public int? MaxDecMs;
        /// <summary>Sens Dec recommandé : 0 = Auto, 1 = Nord seul, 2 = Sud seul.</summary>
        public int? DecMode;
        /// <summary>Compensation du jeu Dec suggérée (ms), mesurée par l'assistant.</summary>
        public int? DecBacklashMs;
        public string CsvPath;

        public bool HasRecommendations => MinMovePx.HasValue || MaxRaMs.HasValue || MaxDecMs.HasValue || DecMode.HasValue || DecBacklashMs.HasValue;
    }

    /// <summary>
    /// Assistant de guidage, dans l'esprit de celui de PHD2 : la monture suit sans recevoir de correction pendant quelques minutes
    /// et on mesure ce que le guidage devra compenser : dérive de chaque axe, bruit de seeing, erreur périodique (RA), alignement
    /// polaire (dérive Dec). Il peut aussi mesurer le jeu mécanique en Dec (impulsions Nord puis Sud).
    /// Il en déduit des réglages indicatifs : mouvement minimal, impulsions maximales, sens des corrections en Dec.
    /// </summary>
    public class GuidingAssistant {
        private const double EdgePx = 40;
        private const int SettleMs = 300;
        private const double MinFluxRatio = 0.6;      // images sous ce flux (nuage) écartées de l'analyse
        private const int MaxMissingFrames = 15;
        private const double SiderealArcsecPerMin = 3.8197;   // erreur polaire (arcmin) = 3,8197 x dérive Dec (arcsec/min)

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private readonly Func<CancellationToken, Task<DetectionResult>> capture;
        private readonly IGuideMount mount;
        private readonly CalibrationData cal;
        private readonly double pixelScale;
        private readonly int maxStars;
        private readonly Action<string, double> progress;
        private readonly Action<DetectionResult> onFrame;

        private MultiStarTracker tracker;
        private double ux1, uy1, ux2, uy2, det;

        private class Sample {
            public double T;
            public double Ra;
            public double Dec;
        }

        public GuidingAssistant(Func<CancellationToken, Task<DetectionResult>> capture, IGuideMount mount, CalibrationData cal,
                                double pixelScale, int maxStars, Action<string, double> progress, Action<DetectionResult> onFrame) {
            this.capture = capture;
            this.mount = mount;
            this.cal = cal;
            this.pixelScale = pixelScale;
            this.maxStars = Math.Max(1, maxStars);
            this.progress = progress;
            this.onFrame = onFrame;
        }

        private void Report(string text, double fraction) {
            try { progress?.Invoke(text, Math.Max(0, Math.Min(1, fraction))); } catch (Exception) { }
        }

        /// <param name="durationSec">Durée de la mesure sans correction.</param>
        /// <param name="measureBacklash">Mesurer aussi le jeu en Dec (impulsions Nord puis Sud).</param>
        /// <param name="context">Lignes de contexte écrites en tête du fichier CSV (matériel, calibration…).</param>
        public async Task<AssistantResult> RunAsync(int durationSec, bool measureBacklash, string context, CancellationToken ct) {
            double ra = cal.RaAngleDeg * Math.PI / 180.0;
            double de = cal.DecAngleDeg * Math.PI / 180.0;
            ux1 = Math.Cos(ra); uy1 = Math.Sin(ra);
            ux2 = Math.Cos(de); uy2 = Math.Sin(de);
            det = ux1 * uy2 - uy1 * ux2;
            if (Math.Abs(det) < 0.3) throw new CalibrationException("calibration inutilisable (axes presque parallèles) : recalibrez.");

            Report("choix des étoiles…", 0);
            var first = await capture(ct).ConfigureAwait(false);
            var candidates = first.Stars
                .Where(s => s.X > EdgePx && s.Y > EdgePx && s.X < first.Width - EdgePx && s.Y < first.Height - EdgePx)
                .ToList();
            var primary = StarSelection.Pick(candidates);
            if (primary == null) throw new CalibrationException("aucune étoile de guidage utilisable dans l'image.");
            tracker = new MultiStarTracker(maxStars, false);
            tracker.Init(first, primary);
            first.Selected = primary;
            first.Tracked = tracker.CurrentStars;
            onFrame?.Invoke(first);
            int starCount = tracker.Count;

            // --- Phase 1 : mesure sans correction ---
            var samples = new List<Sample>();
            var clock = Stopwatch.StartNew();
            int missing = 0;
            int skipped = 0;
            while (clock.Elapsed.TotalSeconds < durationSec) {
                ct.ThrowIfCancellationRequested();
                var m = await MeasureAsync(ct).ConfigureAwait(false);
                if (m == null) {
                    if (++missing > MaxMissingFrames) throw new CalibrationException("étoiles perdues pendant la mesure (nuage, dérive trop forte ?).");
                    continue;
                }
                missing = 0;
                if (m.FluxRatio < MinFluxRatio) { skipped++; continue; }
                samples.Add(new Sample { T = clock.Elapsed.TotalSeconds, Ra = m.A, Dec = m.B });
                double t = clock.Elapsed.TotalSeconds;
                Report($"mesure de la dérive sans correction : {t:0} s sur {durationSec} s · {samples.Count} images", t / durationSec * (measureBacklash ? 0.8 : 1.0));
            }
            if (samples.Count < 20) throw new CalibrationException("trop peu de mesures utilisables (nuages ?) : recommencez par ciel dégagé.");

            var result = new AssistantResult();
            var sb = new StringBuilder();
            Analyse(samples, durationSec, starCount, skipped, result, sb);

            // --- Phase 2 : jeu en Dec ---
            if (measureBacklash) {
                try {
                    await MeasureBacklashAsync(sb, result, ct).ConfigureAwait(false);
                } catch (CalibrationException ex) {
                    sb.AppendLine();
                    sb.AppendLine("Jeu en Dec : mesure impossible : " + ex.Message);
                }
            }

            // Sens Dec : si le jeu est important, le sens unique reste prudent même avec une faible dérive
            result.Report = sb.ToString().TrimEnd();
            result.CsvPath = WriteCsv(samples, context);
            if (result.CsvPath != null) result.Report += Environment.NewLine + Environment.NewLine + "Mesures enregistrées : " + result.CsvPath;
            Report("terminé", 1);
            return result;
        }

        // ---- Mesure d'une image ----

        private class Measure {
            public double A;       // erreur sur l'axe RA (pixels, positif = sens Ouest)
            public double B;       // erreur sur l'axe Dec (pixels, positif = sens Nord)
            public double FluxRatio;
        }

        private async Task<Measure> MeasureAsync(CancellationToken ct) {
            var r = await capture(ct).ConfigureAwait(false);
            var m = tracker.Update(r);
            r.Tracked = tracker.CurrentStars;
            if (m != null) { r.Selected = m.Reference; r.Tracked = m.Used; }
            onFrame?.Invoke(r);
            if (m == null) return null;
            return new Measure {
                A = (m.Dx * uy2 - m.Dy * ux2) / det,
                B = (ux1 * m.Dy - uy1 * m.Dx) / det,
                FluxRatio = m.FluxRatio
            };
        }

        // ---- Analyse de la dérive ----

        private string Both(double px) => pixelScale > 0 ? $"{px:0.00} px ({px * pixelScale:0.00}″)" : $"{px:0.00} px";

        private void Analyse(List<Sample> samples, int durationSec, int starCount, int skipped, AssistantResult result, StringBuilder sb) {
            int n = samples.Count;
            double[] t = samples.Select(s => s.T).ToArray();
            double[] a = samples.Select(s => s.Ra).ToArray();
            double[] b = samples.Select(s => s.Dec).ToArray();
            double span = t[n - 1] - t[0];
            double frameSec = span / Math.Max(1, n - 1);

            Line(a, t, out double raSlope, out double raIcpt);   // px / s
            Line(b, t, out double decSlope, out double decIcpt);
            double[] ra = Detrend(a, t, raSlope, raIcpt);
            double[] dec = Detrend(b, t, decSlope, decIcpt);
            double raSd = StdDev(ra), decSd = StdDev(dec);
            double raHf = HighFreqSigma(ra), decHf = HighFreqSigma(dec);

            sb.AppendLine($"Mesure sans correction : {n} images en {span:0} s (une image toutes les {frameSec:0.0} s), {starCount} étoile(s) suivie(s)" +
                          (skipped > 0 ? $", {skipped} image(s) écartée(s) (nuage)." : "."));
            sb.AppendLine();
            sb.AppendLine("Dérive (ce que le guidage doit compenser en continu) :");
            sb.AppendLine($"  RA : {Drift(raSlope)}");
            sb.AppendLine($"  Dec : {Drift(decSlope)}");
            sb.AppendLine();
            sb.AppendLine("Écart-type autour de la dérive (guidage coupé) :");
            sb.AppendLine($"  RA {Both(raSd)} · Dec {Both(decSd)} · total {Both(Math.Sqrt(raSd * raSd + decSd * decSd))}");
            sb.AppendLine($"  Crête à crête : RA {Both(Range(ra))} · Dec {Both(Range(dec))}");
            sb.AppendLine($"Bruit rapide d'image à image (seeing, centroïde) : RA {Both(raHf)} · Dec {Both(decHf)}");
            sb.AppendLine("  (c'est la limite que le guidage ne peut pas corriger : un RMS de guidage nettement meilleur n'est pas réaliste)");

            // Erreur périodique : un maximum de l'amplitude du spectre de la RA, si la durée couvre au moins 1,5 période
            sb.AppendLine();
            double maxPeriod = span / 1.5;
            if (n >= 40 && maxPeriod >= 60) {
                FindPeriod(ra, t, 40, maxPeriod, out double period, out double amp);
                double noiseAmp = 5 * raSd * Math.Sqrt(2.0 / n);
                if (amp > noiseAmp && amp > 0.3) {
                    sb.AppendLine($"Erreur périodique RA : composante la plus nette à {period:0} s, amplitude {Both(amp)} (crête à crête {Both(2 * amp)}).");
                    sb.AppendLine("  Sur une mesure courte l'estimation est indicative ; pour caractériser la vis ou le réducteur de la monture, mesurez au moins 2 périodes (10 min ou plus).");
                } else {
                    sb.AppendLine($"Erreur périodique RA : aucune composante nette entre 40 s et {maxPeriod:0} s (amplitude maximale {Both(amp)}).");
                }
            } else {
                sb.AppendLine("Erreur périodique RA : durée trop courte pour la mesurer (comptez 10 min ou plus).");
            }

            // Alignement polaire : la dérive en Dec due à une erreur de mise en station est au plus ε x vitesse sidérale
            double decArcsecMin = Math.Abs(decSlope) * 60 * (pixelScale > 0 ? pixelScale : 0);
            sb.AppendLine();
            if (pixelScale > 0) {
                double polar = SiderealArcsecPerMin * decArcsecMin;
                double decTotalPx = Math.Abs(decSlope) * span;
                if (decTotalPx >= Math.Max(2.0, 4 * decHf)) {
                    sb.AppendLine($"Alignement polaire : la dérive en Dec correspond à une erreur d'au moins {polar:0.0}′ (valeur minimale : elle dépend de la position dans le ciel, " +
                                  "la dérive est maximale près du méridien pour une erreur en azimut). La réfraction et la flexion s'ajoutent à cette dérive.");
                } else {
                    sb.AppendLine("Alignement polaire : dérive en Dec faible ou noyée dans le bruit sur cette durée (bon signe).");
                }
            }

            // ---- Recommandations ----
            sb.AppendLine();
            sb.AppendLine("Réglages suggérés (indicatifs, à confirmer par un essai de guidage) :");

            double hf = Math.Max(raHf, decHf);
            double minMove = Math.Max(0.10, Math.Min(0.50, Math.Round(0.8 * hf / 0.05) * 0.05));
            result.MinMovePx = minMove;
            sb.AppendLine($"  Mouvement minimal : {minMove:0.00} px (un peu sous le bruit rapide : on ne corrige pas du seeing).");

            double raRatePxMs = Math.Max(1e-6, cal.RaRatePxPerMs);
            double decRatePxMs = Math.Max(1e-6, cal.DecRatePxPerMs);
            double raFast = FastRate(a, t, 10);
            double decFast = FastRate(b, t, 10);
            result.MaxRaMs = PulseLimit(raFast, frameSec, raRatePxMs);
            result.MaxDecMs = PulseLimit(decFast, frameSec, decRatePxMs);
            sb.AppendLine($"  Impulsion maximale : RA {result.MaxRaMs} ms · Dec {result.MaxDecMs} ms " +
                          $"(vitesse de dérive la plus rapide mesurée : RA {raFast * 60:0.0} px/min, Dec {decFast * 60:0.0} px/min ; " +
                          "prévoir plus si vous faites des dithers).");

            double decTotal = Math.Abs(decSlope) * span;
            if (decTotal >= 3.0) {
                // b positif = l'étoile s'est déplacée vers le Nord : il faut des impulsions Sud
                result.DecMode = decSlope > 0 ? 2 : 1;
                sb.AppendLine($"  Sens Dec : {(decSlope > 0 ? "Sud seul" : "Nord seul")} (l'étoile dérive vers le {(decSlope > 0 ? "Nord" : "Sud")} " +
                              $"de {Math.Abs(decSlope) * 60:0.0} px/min : les corrections nécessaires sont toujours dans le même sens).");
            } else {
                result.DecMode = null;
                sb.AppendLine("  Sens Dec : dérive faible, pas de sens privilégié (Auto convient ; sens unique à préférer si le jeu mesuré est important).");
            }
        }

        private string Drift(double slopePxPerSec) {
            double pxMin = slopePxPerSec * 60;
            string s = $"{pxMin:+0.00;-0.00} px/min";
            if (pixelScale > 0) s += $" ({pxMin * pixelScale:+0.0;-0.0}″/min)";
            return s;
        }

        // ---- Mesure du jeu en Dec ----

        private async Task MeasureBacklashAsync(StringBuilder sb, AssistantResult result, CancellationToken ct) {
            if (mount == null || !mount.IsAvailable) throw new CalibrationException("monture non connectée.");
            double rate = cal.DecRatePxPerMs;    // px / ms, vitesse Nord de la calibration
            if (rate <= 0) throw new CalibrationException("vitesse Dec inconnue (recalibrez).");
            int stepMs = (int)(Math.Round(Math.Max(500, Math.Min(2000, 3.0 / rate)) / 50.0) * 50);

            Report("jeu en Dec : état initial…", 0.8);
            var mm = await MeasureAsync(ct).ConfigureAwait(false);
            if (mm == null) throw new CalibrationException("étoiles perdues.");
            double bPrev = mm.B;

            // 1. Rattrapage du jeu vers le Nord : on pousse jusqu'à deux impulsions de suite qui font bouger l'étoile
            int steps = 0;
            int good = 0;
            int firstGood = -1;
            bool cleared = false;
            while (steps < 12 && !cleared) {
                steps++;
                Report($"jeu en Dec : rattrapage vers le Nord ({steps})…", 0.8 + 0.04 * steps / 12.0);
                await PulseAsync(GuideDir.North, stepMs, ct).ConfigureAwait(false);
                var m = await MeasureAsync(ct).ConfigureAwait(false);
                if (m == null) throw new CalibrationException("étoiles perdues.");
                double moved = m.B - bPrev;
                bPrev = m.B;
                if (moved >= 0.6 * rate * stepMs) {
                    if (good == 0) firstGood = steps;
                    good++;
                } else {
                    good = 0;
                    firstGood = -1;
                }
                if (good >= 2) cleared = true;
            }
            sb.AppendLine();
            sb.AppendLine("Jeu mécanique en Dec :");
            if (!cleared) {
                sb.AppendLine($"  Le jeu n'a pas été rattrapé vers le Nord après {steps} impulsions de {stepMs} ms (plus de {steps * stepMs / 1000.0:0} s de jeu ou monture bloquée).");
                return;
            }
            double northLostMs = Math.Max(0, (firstGood - 1) * (double)stepMs);

            // 2. Quatre impulsions Nord pour mesurer la vitesse
            var tn = new List<double> { 0 };
            var bn = new List<double> { bPrev };
            double cum = 0;
            for (int i = 0; i < 4; i++) {
                Report($"jeu en Dec : mesure de la vitesse Nord ({i + 1}/4)…", 0.85);
                await PulseAsync(GuideDir.North, stepMs, ct).ConfigureAwait(false);
                var m = await MeasureAsync(ct).ConfigureAwait(false);
                if (m == null) throw new CalibrationException("étoiles perdues.");
                cum += stepMs;
                tn.Add(cum);
                bn.Add(m.B);
            }
            Line(bn.ToArray(), tn.ToArray(), out double northSlope, out _);   // px / ms
            if (northSlope <= 0) throw new CalibrationException("l'étoile ne bouge pas vers le Nord.");
            double bTop = bn[bn.Count - 1];

            // 3. Impulsions Sud jusqu'à ce que l'étoile revienne nettement (au plus 30 impulsions)
            var ts = new List<double> { 0 };
            var ds = new List<double> { 0 };
            cum = 0;
            for (int i = 0; i < 30; i++) {
                Report($"jeu en Dec : retour vers le Sud ({i + 1})…", 0.85 + 0.14 * (i + 1) / 30.0);
                await PulseAsync(GuideDir.South, stepMs, ct).ConfigureAwait(false);
                var m = await MeasureAsync(ct).ConfigureAwait(false);
                if (m == null) throw new CalibrationException("étoiles perdues.");
                cum += stepMs;
                ts.Add(cum);
                ds.Add(bTop - m.B);
                if (bTop - m.B >= 12) break;
            }

            sb.AppendLine($"  Impulsions de {stepMs} ms. Vitesse Nord : {northSlope * 1000:0.00} px/s" +
                          (pixelScale > 0 ? $" ({northSlope * 1000 * pixelScale:0.0}″/s)." : "."));
            if (northLostMs > 0) sb.AppendLine($"  Jeu rattrapé au départ vers le Nord : environ {northLostMs / 1000.0:0.0} s d'impulsions (imprécis, au pas de {stepMs} ms).");

            int count = ts.Count;
            if (count < 6 || ds[count - 1] < 6) {
                sb.AppendLine($"  Retour vers le Sud : l'étoile n'est revenue que de {ds[count - 1]:0.0} px après {cum / 1000.0:0} s d'impulsions Sud : jeu supérieur à {cum / 1000.0:0} s ou vitesse Sud très faible.");
                return;
            }
            var lastT = ts.Skip(count - 4).ToArray();
            var lastD = ds.Skip(count - 4).ToArray();
            Line(lastD, lastT, out double southSlope, out double southIcpt);   // d en px, t en ms
            if (southSlope <= 0.25 * northSlope) {
                sb.AppendLine("  Retour vers le Sud : vitesse Sud trop faible pour mesurer le jeu proprement (moins de 25 % de la vitesse Nord).");
                return;
            }
            double lostMs = Math.Max(0, -southIcpt / southSlope);
            sb.AppendLine($"  Vitesse Sud : {southSlope * 1000:0.00} px/s ({southSlope / northSlope * 100:0} % de la vitesse Nord).");
            sb.AppendLine($"  Jeu à l'inversion Nord → Sud : environ {lostMs / 1000.0:0.0} s d'impulsions" +
                          (pixelScale > 0 && northSlope > 0 ? $" (soit environ {lostMs * northSlope * pixelScale:0}″ de mouvement perdu)." : "."));
            // Compensation suggérée : 70 % du jeu mesuré (mieux vaut sous-compenser que faire un dépassement), arrondie à 50 ms
            int suggested = (int)Math.Min(3000, Math.Round(0.7 * lostMs / 50.0) * 50);
            if (suggested >= 100) {
                result.DecBacklashMs = suggested;
                sb.AppendLine($"  Compensation du jeu suggérée : {suggested} ms (70 % du jeu mesuré). Utile seulement en mode Auto ; le jeu varie avec le côté du pilier, la charge et la température.");
            }
            if (lostMs >= 1500) {
                sb.AppendLine("  Le jeu est important : évitez les inversions de sens en Dec (mode « Nord seul » ou « Sud seul », dither en RA seule).");
            } else if (lostMs >= 500) {
                sb.AppendLine("  Jeu modéré : le sens unique en Dec reste préférable, mais le mode Auto est utilisable.");
            } else {
                sb.AppendLine("  Jeu faible : le mode Auto en Dec convient.");
            }
        }

        private async Task PulseAsync(GuideDir direction, int ms, CancellationToken ct) {
            await mount.PulseAsync(direction, ms, ct).ConfigureAwait(false);
            await Task.Delay(SettleMs, ct).ConfigureAwait(false);
        }

        // ---- Fichier de mesures ----

        private string WriteCsv(List<Sample> samples, string context) {
            try {
                Directory.CreateDirectory(GuideLog.Directory);
                string path = Path.Combine(GuideLog.Directory, $"assistant_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
                var sb = new StringBuilder();
                sb.AppendLine("# Internal Guider - mesures de l'assistant de guidage (sans correction)");
                if (!string.IsNullOrEmpty(context)) {
                    foreach (var line in context.Split('\n')) sb.AppendLine("# " + line.TrimEnd('\r'));
                }
                sb.AppendLine("# Convention : RA positive = étoile déplacée dans le sens Ouest ; Dec positive = étoile déplacée vers le Nord.");
                sb.AppendLine("t_s,err_ra_px,err_dec_px");
                foreach (var s in samples) {
                    sb.AppendLine(s.T.ToString("0.000", Inv) + "," + s.Ra.ToString("0.###", Inv) + "," + s.Dec.ToString("0.###", Inv));
                }
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
                return path;
            } catch (Exception) {
                return null;
            }
        }

        // ---- Outils numériques ----

        private static void Line(double[] y, double[] t, out double slope, out double intercept) {
            int n = y.Length;
            double st = 0, sy = 0, stt = 0, sty = 0;
            for (int i = 0; i < n; i++) { st += t[i]; sy += y[i]; stt += t[i] * t[i]; sty += t[i] * y[i]; }
            double den = n * stt - st * st;
            if (n < 2 || Math.Abs(den) < 1e-12) { slope = 0; intercept = n > 0 ? sy / n : 0; return; }
            slope = (n * sty - st * sy) / den;
            intercept = (sy - slope * st) / n;
        }

        private static double[] Detrend(double[] y, double[] t, double slope, double intercept) {
            var r = new double[y.Length];
            for (int i = 0; i < y.Length; i++) r[i] = y[i] - (slope * t[i] + intercept);
            return r;
        }

        private static double StdDev(double[] v) {
            double mean = v.Average();
            return Math.Sqrt(v.Sum(x => (x - mean) * (x - mean)) / Math.Max(1, v.Length - 1));
        }

        private static double Range(double[] v) => v.Max() - v.Min();

        /// <summary>Bruit d'image à image : écart-type robuste (MAD) des différences successives, divisé par racine de 2.</summary>
        private static double HighFreqSigma(double[] v) {
            if (v.Length < 3) return 0;
            var d = new double[v.Length - 1];
            for (int i = 0; i < d.Length; i++) d[i] = v[i + 1] - v[i];
            double med = Median(d);
            double mad = Median(d.Select(x => Math.Abs(x - med)).ToArray());
            return 1.4826 * mad / Math.Sqrt(2);
        }

        private static double Median(double[] v) {
            var s = (double[])v.Clone();
            Array.Sort(s);
            int n = s.Length;
            return n % 2 == 1 ? s[n / 2] : 0.5 * (s[n / 2 - 1] + s[n / 2]);
        }

        /// <summary>Amplitude (pixels) de la meilleure sinusoïde de période comprise entre minPeriod et maxPeriod (secondes).</summary>
        private static void FindPeriod(double[] y, double[] t, double minPeriod, double maxPeriod, out double period, out double amplitude) {
            int n = y.Length;
            double mean = y.Average();
            period = 0;
            amplitude = 0;
            const int Steps = 400;
            for (int k = 0; k <= Steps; k++) {
                double p = minPeriod * Math.Pow(maxPeriod / minPeriod, k / (double)Steps);
                double w = 2 * Math.PI / p;
                double c = 0, s = 0;
                for (int i = 0; i < n; i++) {
                    double v = y[i] - mean;
                    c += v * Math.Cos(w * t[i]);
                    s += v * Math.Sin(w * t[i]);
                }
                double amp = 2.0 / n * Math.Sqrt(c * c + s * s);
                if (amp > amplitude) { amplitude = amp; period = p; }
            }
        }

        /// <summary>Vitesse maximale (95e centile) du signal sur des fenêtres d'environ windowSec secondes, en pixels par seconde.</summary>
        private static double FastRate(double[] y, double[] t, double windowSec) {
            var rates = new List<double>();
            int j = 0;
            for (int i = 0; i < y.Length; i++) {
                if (j < i) j = i;
                while (j < y.Length && t[j] - t[i] < windowSec) j++;
                if (j >= y.Length) break;
                rates.Add(Math.Abs(y[j] - y[i]) / (t[j] - t[i]));
            }
            if (rates.Count == 0) return 0;
            rates.Sort();
            return rates[Math.Min(rates.Count - 1, (int)(0.95 * rates.Count))];
        }

        /// <summary>Impulsion maximale conseillée : 2,5 fois ce que demande la dérive la plus rapide pendant une image, entre 150 et 1000 ms.</summary>
        private static int PulseLimit(double pxPerSec, double frameSec, double axisRatePxPerMs) {
            double ms = pxPerSec * frameSec / axisRatePxPerMs;
            double v = Math.Round(2.5 * ms / 50.0) * 50;
            return (int)Math.Max(150, Math.Min(1000, v));
        }
    }
}
