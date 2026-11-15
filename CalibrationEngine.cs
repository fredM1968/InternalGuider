using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NINA.Core.Utility;

namespace NINA.Plugin.InternalGuider {
    public class CalibrationException : Exception {
        public CalibrationException(string message) : base(message) { }
    }

    /// <summary>
    /// Résultat de la calibration : pour chaque axe, la direction (dans l'image du capteur) et la vitesse
    /// (pixels par milliseconde) du déplacement de l'étoile quand on envoie une impulsion vers l'OUEST (RA) ou le NORD (Dec).
    /// L'est et le sud sont l'opposé. Le guidage s'en servira pour projeter l'erreur sur les axes de la monture.
    /// </summary>
    /// <summary>Position de l'étoile pendant la calibration, relative au départ de l'axe (px) ; Back = pendant le retour.</summary>
    public class CalPoint {
        public double X { get; set; }
        public double Y { get; set; }
        public bool Back { get; set; }
    }

    public class CalibrationData {
        /// <summary>Positions mesurées de l'étoile (pour le bilan graphique de la calibration) ; vide pour les anciennes calibrations.</summary>
        public List<CalPoint> RaPoints { get; set; }
        public List<CalPoint> DecPoints { get; set; }
        public int RaSteps { get; set; }
        public int DecSteps { get; set; }
        public int StepMs { get; set; }

        public double RaAngleDeg { get; set; }
        public double DecAngleDeg { get; set; }
        public double RaRatePxPerMs { get; set; }
        public double DecRatePxPerMs { get; set; }
        /// <summary>Angle Dec - angle RA, ramené entre -180° et 180° (idéalement +90° ou -90°).</summary>
        public double OrthogonalityDeg { get; set; }
        public double RaReturnResidualPx { get; set; }
        public double DecReturnResidualPx { get; set; }
        // Diagnostic (n'influence pas le guidage) : vitesses mesurées au retour et temps d'impulsion « perdu » au début des mouvements
        public double RaReturnRatePxPerMs { get; set; }
        public double DecReturnRatePxPerMs { get; set; }
        public double RaOutLostMs { get; set; }
        public double RaBackLostMs { get; set; }
        public double DecOutLostMs { get; set; }
        public double DecBackLostMs { get; set; }
        public double PixelScale { get; set; }
        public double FocalLengthMm { get; set; }
        public double? DeclinationDeg { get; set; }
        public string PierSide { get; set; }
        public bool Simulated { get; set; }
        public DateTime Date { get; set; }
        public string Warnings { get; set; }

        private const double SiderealArcsecPerSec = 15.041;

        public string Summary() {
            string ra = Describe("RA", RaRatePxPerMs, RaAngleDeg);
            string dec = Describe("Dec", DecRatePxPerMs, DecAngleDeg);
            string text = $"{ra} · {dec} · angle entre axes {Math.Abs(OrthogonalityDeg):0}° " +
                          $"· retour au départ : RA {RaReturnResidualPx:0.0} px, Dec {DecReturnResidualPx:0.0} px";
            if (!string.IsNullOrEmpty(Warnings)) text += (Derived ? " · NOTE : " : " · ATTENTION : ") + Warnings;
            if (RaReturnRatePxPerMs > 0 || DecReturnRatePxPerMs > 0) {
                text += "\nDiagnostic : " + Diag("RA", "Ouest", "Est", RaRatePxPerMs, RaReturnRatePxPerMs, RaOutLostMs, RaBackLostMs) +
                        " · " + Diag("Dec", "Nord", "Sud", DecRatePxPerMs, DecReturnRatePxPerMs, DecOutLostMs, DecBackLostMs);
            }
            return text;
        }

        private static string Diag(string axis, string outName, string backName, double outRate, double backRate, double outLost, double backLost) {
            string ratio = outRate > 0 && backRate > 0 ? $" ({backRate / outRate * 100:0} % de l'aller)" : "";
            return $"{axis} : {outName} {outRate * 1000:0.00} px/s, {backName} {backRate * 1000:0.00} px/s{ratio}, " +
                   $"temps perdu au départ {outLost:0} ms (aller) / {backLost:0} ms (retour)";
        }

