using System;
using System.Collections.Generic;
using System.Linq;

namespace NINA.Plugin.InternalGuider {
    /// <summary>Jeu de réglages que le mode IA peut ajuster (fractions : 0,3 = 30 %).</summary>
    public class TuneSet {
        public double AggrRa, AggrDec, HystRa, HystDec, MinMove, IntegDec;

        public TuneSet Clone() { return (TuneSet)MemberwiseClone(); }

        public static TuneSet From(GuideParameters p) {
            return new TuneSet {
                AggrRa = p.AggressivenessRa, AggrDec = p.AggressivenessDec, HystRa = p.HysteresisRa, HystDec = p.HysteresisDec,
                MinMove = p.MinMovePx, IntegDec = p.IntegralDec
            };
        }

        public void ApplyTo(GuideParameters p) {
            p.AggressivenessRa = (float)AggrRa;
            p.AggressivenessDec = (float)AggrDec;
            p.HysteresisRa = (float)HystRa;
            p.HysteresisDec = (float)HystDec;
            p.MinMovePx = (float)MinMove;
            p.IntegralDec = (float)IntegDec;
        }

        /// <summary>Vrai si les paramètres du guidage ne sont plus ceux de ce jeu (modifiés à la main par l'utilisateur).</summary>
        public bool Differs(GuideParameters p) {
            const double eps = 1e-4;
            return Math.Abs(p.AggressivenessRa - AggrRa) > eps || Math.Abs(p.AggressivenessDec - AggrDec) > eps ||
                   Math.Abs(p.HysteresisRa - HystRa) > eps || Math.Abs(p.HysteresisDec - HystDec) > eps ||
                   Math.Abs(p.MinMovePx - MinMove) > eps || Math.Abs(p.IntegralDec - IntegDec) > eps;
        }

        public override string ToString() {
            return $"agressivité RA {AggrRa * 100:0} %, Dec {AggrDec * 100:0} % · hystérésis RA {HystRa * 100:0} %, Dec {HystDec * 100:0} % · " +
                   $"mouvement minimal {MinMove:0.00} px · intégrale Dec {IntegDec * 100:0} %";
        }
    }

    /// <summary>
    /// Mode IA : règle automatiquement les paramètres de guidage.
    /// 1. Pendant la phase d'analyse, avec les réglages de l'utilisateur, il reconstitue le mouvement de l'étoile sans correction.
    /// 2. Il rejoue sa propre boucle de guidage sur cette série avec d'autres réglages (même ciel pour tous les essais) et retient les meilleurs.
    /// 3. Il les applique par petits pas, vérifie sur le ciel que l'erreur mesurée baisse comme prévu, et revient aux réglages précédents sinon.
    /// Les réglages validés sont conservés à la fin du guidage (ils deviennent ceux de l'utilisateur) ; un essai non validé est abandonné.
    /// Si un intervalle est donné, l'ajustement est refait périodiquement.
    /// </summary>
    public class AutoTuner {
        private enum Phase { Learn, Eval, Wait, Done }

        private const int MaxStages = 3;
        private const double MinPredictedGain = 0.04;   // gain simulé minimal pour tenter un essai
        private const double MinMeasuredGain = 0.03;    // gain mesuré minimal pour conserver l'essai
        private const int MinFrames = 120;
        private const int MinEvalFrames = 60;

        private readonly GuideParameters p;
        private readonly Action<string> log;
        private readonly Action onChanged;
        private readonly double learnSec, evalSec, retuneSec;
        private double waitStartSec;
        private readonly double decRateN, decRateS;

