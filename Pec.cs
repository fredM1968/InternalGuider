using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using NINA.Core.Utility;

namespace NINA.Plugin.InternalGuider {
    /// <summary>
    /// Courbe d'erreur périodique RA (PEC logiciel). Elle est exprimée en secondes d'arc (indépendante de la focale et de la caméra),
    /// moyenne nulle, sous forme de quelques harmoniques de la période de la vis ou de l'engrenage.
    /// Valeur positive = l'étoile est décalée dans le sens d'une impulsion Ouest (même convention que l'erreur RA du guidage).
    /// </summary>
    public class PecCurve {
        public double PeriodSec { get; set; }
        public double[] Sin { get; set; } = new double[0];
        public double[] Cos { get; set; } = new double[0];
        /// <summary>Demi-amplitude crête à crête (secondes d'arc).</summary>
        public double AmplitudeArcsec { get; set; }
        /// <summary>Écart-type de ce qui n'est pas expliqué par la courbe (secondes d'arc).</summary>
        public double ResidualRmsArcsec { get; set; }
        public double DurationSec { get; set; }
        public double DeclinationDeg { get; set; }
        public bool HasDeclination { get; set; }
        public string Recorded { get; set; }

        /// <summary>Valeur de la courbe (secondes d'arc) à l'instant t (secondes) : périodique, de période PeriodSec.</summary>
        public double Value(double t) {
            if (PeriodSec <= 0) return 0;
            double w = 2 * Math.PI * (t - PeriodSec * Math.Floor(t / PeriodSec)) / PeriodSec;
            double v = 0;
            for (int k = 0; k < Sin.Length; k++) v += Sin[k] * Math.Sin((k + 1) * w) + Cos[k] * Math.Cos((k + 1) * w);
            return v;
        }

        public string Describe() {
            return $"période {PeriodSec:0.#} s · amplitude ±{AmplitudeArcsec:0.0}″ · enregistrée le {Recorded} sur {DurationSec / 60:0} min";
        }
    }

    /// <summary>Mémorisation de la courbe PEC dans %LOCALAPPDATA%\NINA\InternalGuider\pec.json.</summary>
    public static class PecStore {
        private static readonly string Folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NINA", "InternalGuider");

        /// <summary>Vrai en mode Simulation : la courbe est alors mémorisée à part (pec_simulation.json) et ne touche jamais la vraie courbe.</summary>
        public static volatile bool Simulation;

        private static string FilePath => Path.Combine(Folder, Simulation ? "pec_simulation.json" : "pec.json");

        public static PecCurve Load() {
            try {
                if (!File.Exists(FilePath)) return null;
                var c = JsonSerializer.Deserialize<PecCurve>(File.ReadAllText(FilePath));
                if (c == null || c.PeriodSec < 30 || c.Sin == null || c.Cos == null || c.Sin.Length == 0 || c.Sin.Length != c.Cos.Length) return null;
                return c;
            } catch (Exception ex) {
                Logger.Error(ex);
                return null;
            }
        }

        public static string CurrentPath => FilePath;

        public static bool Save(PecCurve c) {
            try {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, JsonSerializer.Serialize(c));
                return File.Exists(FilePath);
            } catch (Exception ex) {
                Logger.Error(ex);
                return false;
            }
        }