        private string Describe(string axis, double rate, double angle) {
            string s = $"{axis} {rate * 1000:0.00} px/s";
            if (PixelScale > 0) {
                double arcsec = rate * 1000 * PixelScale;
                s += $" ({arcsec:0.0}″/s = {arcsec / SiderealArcsecPerSec:0.00}× sidéral)";
            }
            return s + $", direction {angle:0}°";
        }

        /// <summary>Sens de rotation RA → Dec dans l'image : +1 ou -1 (0 si inconnu). Il change après un retournement si le pilote n'inverse pas les impulsions Dec.</summary>
        public int GetHandedness() {
            double s = Math.Sin((DecAngleDeg - RaAngleDeg) * Math.PI / 180.0);
            return Math.Abs(s) < 0.2 ? 0 : (s > 0 ? 1 : -1);
        }

        /// <summary>Vrai si cette calibration a été déduite de celle de l'autre côté du pier (après un retournement).</summary>
        public bool Derived { get; set; }

        private static string Dir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NINA", "InternalGuider");

        private static string cameraTag;

        /// <summary>
        /// Caméra de guidage dont on lit et écrit les calibrations (chaque caméra a les siennes : focale et orientation
        /// différentes). Sans caméra, on utilise les fichiers d'origine.
        /// </summary>
        public static string CameraId {
            set {
                if (string.IsNullOrEmpty(value)) { cameraTag = null; return; }
                using (var sha = System.Security.Cryptography.SHA1.Create()) {
                    var h = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(value));
                    cameraTag = BitConverter.ToString(h, 0, 5).Replace("-", "");
                }
            }
        }

        private static string Stem => cameraTag == null ? "calibration" : "calibration--" + cameraTag;

        /// <summary>Dernière calibration enregistrée, quel que soit le côté du pier.</summary>
        private static string FilePath => Path.Combine(Dir, Stem + ".json");

        private static string SideFilePath(string side) => Path.Combine(Dir, cameraTag == null ? $"calibration_{side}.json" : $"calibration--{cameraTag}--{side}.json");

        /// <summary>Nom utilisable du côté du pier ; null si inconnu (la gestion du retournement est alors désactivée).</summary>
        public static string NormalizeSide(string side) {
            if (string.IsNullOrWhiteSpace(side)) return null;
            string t = side.Trim();
            if (t.IndexOf("unknown", StringComparison.OrdinalIgnoreCase) >= 0) return null;
            foreach (char c in Path.GetInvalidFileNameChars()) t = t.Replace(c.ToString(), "");
            return t.Length == 0 ? null : t;
        }

        private static void Write(string path, CalibrationData data) {
            File.WriteAllText(path, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        }

        public static void Save(CalibrationData data) {
            try {
                Directory.CreateDirectory(Dir);
                // Avant d'écraser la « dernière calibration », on garde celle d'un autre côté dans son propre fichier
                var previous = Load();
                string prevSide = NormalizeSide(previous?.PierSide);
                if (prevSide != null && !File.Exists(SideFilePath(prevSide))) Write(SideFilePath(prevSide), previous);

                string side = NormalizeSide(data.PierSide);
                if (side != null) Write(SideFilePath(side), data);
                Write(FilePath, data);
            } catch (Exception ex) {
                Logger.Error(ex);
            }
        }

        public static void Clear() {
            try {
                if (Directory.Exists(Dir)) {
                    // Seules les calibrations de la caméra de guidage actuelle sont effacées
                    foreach (var f in Directory.GetFiles(Dir, Stem + ".json")) File.Delete(f);
                    foreach (var f in Directory.GetFiles(Dir, cameraTag == null ? "calibration_*.json" : $"calibration--{cameraTag}--*.json")) File.Delete(f);
                }
            } catch (Exception ex) { Logger.Error(ex); }
        }

        /// <summary>Dernière calibration enregistrée.</summary>
        public static CalibrationData Load() {
            try {
                return File.Exists(FilePath) ? JsonSerializer.Deserialize<CalibrationData>(File.ReadAllText(FilePath)) : null;
            } catch (Exception ex) {
                Logger.Error(ex);
                return null;
            }
        }

        /// <summary>Calibration faite pour ce côté du pier (null s'il n'y en a pas).</summary>
        public static CalibrationData LoadFor(string pierSide) {
            string side = NormalizeSide(pierSide);
            if (side == null) return Load();
            try {
                if (File.Exists(SideFilePath(side))) return JsonSerializer.Deserialize<CalibrationData>(File.ReadAllText(SideFilePath(side)));
                var last = Load();
                return last != null && NormalizeSide(last.PierSide) == side ? last : null;
            } catch (Exception ex) {
                Logger.Error(ex);
                return null;
            }
        }
    }