        private Phase phase = Phase.Learn;
        private readonly List<double> ts = new List<double>();
        private readonly List<double> uRa = new List<double>(), uDec = new List<double>(), ff = new List<double>();
        private readonly List<double> eRa = new List<double>(), eDec = new List<double>();
        private readonly List<double> cRaAt = new List<double>(), cDecAt = new List<double>();   // somme des impulsions déjà envoyées au début de chaque image
        private readonly List<bool> excl = new List<bool>();
        private double cRa, cDec, raRate;
        private double dataSec, phaseStartSec;
        private int phaseStartIdx;
        private int stage;
        private TuneSet baseline, candidate, applied;
        private double baselineMeasRms;
        private int framesSinceGuard;
        private int maxRaMs = 250, maxDecMs = 250;
        private bool guideDec = true;
        private int decMode;
        private TuneSet initial;
        private bool changed, suspended, guardNoted, cycleKept;
        private int syncFrames;
        private int idleCycles;
        private const int MaxIdleCycles = 2;   // nombre de cycles consécutifs sans amélioration avant d'arrêter l'ajustement périodique

        /// <summary>Texte d'état affiché dans la fenêtre d'aperçu.</summary>
        public volatile string Status = "";

        /// <summary>Derniers réglages validés par le mode IA (null si aucun essai n'a été conservé) : à garder pour le guidage suivant.</summary>
        public TuneSet Retained { get { return changed && !suspended ? baseline.Clone() : null; } }

        public AutoTuner(GuideParameters parameters, CalibrationData cal, int learnMinutes, int evalMinutes, int retuneMinutes, Action<string> log, Action onChanged) {
            p = parameters;
            this.onChanged = onChanged;
            this.log = log ?? (s => { });
            learnSec = Math.Max(5, Math.Min(60, learnMinutes)) * 60.0;
            evalSec = Math.Max(3, Math.Min(30, evalMinutes)) * 60.0;
            retuneSec = retuneMinutes <= 0 ? 0 : Math.Max(10, Math.Min(240, retuneMinutes)) * 60.0;
            decRateN = cal.DecRatePxPerMs;
            decRateS = cal.DecReturnRatePxPerMs > 0 ? Math.Max(0.25 * decRateN, Math.Min(decRateN, cal.DecReturnRatePxPerMs)) : decRateN;
            baseline = TuneSet.From(p);
            initial = baseline.Clone();
            applied = baseline;
            this.log($"IA : analyse du guidage pendant {learnSec / 60:0} min avec vos réglages ({baseline})");
            Status = "IA : analyse 0/" + (learnSec / 60).ToString("0") + " min";
        }

        /// <summary>Appelé à chaque image, après l'envoi des impulsions. aRaw/bRaw : erreurs hors dither (px) ; aErr/bErr : erreurs par rapport au point verrouillé (px).</summary>
        public void AddFrame(double t, double aRaw, double bRaw, double aErr, double bErr, int pulseRaMs, int pulseDecMs, double ffMs, double raRatePxPerMs, bool excluded) {
            if (phase == Phase.Done) return;
            if (applied != null && applied.Differs(p)) {
                log("IA : un réglage a été modifié à la main, le mode IA est suspendu pour ce guidage");
                phase = Phase.Done;
                suspended = true;
                Status = "IA : suspendue (réglage modifié)";
                return;
            }
            double dt = ts.Count == 0 ? 0 : Math.Max(0, Math.Min(5, t - ts[ts.Count - 1]));
            dataSec += dt;
            raRate = raRatePxPerMs;
            maxRaMs = p.MaxRaMs;
            maxDecMs = p.MaxDecMs;
            guideDec = p.GuideDec;
            decMode = p.DecModeApplied;
            ts.Add(t);
            cRaAt.Add(cRa);
            cDecAt.Add(cDec);
            uRa.Add(aRaw + cRa);
            uDec.Add(bRaw + cDec);
            ff.Add(ffMs);
            eRa.Add(aErr);
            eDec.Add(bErr);
            excl.Add(excluded);
            cRa += pulseRaMs * raRate;
            if (guideDec && pulseDecMs != 0) cDec += (pulseDecMs > 0 ? decRateS : decRateN) * pulseDecMs;

            double phaseSec = dataSec - phaseStartSec;
            if (phase == Phase.Learn) {
                Status = $"IA : analyse {phaseSec / 60:0}/{learnSec / 60:0} min";
                if (phaseSec >= learnSec) StartOptimization();
            } else if (phase == Phase.Wait) {
                double left = retuneSec - (dataSec - waitStartSec);
                Status = $"IA : {(changed ? "réglages optimisés" : "réglages conservés (aucun changement retenu)")} · prochain ajustement dans {Math.Ceiling(Math.Max(0, left) / 60):0} min";
                if (++syncFrames >= 25) { syncFrames = 0; NotifyChanged(); }   // réaffiche les réglages réellement en vigueur
                if (left <= 0) StartRetune();
            } else if (phase == Phase.Eval) {
                Status = $"IA : essai {stage}/{MaxStages} · {phaseSec / 60:0}/{evalSec / 60:0} min";
                framesSinceGuard++;
                if (framesSinceGuard >= 20) {
                    framesSinceGuard = 0;
                    GuardCheck();
                }
                if (phase == Phase.Eval && phaseSec >= evalSec) FinishEval();
            }
        }