        public static void Delete() {
            try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch (Exception ex) { Logger.Error(ex); }
        }
    }

    /// <summary>Calculs de la PEC : ajustement d'une courbe périodique et recherche de la phase d'une courbe connue.</summary>
    public static class PecMath {
        public const int Harmonics = 3;
        public const double MinRecordSec = 600;
        /// <summary>Bornes de la recherche de la période (s) : une vis sans fin fait en général de 100 à 900 s par tour.</summary>
        public const double MinPeriodSec = 100;
        public const double MaxPeriodSec = 1500;

        /// <summary>Résout le système linéaire A x = b (élimination de Gauss avec pivot). Faux si singulier.</summary>
        private static bool Solve(double[,] a, double[] b, double[] x) {
            int n = b.Length;
            for (int i = 0; i < n; i++) {
                int p = i;
                for (int r = i + 1; r < n; r++) if (Math.Abs(a[r, i]) > Math.Abs(a[p, i])) p = r;
                if (Math.Abs(a[p, i]) < 1e-12) return false;
                if (p != i) {
                    for (int c = 0; c < n; c++) { double tmp = a[i, c]; a[i, c] = a[p, c]; a[p, c] = tmp; }
                    double tb = b[i]; b[i] = b[p]; b[p] = tb;
                }
                for (int r = i + 1; r < n; r++) {
                    double f = a[r, i] / a[i, i];
                    for (int c = i; c < n; c++) a[r, c] -= f * a[i, c];
                    b[r] -= f * b[i];
                }
            }
            for (int i = n - 1; i >= 0; i--) {
                double s = b[i];
                for (int c = i + 1; c < n; c++) s -= a[i, c] * x[c];
                x[i] = s / a[i, i];
            }
            return true;
        }

        /// <summary>Moindres carrés : y ≈ somme des colonnes de basis (fonction de l'indice d'échantillon) ; renvoie la somme des carrés des résidus.</summary>
        private static double LeastSquares(int n, int m, Func<int, double[]> basis, IList<double> y, double[] coef) {
            var ata = new double[m, m];
            var atb = new double[m];
            for (int i = 0; i < n; i++) {
                var f = basis(i);
                for (int r = 0; r < m; r++) {
                    atb[r] += f[r] * y[i];
                    for (int c = r; c < m; c++) ata[r, c] += f[r] * f[c];
                }
            }
            for (int r = 0; r < m; r++) for (int c = 0; c < r; c++) ata[r, c] = ata[c, r];
            if (!Solve(ata, atb, coef)) return double.MaxValue;
            double sse = 0;
            for (int i = 0; i < n; i++) {
                var f = basis(i);
                double e = y[i];
                for (int r = 0; r < m; r++) e -= coef[r] * f[r];
                sse += e * e;
            }
            return sse;
        }

        private static double Variance(IList<double> y) {
            double m = 0;
            foreach (var v in y) m += v;
            m /= y.Count;
            double s = 0;
            foreach (var v in y) s += (v - m) * (v - m);
            return s;
        }

        /// <summary>
        /// Ajuste une courbe périodique + dérive linéaire sur les mesures (t en secondes, u en secondes d'arc : position RA sans guidage).
        /// Cherche la période dans [minPeriod ; maxPeriod]. Renvoie null (et un message) si aucune erreur périodique nette n'est trouvée.
        /// </summary>
        public static PecCurve Fit(IList<double> t, IList<double> u, double minPeriod, double maxPeriod, out string message) {
            message = null;
            int n = t.Count;
            if (n < 60) { message = "trop peu de mesures"; return null; }
            double t0 = (t[0] + t[n - 1]) / 2;
            var tc = new double[n];
            for (int i = 0; i < n; i++) tc[i] = t[i] - t0;
            double duration = t[n - 1] - t[0];
            maxPeriod = Math.Min(maxPeriod, duration / 2.2);
            if (maxPeriod <= minPeriod) { message = $"enregistrement trop court pour trouver la période ({duration / 60:0} min) : il faut au moins 2,2 fois la période de la monture, par exemple 32 min pour une période de 860 s. Enregistrement inchangé"; return null; }

            int m = 2 + 2 * Harmonics;
            double bestSse = double.MaxValue, bestT = 0;
            var coef = new double[m];
            Func<double, Func<int, double[]>> mk = period => {
                double w = 2 * Math.PI / period;
                return i => {
                    var f = new double[m];
                    f[0] = 1;
                    f[1] = tc[i] / 1000.0;
                    for (int k = 1; k <= Harmonics; k++) { f[2 * k] = Math.Sin(k * w * tc[i]); f[2 * k + 1] = Math.Cos(k * w * tc[i]); }
                    return f;
                };
            };
            for (double p = minPeriod; p <= maxPeriod; p += 1.0) {
                double sse = LeastSquares(n, m, mk(p), u, coef);
                if (sse < bestSse) { bestSse = sse; bestT = p; }
            }
            if (bestT <= 0) { message = "ajustement impossible"; return null; }
            double lo = Math.Max(minPeriod, bestT - 1.0), hi = Math.Min(maxPeriod, bestT + 1.0);
            for (double p = lo; p <= hi; p += 0.05) {
                double sse = LeastSquares(n, m, mk(p), u, coef);
                if (sse < bestSse) { bestSse = sse; bestT = p; }
            }
            LeastSquares(n, m, mk(bestT), u, coef);

            // Part de la variance expliquée, une fois la dérive linéaire retirée
            var linCoef = new double[2];
            double sseLin = LeastSquares(n, 2, i => new[] { 1.0, tc[i] / 1000.0 }, u, linCoef);
            double explained = sseLin > 0 ? 1 - bestSse / sseLin : 0;

            // Construction de la courbe, exprimée en fonction du temps absolu t (et non de tc) : on recale la phase
            double wb = 2 * Math.PI / bestT;
            var sinA = new double[Harmonics];
            var cosA = new double[Harmonics];
            for (int k = 1; k <= Harmonics; k++) {
                double s = coef[2 * k], c = coef[2 * k + 1];
                // sin(kw(t - t0)) = sin(kwt)cos(kwt0) - cos(kwt)sin(kwt0) ; cos(kw(t - t0)) = cos(kwt)cos(kwt0) + sin(kwt)sin(kwt0)
                double a = k * wb * t0;
                sinA[k - 1] = s * Math.Cos(a) + c * Math.Sin(a);
                cosA[k - 1] = -s * Math.Sin(a) + c * Math.Cos(a);
            }
            var curve = new PecCurve {
                PeriodSec = bestT, Sin = sinA, Cos = cosA, DurationSec = duration,
                ResidualRmsArcsec = Math.Sqrt(bestSse / n),
                Recorded = DateTime.Now.ToString("yyyy-MM-dd HH:mm")
            };
            double mn = double.MaxValue, mx = double.MinValue;
            for (int i = 0; i < 400; i++) {
                double v = curve.Value(bestT * i / 400.0);
                if (v < mn) mn = v;
                if (v > mx) mx = v;
            }
            curve.AmplitudeArcsec = (mx - mn) / 2;
            if (explained < 0.4) {
                message = $"aucune erreur périodique nette (part expliquée {explained * 100:0} %, amplitude {curve.AmplitudeArcsec:0.0}″) : enregistrement inchangé";
                return null;
            }
            if (curve.AmplitudeArcsec < 0.5) {
                message = $"erreur périodique négligeable (±{curve.AmplitudeArcsec:0.0}″) : enregistrement inchangé";
                return null;
            }
            message = $"courbe enregistrée : {curve.Describe()} · part de variance expliquée {explained * 100:0} %";
            return curve;
        }

        /// <summary>Part de la période à partir de laquelle on tente un calage rapide de la phase (gain fixé à 1).</summary>
        public const double FastSyncSpan = 0.35;
        /// <summary>Accord exigé pour le calage rapide (plus sévère que 0,5 puisque peu de données).</summary>
        public const double FastSyncQuality = 0.7;

        /// <summary>
        /// Calage rapide : la forme et l'amplitude de la courbe sont connues, on ne cherche que la phase (gain imposé à 1)
        /// avec un décalage et une pente. Utilisable dès un tiers de période de mesures.
        /// </summary>
        private static bool SyncFast(PecCurve curve, IList<double> t, IList<double> u, out double phase, out double gain, out double quality) {
            phase = 0; gain = 1; quality = 0;
            int n = t.Count;
            double t0 = t[0];
            var tc = new double[n];
            for (int i = 0; i < n; i++) tc[i] = t[i] - t0;
            var lin = new double[2];
            double sseLin = LeastSquares(n, 2, i => new[] { 1.0, tc[i] / 1000.0 }, u, lin);
            if (sseLin <= 0) return false;
            double best = double.MaxValue, bestPh = 0;
            var coef = new double[2];
            var y = new double[n];
            Func<double, double> eval = ph => {
                for (int i = 0; i < n; i++) y[i] = u[i] - curve.Value(t[i] + ph);
                return LeastSquares(n, 2, i => new[] { 1.0, tc[i] / 1000.0 }, y, coef);
            };
            int steps = 240;
            for (int k = 0; k < steps; k++) {
                double ph = curve.PeriodSec * k / steps;
                double e = eval(ph);
                if (e < best) { best = e; bestPh = ph; }
            }
            double fine = curve.PeriodSec / steps, refined = bestPh;
            for (int k = -10; k <= 10; k++) {
                double ph = bestPh + fine * k / 10.0;
                double e = eval(ph);
                if (e < best) { best = e; refined = ph; }
            }
            phase = refined - curve.PeriodSec * Math.Floor(refined / curve.PeriodSec);
            quality = 1 - best / sseLin;
            return quality >= FastSyncQuality;
        }

        /// <summary>
        /// Cherche où l'on se trouve dans la période : ajuste u(t) ≈ gain x courbe(t + phase) + constante + pente x t.
        /// Renvoie vrai si l'accord est net (gain entre 0,5 et 1,5 et au moins la moitié de la variance expliquée).
        /// </summary>
        public static bool Sync(PecCurve curve, IList<double> t, IList<double> u, out double phase, out double gain, out double quality) {
            phase = 0; gain = 1; quality = 0;
            int n = t.Count;
            if (n < 40) return false;
            double span = t[n - 1] - t[0];
            if (span < PecMath.FastSyncSpan * curve.PeriodSec) return false;
            double t0 = t[0];
            if (span < 0.75 * curve.PeriodSec) return SyncFast(curve, t, u, out phase, out gain, out quality);
            var tc = new double[n];
            for (int i = 0; i < n; i++) tc[i] = t[i] - t0;
            // Au-delà de 1,25 période, on autorise une dérive qui s'infléchit (terme quadratique) : la dérive lente non périodique
            // (refroidissement, flexion, mise en station) fausserait sinon l'ajustement et ferait chuter l'accord.
            bool quad = span >= 1.25 * curve.PeriodSec;
            int mb = quad ? 4 : 3;
            var coef = new double[mb];
            var linCoef = new double[quad ? 3 : 2];
            double sseLin = quad
                ? LeastSquares(n, 3, i => new[] { 1.0, tc[i] / 1000.0, tc[i] * tc[i] / 1e6 }, u, linCoef)
                : LeastSquares(n, 2, i => new[] { 1.0, tc[i] / 1000.0 }, u, linCoef);
            if (sseLin <= 0) return false;

            double best = double.MaxValue, bestPhase = 0;
            Func<double, double> eval = ph => {
                var cv = new double[n];
                for (int i = 0; i < n; i++) cv[i] = curve.Value(t[i] + ph);
                double e = quad
                    ? LeastSquares(n, 4, i => new[] { cv[i], 1.0, tc[i] / 1000.0, tc[i] * tc[i] / 1e6 }, u, coef)
                    : LeastSquares(n, 3, i => new[] { cv[i], 1.0, tc[i] / 1000.0 }, u, coef);
                // Gain négatif = courbe à l'envers. Pour une courbe presque sinusoïdale, la phase décalée d'une demi-période donne le même
                // accord avec un gain opposé : on ne retient que les phases à gain positif, sinon on choisirait parfois la mauvaise.
                return coef[0] > 0 ? e : double.MaxValue;
            };
            int steps = 120;
            for (int s = 0; s < steps; s++) {
                double ph = curve.PeriodSec * s / steps;
                double sse = eval(ph);
                if (sse < best) { best = sse; bestPhase = ph; }
            }
            double fine = curve.PeriodSec / steps;
            double refineBest = bestPhase;
            for (int s = -10; s <= 10; s++) {
                double ph = bestPhase + fine * s / 10.0;
                double sse = eval(ph);
                if (sse < best) { best = sse; refineBest = ph; }
            }
            if (best == double.MaxValue) return false;   // aucune phase à gain positif
            eval(refineBest);
            phase = refineBest - curve.PeriodSec * Math.Floor(refineBest / curve.PeriodSec);
            gain = coef[0];
            quality = 1 - best / sseLin;
            return gain >= 0.5 && gain <= 1.5 && quality >= 0.5;
        }
    }

    /// <summary>
    /// PEC en cours de guidage : reconstitue la position RA sans guidage (erreur mesurée + impulsions déjà envoyées),
    /// l'enregistre pour fabriquer la courbe, ou retrouve la phase d'une courbe connue pour envoyer des impulsions d'anticipation.
    /// </summary>
    /// <summary>
    /// Mémoire de la phase entre deux guidages successifs : tant que le suivi n'a pas été interrompu (ni pointage ni mise hors tension),
    /// la vis sans fin continue de tourner et la phase se déduit de l'heure. Valable quelques minutes seulement.
    /// </summary>
    public static class PecPhaseMemory {
        public static readonly object Lock = new object();
        public static string CurveId;          // identifie la courbe (période + date d'enregistrement)
        public static double PhaseAbs;         // phase rapportée à l'heure UTC absolue (s)
        public static double Gain = 1;
        public static DateTime LastValidUtc = DateTime.MinValue;
        public const double MaxGapSec = 600;   // 10 min

        public static string Id(PecCurve c) { return c.Recorded + "|" + c.PeriodSec.ToString("0.00"); }
        public static double Epoch(DateTime utc) { return (utc - new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds; }
    }

    public class PecRuntime {
        private const double SyncEverySec = 60;
        private const int MaxFailedSyncs = 12;   // 12 minutes sans ajustement valable avant d'abandonner la phase
        private readonly bool apply;
        private readonly bool record;
        private readonly double strength;
        private readonly double scale;     // secondes d'arc par pixel
        private readonly Action<string> log;
        private readonly PecCurve curve;
        private readonly List<double> ts = new List<double>();
        private readonly List<double> us = new List<double>();   // secondes d'arc
        private double cumulPx;      // somme des impulsions RA déjà envoyées (px, positif = Est)
        private bool synced;
        private bool inherited;      // phase reprise du guidage précédent, pas encore vérifiée
        private readonly DateTime startUtc = DateTime.UtcNow;
        private double phase;
        private double gain = 1;
        private double nextSyncAt = 0;
        private int failedSyncs;
        private double quality;
        private double declinationFactor = 1;
        private string refusal;

        /// <summary>Texte d'état affiché dans la fenêtre d'aperçu (vide si la PEC est inactive).</summary>
        public volatile string Status = "";
        /// <summary>Vrai quand l'enregistrement a duré assez longtemps pour être traité (la case d'enregistrement se décoche alors).</summary>
        public volatile bool RecordCompleted;

        public PecRuntime(bool apply, bool record, double strengthPercent, double arcsecPerPx, Action<string> log, double? declinationDeg) {
            this.apply = apply && arcsecPerPx > 0;
            this.record = record && arcsecPerPx > 0;
            strength = Math.Max(0.1, Math.Min(1.0, strengthPercent / 100.0));
            scale = arcsecPerPx;
            this.log = log ?? (s => { });
            if (apply && arcsecPerPx <= 0) this.log("PEC : échelle (arcsec/px) inconnue, PEC non appliquée");
            if (this.apply) {
                curve = PecStore.Load();
                if (curve == null) {
                    this.apply = false;
                    this.log("PEC : aucune courbe enregistrée, PEC non appliquée (activez l'enregistrement pour en créer une)");
                } else {
                    if (curve.HasDeclination && declinationDeg.HasValue) {
                        double cc = Math.Cos(curve.DeclinationDeg * Math.PI / 180.0), cn = Math.Cos(declinationDeg.Value * Math.PI / 180.0);
                        if (cc > 0.1 && cn > 0.1) declinationFactor = cn / cc;
                    }
                    this.log($"PEC : courbe chargée ({curve.Describe()}) · force {strength * 100:0} % · recherche de la phase pendant les {PecMath.FastSyncSpan * curve.PeriodSec / 60:0.#} premières minutes");
                    lock (PecPhaseMemory.Lock) {
                        double gap = (startUtc - PecPhaseMemory.LastValidUtc).TotalSeconds;
                        if (PecPhaseMemory.CurveId == PecPhaseMemory.Id(curve) && gap >= 0 && gap <= PecPhaseMemory.MaxGapSec) {
                            double P = curve.PeriodSec;
                            double ph = PecPhaseMemory.PhaseAbs + PecPhaseMemory.Epoch(startUtc);
                            phase = ph - P * Math.Floor(ph / P);
                            gain = PecPhaseMemory.Gain;
                            synced = true; inherited = true; quality = 0;
                            this.log($"PEC : phase reprise du guidage précédent (interruption de {gap / 60:0.#} min), à vérifier dès les premières mesures");
                        }
                    }
                }
            }
            if (this.record) this.log("PEC : enregistrement de la courbe pendant ce guidage (au moins 10 minutes)");
            declinationAtStart = declinationDeg;
            UpdateStatus(0);
        }

        private readonly double? declinationAtStart;

        /// <summary>Enregistre une mesure. aPx = erreur RA hors dither (px), t = temps depuis le début du guidage (s).</summary>
        public void AddSample(double t, double aPx) {
            if (!apply && !record) return;
            ts.Add(t);
            us.Add((aPx + cumulPx) * scale);
            if (!record && ts.Count > 5000) { ts.RemoveAt(0); us.RemoveAt(0); }
            if (apply && curve != null && t >= nextSyncAt && t - ts[0] >= PecMath.FastSyncSpan * curve.PeriodSec) {
                nextSyncAt = t + (synced ? SyncEverySec : 30);
                TrySync();
            }
            if (synced && !inherited && curve != null) {
                lock (PecPhaseMemory.Lock) {
                    PecPhaseMemory.CurveId = PecPhaseMemory.Id(curve);
                    PecPhaseMemory.PhaseAbs = phase - PecPhaseMemory.Epoch(startUtc);
                    PecPhaseMemory.Gain = gain;
                    PecPhaseMemory.LastValidUtc = DateTime.UtcNow;
                }
            }
            UpdateStatus(t);
        }

        /// <summary>À appeler après l'envoi d'une impulsion RA (ms signées, positif = Est) ; raRatePxPerMs = vitesse corrigée de la déclinaison.</summary>
        public void AddPulse(int pulseRaMs, double raRatePxPerMs) {
            cumulPx += pulseRaMs * raRatePxPerMs;
        }

        private void TrySync() {
            // Fenêtre : les deux dernières périodes au plus
            double tEnd = ts[ts.Count - 1];
            double from = tEnd - 2 * curve.PeriodSec;
            int start = 0;
            while (start < ts.Count - 1 && ts[start] < from) start++;
            var tt = ts.GetRange(start, ts.Count - start);
            var uu = us.GetRange(start, us.Count - start);
            if (PecMath.Sync(curve, tt, uu, out double ph, out double g, out double q)) {
                bool was = synced && !inherited;
                if (inherited) log($"PEC : phase vérifiée (accord {q * 100:0} %), écart avec la phase reprise {PhaseDiff(ph, phase):0.#} s");
                else if (!was) log($"PEC : phase trouvée (accord {q * 100:0} %, amplitude relative {g * 100:0} %), anticipation active");
                synced = true;
                inherited = false;
                phase = ph;
                gain = was ? 0.5 * gain + 0.5 * g : g;
                quality = q;
                failedSyncs = 0;
            } else if (synced) {
                // La phase de la vis sans fin est continue : tant que le guidage tourne, la dernière phase trouvée reste valable.
                // Un ajustement raté (excursion non périodique, étoile perdue un instant) ne doit donc pas la faire oublier.
                failedSyncs++;
                if (inherited && failedSyncs >= 3) { synced = false; inherited = false; failedSyncs = 0; log("PEC : la phase reprise du guidage précédent n'est pas confirmée, abandonnée"); return; }
                log($"PEC : nouvel ajustement non retenu (accord {q * 100:0} %, amplitude {g * 100:0} %), phase précédente conservée ({failedSyncs}/{MaxFailedSyncs})");
                if (failedSyncs >= MaxFailedSyncs) { synced = false; log($"PEC : phase perdue après {failedSyncs} échecs consécutifs, anticipation suspendue"); }
            } else {
                quality = q;
                bool fastMode = tt[tt.Count - 1] - tt[0] < 0.75 * curve.PeriodSec;
                double need = fastMode ? PecMath.FastSyncQuality : 0.5;
                refusal = q < need ? $"accord insuffisant ({q * 100:0} %, il en faut {need * 100:0})" : $"amplitude mesurée {g * 100:0} % de la courbe (il faut 50 à 150 %)";
                // Une ligne dans le journal à chaque tentative (une par minute) pour comprendre pourquoi la phase n'est pas trouvée
                log($"PEC : phase non retenue, {refusal}");
            }
        }

        private double PhaseDiff(double a, double b) {
            double P = curve.PeriodSec, d = (a - b) % P;
            if (d > P / 2) d -= P; else if (d < -P / 2) d += P;
            return d;
        }

        /// <summary>Impulsion d'anticipation (ms, positif = Est) pour compenser la dérive périodique prévue entre t et t + dt.</summary>
        public double FeedForwardMs(double t, double dtSec, double raRatePxPerMs) {
            if (!apply || !synced || curve == null || raRatePxPerMs <= 0) return 0;
            double dArc = curve.Value(t + dtSec + phase) - curve.Value(t + phase);
            double g = Math.Max(0.5, Math.Min(1.3, gain));
            double px = strength * g * declinationFactor * dArc / scale;
            return px / raRatePxPerMs;
        }

        private void UpdateStatus(double t) {
            if (record) {
                Status = t < PecMath.MinRecordSec ? $"PEC : enregistrement {t / 60:0}/{PecMath.MinRecordSec / 60:0} min" : $"PEC : enregistrement {t / 60:0} min";
            } else if (apply && curve != null) {
                if (synced && inherited) Status = "PEC active (phase reprise du guidage précédent, à vérifier)";
                else if (synced) Status = failedSyncs > 0 ? $"PEC active (phase conservée, {failedSyncs} ajustement(s) non retenu(s))" : $"PEC active (accord {quality * 100:0} %)";
                else if (ts.Count > 0 && ts[ts.Count - 1] - ts[0] >= PecMath.FastSyncSpan * curve.PeriodSec) Status = "PEC : phase introuvable" + (refusal != null ? " · " + refusal : "");
                else Status = "PEC : recherche de la phase";
            } else Status = "";
        }

        /// <summary>Fin du guidage : si l'enregistrement a duré assez longtemps, ajuste la courbe et la mémorise.</summary>
        public void Finish(double? declinationDeg) {
            if (!record) return;
            if (ts.Count < 60 || ts[ts.Count - 1] - ts[0] < PecMath.MinRecordSec) {
                log($"PEC : enregistrement trop court ({(ts.Count > 0 ? (ts[ts.Count - 1] - ts[0]) / 60 : 0):0.#} min, il en faut au moins {PecMath.MinRecordSec / 60:0}) : courbe inchangée");
                return;
            }
            var c = PecMath.Fit(ts, us, PecMath.MinPeriodSec, PecMath.MaxPeriodSec, out string msg);
            if (c != null) {
                var dec = declinationDeg ?? declinationAtStart;
                c.HasDeclination = dec.HasValue;
                c.DeclinationDeg = dec ?? 0;
                if (PecStore.Save(c)) {
                    RecordCompleted = true;   // la case d'enregistrement ne se décoche que si la courbe a bien été mémorisée
                    msg += " · mémorisée dans " + PecStore.CurrentPath;
                } else msg += " · ÉCHEC de l'écriture du fichier, voir le journal de NINA";
            } else msg += " (la case « enregistrer la courbe » reste cochée pour un nouvel essai)";
            log("PEC : " + msg);
        }
    }
}