    /// <summary>
    /// Calibration à la manière de PHD2 : l'étoile est suivie image après image pendant que la monture reçoit des
    /// impulsions vers l'ouest (puis retour vers l'est), puis vers le nord (puis retour vers le sud).
    /// La première impulsion (1 s) sert à estimer la vitesse ; les pas suivants sont dimensionnés pour environ 6 pixels.
    /// Pour la déclinaison, on commence par rattraper le jeu des engrenages (impulsions jusqu'à ce que l'étoile bouge).
    /// À faire monture en suivi sidéral, de préférence près du méridien et de l'équateur céleste.
    /// </summary>
    public class CalibrationEngine {
        private const double TargetDistancePx = 20;
        private const double StepDistancePx = 6;
        private const double MinProbePx = 0.4;
        private const double BacklashClearPx = 1.5;
        private const double MaxSearchPx = 20;
        private const double MinEdgePx = 12;
        private const double SelectionEdgePx = 40;
        private const int ProbeMs = 1000;
        private const int MinStepMs = 200;
        private const int MaxStepMs = 4000;
        private const int MaxSteps = 12;
        private const int MinSteps = 3;
        private const int MaxClearSteps = 8;
        private const int SettleMs = 300;

        private int fixedStepMs;   // 0 = pas automatique
        private readonly Func<CancellationToken, Task<DetectionResult>> capture;
        private readonly IGuideMount mount;
        private readonly Action<string> report;
        /// <summary>Messages de diagnostic (journal des calibrations), facultatif.</summary>
        public Action<string> Log { get; set; }
        private readonly Action<DetectionResult> onFrame;

        public CalibrationEngine(Func<CancellationToken, Task<DetectionResult>> capture, IGuideMount mount,
                                 Action<string> report, Action<DetectionResult> onFrame) {
            this.capture = capture;
            this.mount = mount;
            this.report = report;
            this.onFrame = onFrame;
        }

        private class AxisResult {
            public double VX;
            public double VY;
            public double Angle;
            public double Rate;
            public double Rms;
            public double ReturnResidual;
            public int StepMs;
            public StarInfo End;
            public double BackRate;
            public double OutLostMs;
            public double BackLostMs;
            public List<CalPoint> Points = new List<CalPoint>();
            public int Steps;
        }