        /// <summary>Fin du guidage : rappelle les réglages retenus (non enregistrés).</summary>
        public void Finish() {
            if (changed && !suspended) log("IA : réglages retenus, enregistrés pour le prochain guidage : " + baseline);
        }

        // ---------------------------------------------------------------------------------------------------------------------------

        /// <summary>Ajustement périodique : repart des dernières minutes de guidage déjà enregistrées (la reconstitution du mouvement de l'étoile ne dépend pas des réglages utilisés).</summary>
        private void StartRetune() {
            int n = ts.Count;
            if (n < 2) { waitStartSec = dataSec; return; }
            double lastT = ts[n - 1];
            int from = n - 1;
            while (from > 0 && ts[from - 1] >= lastT - learnSec) from--;
            phase = Phase.Learn;
            stage = 0;
            cycleKept = false;
            phaseStartIdx = from;
            phaseStartSec = dataSec;
            log($"IA : ajustement périodique (analyse des {(lastT - ts[from]) / 60:0} dernières minutes, réglages actuels : {TuneSet.From(p)})");
            StartOptimization();
        }

        private void StartOptimization() {
            int from = phaseStartIdx, to = ts.Count;
            if (to - from < MinFrames) { EndTuning("trop peu d'images pour analyser", false); return; }
            baseline = TuneSet.From(p);
            var series = BuildSeries(from, to);
            baselineMeasRms = MeasuredTotal(from, to, out int nUsed);
            if (nUsed < MinFrames / 2) { EndTuning("trop peu d'images utilisables (dither, retournement)", false); return; }
            var best = Optimize(series, baseline, out double predBase, out double predBest);
            double gain = predBase > 0 ? 1 - predBest / predBase : 0;
            log($"IA : analyse terminée · erreur mesurée {baselineMeasRms:0.00} px, bruit estimé RA {series.SigmaRa:0.00} / Dec {series.SigmaDec:0.00} px · gain simulé {gain * 100:0} % avec : {best}");
            if (gain < MinPredictedGain || stage >= MaxStages) {
                EndTuning(stage == 0 ? "vos réglages sont déjà proches de l'optimum, aucun changement" : "réglages optimisés", stage > 0);
                return;
            }
            candidate = best;
            candidate.ApplyTo(p);
            applied = candidate;
            NotifyChanged();
            stage++;
            phase = Phase.Eval;
            phaseStartSec = dataSec;
            phaseStartIdx = ts.Count;
            framesSinceGuard = 0;
            guardNoted = false;
            log($"IA : essai {stage}/{MaxStages} pendant {evalSec / 60:0} min : {candidate}");
            Status = $"IA : essai {stage}/{MaxStages}";
        }