        public async Task<CalibrationData> RunAsync(double pixelScale, double focalMm, bool simulated, CancellationToken ct) {
            fixedStepMs = GuiderSettings.CalibrationStepMs;
            report(fixedStepMs > 0
                ? $"recherche de l'étoile de guidage (pas de calibration imposé : {fixedStepMs} ms)…"
                : "recherche de l'étoile de guidage…");
            var first = await capture(ct).ConfigureAwait(false);
            var candidates = first.Stars
                .Where(s => s.X > SelectionEdgePx && s.Y > SelectionEdgePx &&
                            s.X < first.Width - SelectionEdgePx && s.Y < first.Height - SelectionEdgePx)
                .ToList();
            var start = StarSelection.Pick(candidates);
            if (start == null) {
                throw new CalibrationException("aucune étoile de guidage utilisable (assez brillante, isolée, non saturée " +
                                               "et à plus de 40 pixels du bord) dans l'image.");
            }
            first.Selected = start;
            onFrame?.Invoke(first);

            double? declination = mount.DeclinationDegrees;
            string pier = mount.PierSide;

            var ra = await MeasureAxis("RA", GuideDir.West, GuideDir.East, fixedStepMs > 0 ? fixedStepMs : ProbeMs, start, false, ct).ConfigureAwait(false);
            var dec = await MeasureAxis("Dec", GuideDir.North, GuideDir.South, ra.StepMs, ra.End, true, ct).ConfigureAwait(false);

            double ortho = dec.Angle - ra.Angle;
            while (ortho > 180) ortho -= 360;
            while (ortho <= -180) ortho += 360;

            var warnings = new List<string>();
            double skew = Math.Abs(Math.Abs(ortho) - 90);
            if (skew > 30) {
                throw new CalibrationException($"les deux axes ne sont pas perpendiculaires (écart de {skew:0}° à 90°) : " +
                                               "mesure non fiable (nuages, mauvaise étoile, vent ?).");
            }
            if (skew > 10) warnings.Add($"axes à {skew:0}° de la perpendiculaire");
            if (ra.Rms > 1.5 || dec.Rms > 1.5) warnings.Add("déplacement peu linéaire (turbulence ou jeu mécanique)");
            if (ra.ReturnResidual > 3 || dec.ReturnResidual > 3) warnings.Add("mauvais retour au point de départ (jeu ou dérive)");

            return new CalibrationData {
                RaAngleDeg = ra.Angle,
                DecAngleDeg = dec.Angle,
                RaRatePxPerMs = ra.Rate,
                DecRatePxPerMs = dec.Rate,
                OrthogonalityDeg = ortho,
                RaPoints = ra.Points,
                DecPoints = dec.Points,
                RaSteps = ra.Steps,
                DecSteps = dec.Steps,
                StepMs = ra.StepMs,
                RaReturnRatePxPerMs = ra.BackRate,
                DecReturnRatePxPerMs = dec.BackRate,
                RaOutLostMs = ra.OutLostMs,
                RaBackLostMs = ra.BackLostMs,
                DecOutLostMs = dec.OutLostMs,
                DecBackLostMs = dec.BackLostMs,
                RaReturnResidualPx = ra.ReturnResidual,
                DecReturnResidualPx = dec.ReturnResidual,
                PixelScale = pixelScale,
                FocalLengthMm = focalMm,
                DeclinationDeg = declination,
                PierSide = pier,
                Simulated = simulated,
                Date = DateTime.Now,
                Warnings = string.Join(", ", warnings)
            };
        }

        private class ProbeResult {
            public int Sign;
            public double MeasuredDeg;
            public StarInfo End;
        }

        private static double NormAngle(double a) {
            while (a > 180) a -= 360;
            while (a <= -180) a += 360;
            return a;
        }