        private void GuardCheck() {
            // Dégradation franche : retour immédiat aux réglages précédents, mais seulement si l'essai en est la cause.
            // Une perturbation extérieure (rafale, saut de Dec, impulsions au maximum) gonfle l'erreur quels que soient les réglages :
            // on rejoue donc les dernières images avec les anciens et les nouveaux réglages et on compare.
            int to = ts.Count, from = Math.Max(phaseStartIdx, to - 60);
            double recent = MeasuredTotal(from, to, out int n);
            if (n < 40 || baselineMeasRms <= 0 || recent <= 1.6 * baselineMeasRms) return;
            var series = BuildSeries(from, to);
            double simCand = Simulate(candidate, series, true), simBase = Simulate(baseline, series, true);
            if (simCand > 1.25 * simBase) {
                log($"IA : l'erreur a augmenté ({recent:0.00} px contre {baselineMeasRms:0.00} px) et l'essai est en cause (simulé {simCand:0.00} px contre {simBase:0.00} px avec les réglages précédents), retour aux réglages précédents");
                Revert("dégradation");
            } else if (!guardNoted) {
                guardNoted = true;
                log($"IA : l'erreur a augmenté ({recent:0.00} px contre {baselineMeasRms:0.00} px) mais les réglages précédents n'auraient pas fait mieux (simulé {simBase:0.00} px contre {simCand:0.00} px) : perturbation extérieure, l'essai continue");
            }
        }

        private void FinishEval() {
            int from = phaseStartIdx, to = ts.Count;
            double measured = MeasuredTotal(from, to, out int n);
            if (n < MinEvalFrames) { log("IA : trop peu d'images utilisables pendant l'essai, retour aux réglages précédents"); Revert("pas assez de données"); return; }
            var series = BuildSeries(from, to);
            double predCand = Simulate(candidate, series, true), predBase = Simulate(baseline, series, true);
            // Erreur vraie mesurée : on retire le bruit de mesure (qui n'est pas du mouvement de l'étoile)
            double trueMeas = TrueFromMeasured(from, to, series);
            // Cohérence du modèle jugée sur l'erreur MESURÉE (bruit compris) : quand l'erreur vraie devient faible devant le bruit,
            // la soustraire au bruit estimé donne un résultat très imprécis et fausserait le rapport.
            double simMeasured = Math.Sqrt(predCand * predCand + series.SigmaRa * series.SigmaRa + series.SigmaDec * series.SigmaDec);
            double modelRatio = simMeasured > 0 ? measured / simMeasured : 1;
            double gain = predBase > 0 ? 1 - trueMeas / predBase : 0;
            log($"IA : essai {stage} terminé · erreur vraie mesurée {trueMeas:0.00} px, simulée {predCand:0.00} px, avec les réglages précédents (simulée) {predBase:0.00} px · gain {gain * 100:0} %");
            if (modelRatio > 1.4 || modelRatio < 0.6) {
                log("IA : la simulation ne reproduit pas bien le guidage réel (" + measured.ToString("0.00") + " px mesuré contre " + simMeasured.ToString("0.00") + " px simulé, bruit compris), retour aux réglages précédents");
                Revert("modèle non fiable");
                return;
            }
            if (gain >= MinMeasuredGain) {
                baseline = candidate;
                changed = true;
                cycleKept = true;
                baselineMeasRms = measured;
                log($"IA : essai {stage} conservé (gain {gain * 100:0} %)");
                StartOptimization();
            } else {
                log($"IA : pas d'amélioration nette (gain {gain * 100:0} %), retour aux réglages précédents");
                Revert("pas d'amélioration");
            }
        }

        private void Revert(string why) {
            baseline.ApplyTo(p);
            applied = baseline;
            NotifyChanged();
            EndTuning(why, changed);
        }

        private void NotifyChanged() {
            try { onChanged?.Invoke(); } catch (Exception) { }
        }

        private void EndTuning(string why, bool optimised) {
            Status = optimised ? "IA : réglages optimisés" : "IA : réglages conservés";
            NotifyChanged();     // l'affichage reprend les réglages réellement appliqués
            idleCycles = cycleKept ? 0 : idleCycles + 1;
            if (retuneSec > 0 && idleCycles < MaxIdleCycles) {
                phase = Phase.Wait;
                waitStartSec = dataSec;
                log($"IA : {why} · réglages actuels : {TuneSet.From(p)} · prochain ajustement dans {retuneSec / 60:0} min");
            } else {
                phase = Phase.Done;
                if (retuneSec > 0) {
                    Status = "IA : réglages stabilisés";
                    log($"IA : {MaxIdleCycles} cycles de suite sans amélioration, réglages stabilisés : l'ajustement périodique s'arrête (il reprendra au prochain démarrage du guidage, par exemple après un retournement au méridien) · réglages actuels : {TuneSet.From(p)}");
                } else {
                    log("IA : terminé · " + why + " · réglages actuels : " + TuneSet.From(p));
                }
            }
        }

        // ---------------------------------------------------------------------------------------------------------------------------
        // Mesures sur les images réelles

        /// <summary>Erreur mesurée (RMS total, px) sur une plage d'images, hors dither et retournement.</summary>
        private double MeasuredTotal(int from, int to, out int used) {
            double sa = 0, sb = 0; used = 0;
            for (int i = from; i < to; i++) {
                if (excl[i]) continue;
                sa += eRa[i] * eRa[i];
                sb += eDec[i] * eDec[i];
                used++;
            }
            return used == 0 ? 0 : Math.Sqrt((sa + sb) / used);
        }

        private double TrueFromMeasured(int from, int to, Series s) {
            double sa = 0, sb = 0; int used = 0;
            for (int i = from; i < to; i++) {
                if (excl[i]) continue;
                sa += eRa[i] * eRa[i];
                sb += eDec[i] * eDec[i];
                used++;
            }
            if (used == 0) return 0;
            double ra2 = Math.Max(0, sa / used - s.SigmaRa * s.SigmaRa);
            double de2 = Math.Max(0, sb / used - s.SigmaDec * s.SigmaDec);
            return Math.Sqrt(ra2 + de2);
        }

        // ---------------------------------------------------------------------------------------------------------------------------
        // Rejeu de la boucle de guidage

        private class Series {
            public double[] URa, UDec, Ff;
            public double SigmaRa, SigmaDec;
            public int N;
            public double[][] NoiseRa, NoiseDec;   // un jeu de bruit par graine
        }

        private Series BuildSeries(int from, int to) {
            int n = to - from;
            var s = new Series { N = n, URa = new double[n], UDec = new double[n], Ff = new double[n] };
            // Le rejeu démarre avec l'erreur réelle du début de la plage : on retire les impulsions déjà envoyées avant elle
            double c0Ra = cRaAt[from], c0Dec = cDecAt[from];
            for (int i = 0; i < n; i++) { s.URa[i] = uRa[from + i] - c0Ra; s.UDec[i] = uDec[from + i] - c0Dec; s.Ff[i] = ff[from + i]; }
            s.SigmaRa = NoiseSigma(s.URa);
            s.SigmaDec = NoiseSigma(s.UDec);
            // Le mouvement reconstitué contient le bruit de mesure (seeing, centroïde) : sans lissage, le rejeu le prendrait pour un vrai
            // déplacement de l'étoile et surestimerait l'erreur vraie (plancher d'environ 0,3 px). Le vrai mouvement est lent (erreur
            // périodique, dérive) : une moyenne glissante centrée sur 7 images le conserve, le bruit synthétique est ajouté ensuite.
            s.URa = Smooth(s.URa, 3);
            s.UDec = Smooth(s.UDec, 3);
            const int seeds = 3;
            s.NoiseRa = new double[seeds][];
            s.NoiseDec = new double[seeds][];
            for (int k = 0; k < seeds; k++) {
                var rng = new Random(1234 + k);
                s.NoiseRa[k] = new double[n];
                s.NoiseDec[k] = new double[n];
                for (int i = 0; i < n; i++) {
                    s.NoiseRa[k][i] = Gauss(rng) * s.SigmaRa;
                    s.NoiseDec[k][i] = Gauss(rng) * s.SigmaDec;
                }
            }
            return s;
        }