        /// <summary>
        /// Après un retournement au méridien : l'image tourne de 180° et le pilote de la monture peut ou non inverser le sens
        /// des impulsions en Dec. On reprend donc la calibration de l'autre côté du pier et on vérifie, avec une impulsion
        /// par axe, si chaque axe va dans le même sens ou dans le sens opposé. Renvoie null si la vérification est impossible
        /// (étoile immobile, direction qui ne correspond ni à l'un ni à l'autre) : une calibration complète est alors nécessaire.
        /// </summary>
        public async Task<CalibrationData> DeriveForFlipAsync(CalibrationData src, string newSide, CancellationToken ct) {
            report("vérification rapide de la calibration…");
            var first = await capture(ct).ConfigureAwait(false);
            var candidates = first.Stars
                .Where(s => s.X > SelectionEdgePx && s.Y > SelectionEdgePx &&
                            s.X < first.Width - SelectionEdgePx && s.Y < first.Height - SelectionEdgePx)
                .ToList();
            var start = StarSelection.Pick(candidates);
            if (start == null) throw new CalibrationException("aucune étoile de guidage utilisable dans l'image.");
            first.Selected = start;
            onFrame?.Invoke(first);

            var ra = await ProbeSign("RA", GuideDir.West, GuideDir.East, src.RaRatePxPerMs, src.RaAngleDeg, start, ct).ConfigureAwait(false);
            if (ra == null) return null;
            var dec = await ProbeSign("Dec", GuideDir.North, GuideDir.South, src.DecRatePxPerMs, src.DecAngleDeg, ra.End, ct).ConfigureAwait(false);
            if (dec == null) return null;

            var d = new CalibrationData {
                RaAngleDeg = ra.MeasuredDeg,   // angle réellement mesuré par l'impulsion de vérification
                DecAngleDeg = dec.MeasuredDeg,
                RaRatePxPerMs = src.RaRatePxPerMs,
                DecRatePxPerMs = src.DecRatePxPerMs,
                PixelScale = src.PixelScale,
                FocalLengthMm = src.FocalLengthMm,
                DeclinationDeg = src.DeclinationDeg,   // la vitesse RA se réfère à la déclinaison de la calibration d'origine
                PierSide = newSide,
                Simulated = false,
                Derived = true,
                Date = DateTime.Now,
                Warnings = $"calibration déduite de celle du côté {src.PierSide} après retournement " +
                           $"(RA {(ra.Sign > 0 ? "même sens" : "inversé")}, Dec {(dec.Sign > 0 ? "même sens" : "inversé")})"
            };
            d.OrthogonalityDeg = NormAngle(d.DecAngleDeg - d.RaAngleDeg);
            return d;
        }

        /// <summary>Envoie quelques impulsions sur un axe et compare le déplacement à celui prévu (même sens ou sens opposé).</summary>
        private async Task<ProbeResult> ProbeSign(string name, GuideDir outDir, GuideDir backDir, double rate, double angleDeg,
                                                  StarInfo origin, CancellationToken ct) {
            if (rate <= 0) return null;
            int ms = (int)(Math.Round(Math.Min(MaxStepMs, Math.Max(500, 8.0 / rate)) / 50.0) * 50);
            double ux = Math.Cos(angleDeg * Math.PI / 180.0);
            double uy = Math.Sin(angleDeg * Math.PI / 180.0);

            var cur = origin;
            int tries = 0;
            double dx = 0, dy = 0, dist = 0;
            while (tries < 4) {
                tries++;
                report($"{name} : impulsion de vérification {tries}…");
                await Pulse(outDir, ms, ct).ConfigureAwait(false);
                cur = await Grab(cur, ct).ConfigureAwait(false);
                dx = cur.X - origin.X;
                dy = cur.Y - origin.Y;
                dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist >= 4) break;
            }

            int sign = 0;
            double measured = angleDeg;
            string verdict;
            if (dist >= 2) {
                double dot = (dx * ux + dy * uy) / dist;
                measured = Math.Atan2(dy, dx) * 180.0 / Math.PI;
                if (Math.Abs(dot) >= 0.5) { sign = dot > 0 ? 1 : -1; verdict = sign > 0 ? "même sens" : "sens inversé"; }   // sinon : ni le sens prévu ni son opposé
                else verdict = "direction inattendue";
                Log?.Invoke($"vérification {name} : {tries} impulsion(s) de {ms} ms, déplacement {dist:0.0} px, direction mesurée {measured:0}° (prévue {angleDeg:0}°, cos {dot:0.00}) → {verdict}");
            } else {
                Log?.Invoke($"vérification {name} : {tries} impulsion(s) de {ms} ms, déplacement {dist:0.0} px seulement → étoile quasi immobile (jeu mécanique ?)");
            }

            for (int i = 0; i < tries; i++) {
                report($"{name} : retour de vérification {i + 1}/{tries}…");
                await Pulse(backDir, ms, ct).ConfigureAwait(false);
                cur = await Grab(cur, ct).ConfigureAwait(false);
            }
            return sign == 0 ? null : new ProbeResult { Sign = sign, End = cur, MeasuredDeg = measured };
        }