        private static double[] Smooth(double[] u, int half) {
            int n = u.Length;
            var r = new double[n];
            for (int i = 0; i < n; i++) {
                int a = Math.Max(0, i - half), b = Math.Min(n - 1, i + half);
                // fenêtre symétrique aux bords pour ne pas biaiser
                int h = Math.Min(i - a, b - i);
                a = i - h; b = i + h;
                double sum = 0;
                for (int j = a; j <= b; j++) sum += u[j];
                r[i] = sum / (b - a + 1);
            }
            return r;
        }

        private static double Gauss(Random rng) {
            double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }

        /// <summary>Bruit de mesure rapide (seeing, photons) : écart-type estimé sur les différences secondes, robuste.</summary>
        private static double NoiseSigma(double[] u) {
            if (u.Length < 10) return 0.2;
            var d = new List<double>();
            for (int i = 1; i < u.Length - 1; i++) d.Add(Math.Abs(u[i + 1] - 2 * u[i] + u[i - 1]));
            d.Sort();
            double med = d[d.Count / 2];
            return Math.Max(0.02, 1.4826 * med / Math.Sqrt(6.0));
        }

        /// <summary>RMS de l'erreur vraie (px, total RA + Dec) obtenue en rejouant la boucle avec ce jeu de réglages ; moyenne sur plusieurs graines de bruit.</summary>
        private double Simulate(TuneSet s, Series d, bool all) {
            double sum = 0;
            int seeds = d.NoiseRa.Length;
            for (int k = 0; k < seeds; k++) sum += SimulateOnce(s, d, d.NoiseRa[k], d.NoiseDec[k]);
            return sum / seeds;
        }

        private double SimulateOnce(TuneSet s, Series d, double[] nzRa, double[] nzDec) {
            double cRaSim = 0, cDecSim = 0, prevRa = 0, prevDec = 0, integ = 0;
            double sa = 0, sb = 0;
            const int MinPulse = 10;
            const double IntegMax = 1.0, IntegMaxErr = 2.0;
            double raR = raRate > 0 ? raRate : 0.0016;
            int n = d.N;
            for (int k = 0; k < n; k++) {
                double aTrue = d.URa[k] - cRaSim;
                double aMeas = aTrue + nzRa[k];
                sa += aTrue * aTrue;
                double rawRa = Math.Abs(aMeas) >= s.MinMove ? s.AggrRa * aMeas / raR : 0;
                double cmdRa = (1 - s.HystRa) * rawRa + s.HystRa * prevRa;
                prevRa = cmdRa;
                double send = cmdRa + d.Ff[k];
                double msRa = Math.Min(maxRaMs, Math.Abs(send));
                double pulseRa = msRa >= MinPulse ? Math.Sign(send) * msRa : 0;
                cRaSim += pulseRa * raR;

                double bTrue = d.UDec[k] - cDecSim;
                sb += bTrue * bTrue;
                if (guideDec && decRateN > 0) {
                    double bMeas = bTrue + nzDec[k];
                    double integAdd = 0;
                    if (s.IntegDec > 0 && Math.Abs(bMeas) < IntegMaxErr) {
                        double next = Math.Max(-IntegMax, Math.Min(IntegMax, integ + s.IntegDec * bMeas));
                        integAdd = next - integ;
                        integ = next;
                    }
                    bool active = Math.Abs(bMeas) >= s.MinMove || (s.IntegDec > 0 && Math.Abs(integ) >= s.MinMove * 0.5);
                    double rawDec = active ? (s.AggrDec * bMeas + (s.IntegDec > 0 ? integ : 0)) / decRateN : 0;
                    double cmdDec = (1 - s.HystDec) * rawDec + s.HystDec * prevDec;
                    if (decMode == 1 && cmdDec > 0) cmdDec = 0;
                    if (decMode == 2 && cmdDec < 0) cmdDec = 0;
                    if (integAdd != 0 && ((active && cmdDec == 0) || Math.Abs(cmdDec) > maxDecMs)) integ -= integAdd;
                    prevDec = cmdDec;
                    double msDec = Math.Min(maxDecMs, Math.Abs(cmdDec));
                    if (msDec >= MinPulse) {
                        double pulse = cmdDec > 0 ? msDec : -msDec;
                        cDecSim += (pulse > 0 ? decRateS : decRateN) * pulse;
                    }
                }
            }
            return Math.Sqrt((sa + sb) / n);
        }