        private async Task<AxisResult> MeasureAxis(string name, GuideDir outDir, GuideDir backDir, int firstStepMs,
                                                   StarInfo origin, bool clearBacklash, CancellationToken ct) {
            var cur = origin;
            int stepMs = firstStepMs;

            // Rattrapage du jeu : on pousse jusqu'à ce que l'étoile bouge vraiment
            int cleared = 0;
            if (clearBacklash) {
                while (true) {
                    report($"{name} : rattrapage du jeu ({cleared + 1})…");
                    await Pulse(outDir, stepMs, ct).ConfigureAwait(false);
                    cur = await Grab(cur, ct).ConfigureAwait(false);
                    cleared++;
                    if (Dist(cur, origin) >= BacklashClearPx) break;
                    if (cleared >= (fixedStepMs > 0 ? 4 * MaxClearSteps : MaxClearSteps)) {
                        throw new CalibrationException($"{name} : l'étoile ne bouge pas, la monture ne répond pas aux impulsions.");
                    }
                }
            }

            var start = cur;
            var times = new List<double> { 0 };
            var xs = new List<double> { 0 };
            var ys = new List<double> { 0 };
            var steps = new List<int>();
            var points = new List<CalPoint> { new CalPoint { X = 0, Y = 0, Back = false } };
            double t = 0;

            int maxSteps = fixedStepMs > 0 ? 4 * MaxSteps : MaxSteps;
            for (int i = 0; i < maxSteps; i++) {
                report($"{name} : pas {i + 1}…");
                await Pulse(outDir, stepMs, ct).ConfigureAwait(false);
                cur = await Grab(cur, ct).ConfigureAwait(false);
                t += stepMs;
                steps.Add(stepMs);
                times.Add(t);
                xs.Add(cur.X - start.X);
                ys.Add(cur.Y - start.Y);
                points.Add(new CalPoint { X = cur.X - start.X, Y = cur.Y - start.Y, Back = false });
                double dist = Dist(cur, start);

                if (i == 0 && fixedStepMs == 0) {
                    if (dist < MinProbePx) {
                        throw new CalibrationException($"{name} : l'étoile n'a pas bougé après une impulsion de {stepMs} ms. " +
                            "Vérifiez que la monture est connectée, en suivi, qu'elle accepte le « pulse guide » et que la vitesse de guidage n'est pas nulle.");
                    }
                    double rate = dist / stepMs;
                    stepMs = (int)Math.Round(Math.Min(MaxStepMs, Math.Max(MinStepMs, StepDistancePx / rate)) / 50.0) * 50;
                }
                if (steps.Count >= MinSteps && dist >= TargetDistancePx) break;
            }

            if (fixedStepMs > 0 && Dist(cur, start) < 1.0) {
                throw new CalibrationException($"{name} : l'étoile a bougé de moins d'un pixel après {steps.Count} impulsions de {stepMs} ms : " +
                    "pas de calibration trop court (augmentez-le) ou monture sans réponse (vérifiez le « pulse guide »).");
            }

            // Ajustement par les moindres carrés d'un déplacement linéaire en fonction du temps cumulé de pulsation (départ fixé)
            double stt = 0, stx = 0, sty = 0;
            for (int i = 1; i < times.Count; i++) {
                stt += times[i] * times[i];
                stx += times[i] * xs[i];
                sty += times[i] * ys[i];
            }
            double vx = stx / stt;
            double vy = sty / stt;
            double sq = 0;
            for (int i = 1; i < times.Count; i++) {
                double ex = xs[i] - vx * times[i];
                double ey = ys[i] - vy * times[i];
                sq += ex * ex + ey * ey;
            }
            double rms = Math.Sqrt(sq / (times.Count - 1));

            // Diagnostic : avancement projeté sur la direction de l'aller, avec ordonnée à l'origine (temps perdu au départ)
            double vlen = Math.Sqrt(vx * vx + vy * vy);
            double ux = vx / vlen;
            double uy = vy / vlen;
            var outProgress = new List<double>();
            for (int i = 0; i < times.Count; i++) outProgress.Add(xs[i] * ux + ys[i] * uy);
            FitLine(times, outProgress, out double outSlope, out double outIntercept);

            // Retour : mêmes impulsions en sens inverse
            var endPos = cur;
            var backTimes = new List<double> { 0 };
            var backProgress = new List<double> { 0 };
            double tb = 0;
            for (int i = 0; i < steps.Count; i++) {
                report($"{name} : retour {i + 1}/{steps.Count}…");
                await Pulse(backDir, steps[i], ct).ConfigureAwait(false);
                cur = await Grab(cur, ct).ConfigureAwait(false);
                tb += steps[i];
                backTimes.Add(tb);
                backProgress.Add((endPos.X - cur.X) * ux + (endPos.Y - cur.Y) * uy);
                points.Add(new CalPoint { X = cur.X - start.X, Y = cur.Y - start.Y, Back = true });
            }
            FitLine(backTimes, backProgress, out double backSlope, out double backIntercept);
            for (int i = 0; i < cleared; i++) {
                report($"{name} : retour du rattrapage {i + 1}/{cleared}…");
                await Pulse(backDir, firstStepMs, ct).ConfigureAwait(false);
                cur = await Grab(cur, ct).ConfigureAwait(false);
            }

            return new AxisResult {
                VX = vx,
                VY = vy,
                Angle = Math.Atan2(vy, vx) * 180.0 / Math.PI,
                Rate = Math.Sqrt(vx * vx + vy * vy),
                Rms = rms,
                ReturnResidual = Dist(cur, origin),
                StepMs = stepMs,
                Points = points,
                Steps = steps.Count,
                End = cur,
                BackRate = Math.Max(0, backSlope),
                OutLostMs = outSlope > 1e-9 ? Math.Max(0, -outIntercept / outSlope) : 0,
                BackLostMs = backSlope > 1e-9 ? Math.Max(0, -backIntercept / backSlope) : 0
            };
        }