        // ---------------------------------------------------------------------------------------------------------------------------
        // Recherche des réglages : descente par coordonnées, chaque réglage ne pouvant bouger que d'un petit pas par essai

        private class Dim {
            public string Name;
            public double Lo, Hi, RelStep, AbsStep;
            public Func<TuneSet, double> Get;
            public Action<TuneSet, double> Set;
        }

        private TuneSet Optimize(Series d, TuneSet start, out double predStart, out double predBest) {
            var dims = new List<Dim> {
                new Dim { Name = "agressivité RA", Lo = 0.10, Hi = 0.90, RelStep = 0.30, AbsStep = 0.05, Get = t => t.AggrRa, Set = (t, v) => t.AggrRa = v },
                new Dim { Name = "hystérésis RA", Lo = 0.0, Hi = 0.50, RelStep = 0.30, AbsStep = 0.05, Get = t => t.HystRa, Set = (t, v) => t.HystRa = v },
                new Dim { Name = "mouvement minimal", Lo = 0.05, Hi = 0.40, RelStep = 0.30, AbsStep = 0.03, Get = t => t.MinMove, Set = (t, v) => t.MinMove = v }
            };
            if (guideDec && decRateN > 0) {
                dims.Add(new Dim { Name = "agressivité Dec", Lo = 0.10, Hi = 0.90, RelStep = 0.30, AbsStep = 0.05, Get = t => t.AggrDec, Set = (t, v) => t.AggrDec = v });
                dims.Add(new Dim { Name = "hystérésis Dec", Lo = 0.0, Hi = 0.50, RelStep = 0.30, AbsStep = 0.05, Get = t => t.HystDec, Set = (t, v) => t.HystDec = v });
                dims.Add(new Dim { Name = "intégrale Dec", Lo = 0.0, Hi = 0.30, RelStep = 0.30, AbsStep = 0.05, Get = t => t.IntegDec, Set = (t, v) => t.IntegDec = v });
            }
            predStart = Simulate(start, d, true);
            var best = start.Clone();
            double bestCost = predStart;
            for (int pass = 0; pass < 3; pass++) {
                bool improved = false;
                foreach (var dim in dims) {
                    double cur0 = dim.Get(start);   // les pas sont comptés à partir du réglage de départ
                    double window = Math.Max(Math.Abs(cur0) * dim.RelStep, dim.AbsStep);
                    double lo = Math.Max(dim.Lo, cur0 - window), hi = Math.Min(dim.Hi, cur0 + window);
                    for (int g = 0; g <= 8; g++) {
                        double v = Math.Round(lo + (hi - lo) * g / 8.0, 2);   // pas de 1 % (ou 0,01 px)
                        var trial = best.Clone();
                        dim.Set(trial, v);
                        double c = Simulate(trial, d, true);
                        if (c < bestCost * 0.998) { bestCost = c; best = trial; improved = true; }
                    }
                }
                if (!improved) break;
            }
            predBest = bestCost;
            return best;
        }
    }
}