        /// <summary>Droite y = pente x + ordonnée, par moindres carrés (au moins 2 points).</summary>
        private static void FitLine(IList<double> x, IList<double> y, out double slope, out double intercept) {
            int n = x.Count;
            double sx = 0, sy = 0, sxx = 0, sxy = 0;
            for (int i = 0; i < n; i++) { sx += x[i]; sy += y[i]; sxx += x[i] * x[i]; sxy += x[i] * y[i]; }
            double den = n * sxx - sx * sx;
            if (n < 2 || Math.Abs(den) < 1e-12) { slope = 0; intercept = 0; return; }
            slope = (n * sxy - sx * sy) / den;
            intercept = (sy - slope * sx) / n;
        }

        private async Task Pulse(GuideDir direction, int milliseconds, CancellationToken ct) {
            await mount.PulseAsync(direction, milliseconds, ct).ConfigureAwait(false);
            await Task.Delay(SettleMs, ct).ConfigureAwait(false);
        }

        /// <summary>Prend une image et retrouve l'étoile suivie (la plus proche de sa dernière position).</summary>
        private async Task<StarInfo> Grab(StarInfo previous, CancellationToken ct) {
            var r = await capture(ct).ConfigureAwait(false);
            StarInfo best = null;
            double bestDist = MaxSearchPx;
            foreach (var s in r.Stars) {
                double d = Dist(s, previous);
                if (d <= bestDist) { best = s; bestDist = d; }
            }
            if (best == null) {
                throw new CalibrationException("l'étoile de guidage a été perdue (déplacement trop grand, nuage ou sortie du champ).");
            }
            r.Selected = best;
            onFrame?.Invoke(r);

            if (best.X < MinEdgePx || best.Y < MinEdgePx || best.X > r.Width - MinEdgePx || best.Y > r.Height - MinEdgePx) {
                throw new CalibrationException("l'étoile de guidage est arrivée trop près du bord de l'image.");
            }
            return best;
        }

        private static double Dist(StarInfo a, StarInfo b) {
            double dx = a.X - b.X;
            double dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }
}
