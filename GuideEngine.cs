using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugin.InternalGuider {
    /// <summary>Réglages du guidage, relus à chaque image (modifiables pendant le guidage).</summary>
    public class GuideParameters {
        /// <summary>Fraction de l'erreur corrigée à chaque image, en RA (0,2 = 20 %).</summary>
        public volatile float AggressivenessRa = 0.3f;
        public volatile float AggressivenessDec = 0.3f;
        public volatile bool GuideDec = true;
        /// <summary>Sens autorisés en Dec : 0 = Auto (Nord et Sud), 1 = Nord seul, 2 = Sud seul.</summary>
        public volatile int DecMode = 0;
        /// <summary>Sens Dec réellement appliqué (DecMode, inversé si le pilote de la monture n'inverse pas les impulsions après un retournement).</summary>
        public volatile int DecModeApplied = 0;
        /// <summary>Erreur minimale (pixels) en dessous de laquelle on ne corrige pas.</summary>
        public volatile float MinMovePx = 0.15f;
        /// <summary>Durée maximale d'une impulsion de correction (ms), par axe.</summary>
        /// <summary>Hystérésis (0 à 0,9) : part de la commande précédente mélangée à la nouvelle (0 = aucune).</summary>
        public volatile float HysteresisRa = 0f;
        public volatile float HysteresisDec = 0f;
        /// <summary>Nombre maximal d'étoiles suivies (1 = guidage sur une seule étoile). Lu au démarrage du guidage.</summary>
        public volatile int MultiStarMax = 1;
        public volatile int MaxRaMs = 250;
        /// <summary>Gain de la correction intégrale en Dec (0 = désactivée ; 0,1 = 10 % de l'erreur ajoutée à chaque image). Supprime le décalage causé par une dérive constante.</summary>
        public volatile float IntegralDec = 0f;
        /// <summary>Après un retournement : impulsion Dec maximale temporaire (ms) pour rattraper vite le jeu et la dérive ; 0 = désactivé.</summary>
        public volatile int PostFlipMaxDecMs = 0;
        /// <summary>Compensation du jeu en Dec : durée (ms) ajoutée à la première impulsion après une inversion de sens (0 = désactivée).</summary>
        public volatile int DecBacklashMs = 0;
        /// <summary>Vrai au démarrage d'un guidage qui suit un retournement (lu au démarrage par le moteur).</summary>
        public volatile bool PostFlipBoost = false;
        public volatile int MaxDecMs = 250;
        /// <summary>Suspend les corrections quand le flux des étoiles tombe sous CloudFluxRatio (nuage).</summary>
        public volatile bool CloudPause = true;
        /// <summary>Seuil (0,2 à 0,9) du flux relatif en dessous duquel les corrections sont suspendues.</summary>
        public volatile float CloudFluxRatio = 0.5f;
        /// <summary>Durée maximale (s) d'attente du retour de l'étoile ou du ciel clair avant d'arrêter le guidage (0 = sans limite).</summary>
        public volatile int LostTimeoutSec = 600;
        /// <summary>Guidage mono-étoile : suit une seconde étoile en réserve et bascule dessus si la première disparaît.</summary>
        public volatile bool BackupStar = true;
        /// <summary>PEC logiciel : applique la courbe d'erreur périodique RA mémorisée (anticipation).</summary>
        public volatile bool PecApply = false;
        /// <summary>PEC logiciel : enregistre la courbe d'erreur périodique RA pendant ce guidage.</summary>
        public volatile bool PecRecord = false;
        /// <summary>Force de la PEC appliquée (0,1 à 1).</summary>
        public volatile float PecStrength = 0.8f;
        /// <summary>Échelle de l'image (secondes d'arc par pixel), nécessaire à la PEC ; 0 = inconnue.</summary>
        public double PixelScale = 0;
        /// <summary>Mode IA : règle automatiquement l'agressivité, l'hystérésis, le mouvement minimal et l'intégrale Dec pendant le guidage.</summary>
        public volatile bool AiMode = false;
        public volatile int AiLearnMin = 15;
        public volatile int AiEvalMin = 8;
        public volatile int AiRetuneMin = 30;
    }

    /// <summary>Valeurs par défaut du dither et de la stabilisation (à relier plus tard aux réglages du profil NINA).</summary>
    public static class GuideDefaults {
        public const double DitherPixels = 3.0;
        public const bool DitherRaOnly = false;
        public const double SettlePixels = 1.5;
        public const double SettleTimeSec = 10;
        public const double SettleTimeoutSec = 40;
    }

    /// <summary>Une mesure de guidage. Erreurs en pixels, projetées sur les axes de la monture.</summary>
    public class GuideSample {
        public double Time;
        /// <summary>Positif = l'étoile s'est déplacée dans le sens d'une impulsion OUEST.</summary>
        public double ErrRaPx;
        /// <summary>Positif = l'étoile s'est déplacée dans le sens d'une impulsion NORD.</summary>
        public double ErrDecPx;
        /// <summary>Impulsion envoyée pour corriger (ms) : positive = Est / Sud, négative = Ouest / Nord, 0 = aucune.</summary>
        public int PulseRaMs;
        public int PulseDecMs;
        // Détails de la mesure (pour le journal)
        public double StarX;
        public double StarY;
        public double Snr;
        public double Hfd;
        public double Flux;
        public double LockX;
        public double LockY;
        /// <summary>Nombre d'étoiles utilisées pour cette mesure.</summary>
        public int StarsUsed;
    }

    /// <summary>
    /// Boucle de guidage : à chaque image, l'écart de l'étoile par rapport à sa position verrouillée est décomposé
    /// sur les deux axes de la monture (d'après la calibration), puis corrigé par une impulsion proportionnelle
    /// (agressivité x écart / vitesse de la monture).
    /// </summary>
    public class GuideEngine {
        private const double MaxSearchPx = 20;
        private const double LockEdgePx = 30;
        private const double MinEdgePx = 12;
        private const int MinPulseMs = 10;

        private readonly Func<CancellationToken, Task<DetectionResult>> capture;
        private readonly IGuideMount mount;
        private readonly CalibrationData cal;
        private readonly GuideParameters parameters;
        private readonly Action<string> report;
        private readonly Action<DetectionResult> onFrame;
        private readonly Action<GuideSample> onSample;
        private readonly Action onLocked;
        private readonly Random rng = new Random();
        private DitherRequest pending;

        /// <summary>Position verrouillée actuelle (coordonnées de l'image analysée : ajouter 0,5 pour le capteur).</summary>
        private PecRuntime pecRt;
        private AutoTuner aiRt;

        /// <summary>Levé quand le mode IA modifie les réglages de guidage (pour mettre à jour l'affichage).</summary>
        public event Action ParametersChanged;

        /// <summary>État du mode IA pour l'affichage (vide si inactif).</summary>
        /// <summary>Réglages validés par le mode IA, à conserver (null si aucun).</summary>
        public TuneSet AiRetained { get { return aiRt?.Retained; } }
        public string AiStatus { get { return aiRt?.Status ?? ""; } }

        /// <summary>État de la PEC pour l'affichage (vide si inactive).</summary>
        public string PecStatus { get { return pecRt?.Status ?? ""; } }
        /// <summary>Vrai si ce guidage a enregistré une courbe PEC (durée suffisante).</summary>
        public bool PecRecordCompleted { get { return pecRt != null && pecRt.RecordCompleted; } }

        public double LockX { get; private set; }
        public double LockY { get; private set; }

        private class DitherRequest {
            public double Pixels;
            public bool RaOnly;
            public double SettlePx;
            public double SettleSec;
            public double TimeoutSec;
            public TaskCompletionSource<bool> Tcs;
            public bool Started;
            public Stopwatch Clock;
            public Stopwatch SettleClock;
            public bool InSettle;
            public Stopwatch Created = Stopwatch.StartNew();
        }

        /// <summary>
        /// Décale la position verrouillée d'un petit déplacement aléatoire (Pixels = 0 : aucun décalage), puis attend que
        /// l'erreur reste sous SettlePx pendant SettleSec secondes. Vrai si stabilisé, faux si délai dépassé ou guidage arrêté.
        /// </summary>
        public Task<bool> DitherAsync(double pixels, bool raOnly, double settlePx, double settleSec, double timeoutSec, CancellationToken ct) {
            var req = new DitherRequest {
                Pixels = pixels, RaOnly = raOnly, SettlePx = settlePx, SettleSec = settleSec, TimeoutSec = timeoutSec,
                Tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
            };
            var old = Interlocked.Exchange(ref pending, req);
            old?.Tcs.TrySetResult(false);
            ct.Register(() => req.Tcs.TrySetResult(false));
            return req.Tcs.Task;
        }

        /// <summary>Événements notables (étoile perdue ou retrouvée, dither…), pour le journal.</summary>
        public event Action<string> LogEvent;

        private void Log(string text) {
            try { LogEvent?.Invoke(text); } catch (Exception) { }
        }

        public GuideEngine(Func<CancellationToken, Task<DetectionResult>> capture, IGuideMount mount, CalibrationData cal,
                           GuideParameters parameters, Action<string> report, Action<DetectionResult> onFrame,
                           Action<GuideSample> onSample, Action onLocked = null) {
            this.onLocked = onLocked;
            this.capture = capture;
            this.mount = mount;
            this.cal = cal;
            this.parameters = parameters;
            this.report = report;
            this.onFrame = onFrame;
            this.onSample = onSample;
        }

        public async Task RunAsync(CancellationToken ct) {
            try {
                await RunCoreAsync(ct).ConfigureAwait(false);
            } finally {
                try { aiRt?.Finish(); } catch (Exception ex) { Log("IA : erreur : " + ex.Message); }
                try { pecRt?.Finish(mount.DeclinationDegrees); } catch (Exception ex) { Log("PEC : erreur à l'enregistrement : " + ex.Message); }
                Interlocked.Exchange(ref pending, null)?.Tcs.TrySetResult(false);
            }
        }

        private async Task RunCoreAsync(CancellationToken ct) {
            // Vecteurs unitaires (dans l'image) d'une impulsion Ouest et d'une impulsion Nord
            double ra = cal.RaAngleDeg * Math.PI / 180.0;
            double de = cal.DecAngleDeg * Math.PI / 180.0;
            double ux1 = Math.Cos(ra), uy1 = Math.Sin(ra);
            double ux2 = Math.Cos(de), uy2 = Math.Sin(de);
            double det = ux1 * uy2 - uy1 * ux2;
            if (Math.Abs(det) < 0.3) throw new CalibrationException("calibration inutilisable (axes presque parallèles) : recalibrez.");

            report("choix de l'étoile de guidage…");
            var first = await capture(ct).ConfigureAwait(false);
            var candidates = first.Stars
                .Where(s => s.X > LockEdgePx && s.Y > LockEdgePx && s.X < first.Width - LockEdgePx && s.Y < first.Height - LockEdgePx)
                .ToList();
            var primary = StarSelection.Pick(candidates);
            if (primary == null) {
                throw new CalibrationException("aucune étoile de guidage utilisable (assez brillante, isolée, non saturée, loin des bords).");
            }
            var tracker = new MultiStarTracker(parameters.MultiStarMax, parameters.BackupStar);
            tracker.Init(first, primary);
            double ditherX = 0;
            double ditherY = 0;
            double lockX = tracker.PrimaryLockX;
            double lockY = tracker.PrimaryLockY;
            LockX = lockX;
            LockY = lockY;
            first.Selected = primary;
            first.Tracked = tracker.CurrentStars;
            onFrame?.Invoke(first);
            Log(tracker.MaxStars > 1
                ? $"guidage multi-étoiles : {tracker.Count} étoile(s) suivie(s) (maximum {tracker.MaxStars})"
                : "guidage sur une seule étoile" + (parameters.BackupStar ? (tracker.HasBackup ? " (étoile de secours suivie)" : " (pas d'étoile de secours disponible pour l'instant)") : ""));
            onLocked?.Invoke();

            double? calDec = cal.DeclinationDeg;
            var clock = Stopwatch.StartNew();
            pecRt = new PecRuntime(parameters.PecApply, parameters.PecRecord, parameters.PecStrength * 100.0, parameters.PixelScale, Log, mount.DeclinationDegrees);
            double lastSampleT = -1;
            double lastDitherT = -1000;
            aiRt = parameters.AiMode ? new AutoTuner(parameters, cal, parameters.AiLearnMin, parameters.AiEvalMin, parameters.AiRetuneMin, Log, () => { try { ParametersChanged?.Invoke(); } catch (Exception) { } }) : null;
            int lost = 0;
            double prevCmdRa = 0;
            double prevCmdDec = 0;
            int lastDecDir = 0;   // sens de la dernière impulsion Dec envoyée (+1 Sud, -1 Nord, 0 = aucune)
            double integDec = 0;   // somme corrigée de l'erreur Dec (px), correction intégrale
            const double IntegMaxPx = 1.0;      // plafond de la correction intégrale
            const double IntegMaxErrPx = 2.0;   // au-delà, l'erreur est un transitoire (dither, rattrapage) : on n'intègre pas
            Stopwatch boostClock = parameters.PostFlipBoost && parameters.PostFlipMaxDecMs > parameters.MaxDecMs ? Stopwatch.StartNew() : null;
            int boostCalm = 0;
            if (boostClock != null) Log($"retournement : impulsion Dec maximale portée à {parameters.PostFlipMaxDecMs} ms jusqu'à la stabilisation (120 s maximum)");
            bool inCloud = false;
            bool onBackup = false;
            double minRatio = 1;
            Stopwatch pauseClock = null;     // depuis quand le guidage est suspendu (étoile perdue ou nuage)

            void CheckPause() {
                if (pauseClock == null) return;
                // Une demande de dither/stabilisation ne doit pas bloquer NINA pendant l'interruption
                var rq = Volatile.Read(ref pending);
                if (rq != null && !rq.Started && rq.Created.Elapsed.TotalSeconds >= rq.TimeoutSec) {
                    Interlocked.CompareExchange(ref pending, null, rq);
                    rq.Tcs.TrySetResult(false);
                    Log("dither/stabilisation abandonné : guidage suspendu (nuage ou étoile perdue)");
                }
                int limit = parameters.LostTimeoutSec;
                if (limit > 0 && pauseClock.Elapsed.TotalSeconds > limit) {
                    throw new CalibrationException($"guidage suspendu depuis plus de {limit / 60.0:0.#} min (nuages ou étoile perdue) : guidage arrêté.");
                }
            }

            while (!ct.IsCancellationRequested) {
                var r = await capture(ct).ConfigureAwait(false);

                var m = tracker.Update(r);
                foreach (var ev in m?.Events ?? new System.Collections.Generic.List<string>()) Log(ev);
                if (m == null) {
                    lost++;
                    if (pauseClock == null) pauseClock = Stopwatch.StartNew();
                    if (lost == 1) Log($"étoile perdue ({r.Stars.Count} étoiles détectées dans l'image) : corrections suspendues");
                    prevCmdRa = 0;
                    prevCmdDec = 0;
                    integDec = 0;
                    onFrame?.Invoke(r);
                    report($"étoile perdue depuis {pauseClock.Elapsed.TotalSeconds:0} s (nuage ?) — corrections suspendues, recherche en cours");
                    CheckPause();
                    continue;
                }
                if (tracker.UsesBackup(m) != onBackup) {
                    onBackup = !onBackup;
                    Log(onBackup ? "étoile de guidage absente : mesure sur l'étoile de secours" : "étoile de guidage retrouvée : retour à l'étoile de guidage");
                }
                if (lost > 0) Log($"étoile retrouvée après {lost} image(s) ({(pauseClock?.Elapsed.TotalSeconds ?? 0):0} s)");
                lost = 0;
                var best = m.Reference;
                r.Selected = best;
                r.Tracked = m.Used;
                onFrame?.Invoke(r);

                // Nuage : le flux des étoiles chute, les mesures deviennent bruitées ou fausses → on suspend les corrections
                if (parameters.CloudPause) {
                    double thr = parameters.CloudFluxRatio;
                    double exitThr = Math.Min(0.95, thr + 0.15);    // hystérésis : on reprend un peu au-dessus du seuil d'arrêt
                    if (!inCloud && m.FluxRatio < thr) {
                        inCloud = true;
                        minRatio = m.FluxRatio;
                        Log($"nuage détecté : flux à {m.FluxRatio * 100:0} % du normal (seuil {thr * 100:0} %) : corrections suspendues");
                    } else if (inCloud && m.FluxRatio >= exitThr) {
                        inCloud = false;
                        Log($"ciel dégagé : flux revenu à {m.FluxRatio * 100:0} % du normal après {(pauseClock?.Elapsed.TotalSeconds ?? 0):0} s (minimum {minRatio * 100:0} %) : corrections reprises");
                        prevCmdRa = 0;
                        prevCmdDec = 0;
                        integDec = 0;
                    }
                    if (inCloud) {
                        minRatio = Math.Min(minRatio, m.FluxRatio);
                        if (pauseClock == null) pauseClock = Stopwatch.StartNew();
                        report($"nuage : flux à {m.FluxRatio * 100:0} % du normal — corrections suspendues");
                        CheckPause();
                        continue;
                    }
                } else if (inCloud) {
                    inCloud = false;
                }
                pauseClock = null;
                tracker.AdoptFlux();
                if (tracker.MaxStars == 1 &&
                    (best.X < MinEdgePx || best.Y < MinEdgePx || best.X > r.Width - MinEdgePx || best.Y > r.Height - MinEdgePx)) {
                    throw new CalibrationException("l'étoile de guidage est arrivée près du bord de l'image : guidage arrêté.");
                }
                // L'étoile principale a pu changer : la position verrouillée suit
                lockX = tracker.PrimaryLockX + ditherX;
                lockY = tracker.PrimaryLockY + ditherY;
                LockX = lockX;
                LockY = lockY;

                // Dither demandé : on décale la position verrouillée (déplacement aléatoire sur les axes de la monture)
                var req = Volatile.Read(ref pending);
                if (req != null && !req.Started) {
                    req.Started = true;
                    lastDitherT = clock.Elapsed.TotalSeconds;
                    req.Clock = Stopwatch.StartNew();
                    // Dec à sens unique (ou guidage Dec coupé) : un décalage en Dec du mauvais côté ne pourrait être rattrapé
                    // qu'en inversant le sens de la monture (jeu de plusieurs secondes) ou par la dérive naturelle : on dither en RA seul.
                    bool forcedRaOnly = !req.RaOnly && (parameters.DecMode != 0 || !parameters.GuideDec);
                    bool ditherRaOnly = req.RaOnly || forcedRaOnly;
                    Log(req.Pixels > 0
                        ? $"dither commencé : {req.Pixels:0.#} px max{(ditherRaOnly ? ", RA seul" : "")}{(forcedRaOnly ? " (imposé : Dec à sens unique ou guidage Dec désactivé)" : "")}"
                        : "attente de stabilisation commencée");
                    if (req.Pixels > 0) {
                        double oa = (rng.NextDouble() * 2 - 1) * req.Pixels;
                        double ob = ditherRaOnly ? 0 : (rng.NextDouble() * 2 - 1) * req.Pixels;
                        ditherX += oa * ux1 + ob * ux2;
                        ditherY += oa * uy1 + ob * uy2;
                        lockX = tracker.PrimaryLockX + ditherX;
                        lockY = tracker.PrimaryLockY + ditherY;
                        LockX = lockX;
                        LockY = lockY;
                    }
                }

                // Erreur décomposée sur les axes de la monture : e = a * u_ouest + b * u_nord
                double ex = m.Dx - ditherX;
                double ey = m.Dy - ditherY;
                double a = (ex * uy2 - ey * ux2) / det;
                double b = (ux1 * ey - uy1 * ex) / det;
                double nowT = clock.Elapsed.TotalSeconds;
                double frameDt = lastSampleT < 0 ? 1.5 : Math.Max(0.5, Math.Min(5.0, nowT - lastSampleT));
                lastSampleT = nowT;
                // Position RA sans dither (pour la PEC) : le dither déplace réellement l'étoile par des impulsions, déjà comptées avec les impulsions envoyées
                pecRt.AddSample(nowT, (m.Dx * uy2 - m.Dy * ux2) / det);

                // La vitesse en RA diminue avec cos(déclinaison) : on la corrige si la monture donne sa déclinaison actuelle.
                double raRate = cal.RaRatePxPerMs;
                double? decNow = mount.DeclinationDegrees;
                if (calDec.HasValue && decNow.HasValue) {
                    double cc = Math.Cos(calDec.Value * Math.PI / 180.0);
                    double cn = Math.Cos(decNow.Value * Math.PI / 180.0);
                    if (cc > 0.1 && cn > 0.1) raRate *= cn / cc;
                }

                double min = parameters.MinMovePx;
                int pulseRa = 0;
                int pulseDec = 0;

                // Commande brute (ms signées, positif = Est / Sud) ; sous le seuil de déplacement minimal, elle vaut 0.
                double rawRa = Math.Abs(a) >= min ? parameters.AggressivenessRa * a / raRate : 0;
                // Correction intégrale en Dec : accumule une part de l'erreur (sauf transitoires) pour annuler une dérive constante
                double ki = parameters.IntegralDec;
                double integAdd = 0;
                if (parameters.GuideDec && ki > 0 && Math.Abs(b) < IntegMaxErrPx) {
                    double next = Math.Max(-IntegMaxPx, Math.Min(IntegMaxPx, integDec + ki * b));
                    integAdd = next - integDec;
                    integDec = next;
                }
                bool decActive = parameters.GuideDec && (Math.Abs(b) >= min || (ki > 0 && Math.Abs(integDec) >= min * 0.5));
                double rawDec = decActive ? (parameters.AggressivenessDec * b + (ki > 0 ? integDec : 0)) / cal.DecRatePxPerMs : 0;

                // Hystérésis (comme PHD2) : nouvelle = (1 - h) x brute + h x précédente
                double hRa = parameters.HysteresisRa;
                double hDec = parameters.HysteresisDec;
                double cmdRa = (1 - hRa) * rawRa + hRa * prevCmdRa;
                double cmdDec = (1 - hDec) * rawDec + hDec * prevCmdDec;
                // Mode Dec à sens unique : on supprime les corrections du sens interdit (positif = Sud, négatif = Nord)
                int decMode = parameters.DecModeApplied;
                if (decMode == 1 && cmdDec > 0) cmdDec = 0;
                if (decMode == 2 && cmdDec < 0) cmdDec = 0;

                // Impulsion Dec maximale : temporairement relevée juste après un retournement
                int maxDec = parameters.MaxDecMs;
                if (boostClock != null) {
                    boostCalm = Math.Abs(b) < 1.0 ? boostCalm + 1 : 0;
                    if (boostClock.Elapsed.TotalSeconds > 120 || boostCalm >= 5) {
                        Log($"retournement : retour à l'impulsion Dec maximale normale ({parameters.MaxDecMs} ms) après {boostClock.Elapsed.TotalSeconds:0} s");
                        boostClock = null;
                    } else maxDec = Math.Max(maxDec, parameters.PostFlipMaxDecMs);
                }
                // Anti-emballement : si la commande est bloquée (sens interdit) ou plafonnée, on annule l'intégration de cette image
                if (integAdd != 0 && ((decActive && cmdDec == 0) || Math.Abs(cmdDec) > maxDec)) integDec -= integAdd;
                prevCmdRa = cmdRa;
                prevCmdDec = parameters.GuideDec ? cmdDec : 0;
                // PEC : impulsion d'anticipation pour la dérive périodique prévue jusqu'à l'image suivante (hors hystérésis et seuil de mouvement)
                double ffMs = pecRt.FeedForwardMs(nowT, frameDt, raRate);
                double cmdRaSend = cmdRa + ffMs;

                int msRa = (int)Math.Min(parameters.MaxRaMs, Math.Abs(cmdRaSend));
                if (msRa >= MinPulseMs) pulseRa = cmdRaSend > 0 ? msRa : -msRa;   // étoile "trop à l'ouest" : impulsion Est
                if (parameters.GuideDec) {
                    int msDec = (int)Math.Min(maxDec, Math.Abs(cmdDec));
                    if (msDec >= MinPulseMs) pulseDec = cmdDec > 0 ? msDec : -msDec;   // étoile "trop au nord" : impulsion Sud
                }

                if (pulseRa != 0) {
                    await mount.PulseAsync(pulseRa > 0 ? GuideDir.East : GuideDir.West, Math.Abs(pulseRa), ct).ConfigureAwait(false);
                    pecRt.AddPulse(pulseRa, raRate);
                }
                if (pulseDec != 0) {
                    int dir = pulseDec > 0 ? 1 : -1;
                    int ms = Math.Abs(pulseDec);
                    // Compensation du jeu : à l'inversion de sens, on ajoute la durée du jeu à l'impulsion (pas pour la toute première)
                    int comp = parameters.DecBacklashMs;
                    if (comp > 0 && lastDecDir != 0 && dir != lastDecDir) {
                        ms += comp;
                        Log($"jeu Dec : inversion de sens, +{comp} ms ajoutées à l'impulsion ({ms} ms au total)");
                    }
                    lastDecDir = dir;
                    await mount.PulseAsync(dir > 0 ? GuideDir.South : GuideDir.North, ms, ct).ConfigureAwait(false);
                }

                // Mode IA : enregistre l'image (hors dither et rattrapage après retournement pour les statistiques)
                if (aiRt != null) {
                    double aRaw = (m.Dx * uy2 - m.Dy * ux2) / det;
                    double bRaw = (ux1 * m.Dy - uy1 * m.Dx) / det;
                    aiRt.AddFrame(nowT, aRaw, bRaw, a, b, pulseRa, pulseDec, ffMs, raRate, boostClock != null || nowT - lastDitherT < 30);
                }

                if (req != null && req.Started) {
                    double err = Math.Sqrt(a * a + b * b);
                    if (err < req.SettlePx) {
                        if (!req.InSettle) { req.InSettle = true; req.SettleClock = Stopwatch.StartNew(); }
                        else if (req.SettleClock.Elapsed.TotalSeconds >= req.SettleSec) {
                            Log($"{(req.Pixels > 0 ? "dither stabilisé" : "guidage stabilisé")} ({req.Clock.Elapsed.TotalSeconds:0.0} s)");
                            Interlocked.CompareExchange(ref pending, null, req);
                            req.Tcs.TrySetResult(true);
                        }
                    } else {
                        req.InSettle = false;
                    }
                    if (req.Clock.Elapsed.TotalSeconds >= req.TimeoutSec) {
                        Log($"{(req.Pixels > 0 ? "dither" : "stabilisation")} : délai dépassé ({req.TimeoutSec:0} s), guidage poursuivi");
                        Interlocked.CompareExchange(ref pending, null, req);
                        req.Tcs.TrySetResult(false);
                    }
                }

                var sample = new GuideSample {
                    Time = clock.Elapsed.TotalSeconds,
                    ErrRaPx = a,
                    ErrDecPx = b,
                    PulseRaMs = pulseRa,
                    PulseDecMs = pulseDec,
                    StarX = best.X + 0.5,
                    StarY = best.Y + 0.5,
                    Snr = best.Snr,
                    Hfd = best.Hfd,
                    Flux = best.Flux,
                    LockX = lockX + 0.5,
                    LockY = lockY + 0.5,
                    StarsUsed = m.Used.Count
                };
                onSample?.Invoke(sample);
                report($"erreur RA {a:+0.00;-0.00} px, Dec {b:+0.00;-0.00} px · impulsions RA {FormatPulse(pulseRa, 'E', 'O')}, Dec {FormatPulse(pulseDec, 'S', 'N')}");
            }
            ct.ThrowIfCancellationRequested();
        }

        private static string FormatPulse(int ms, char positive, char negative) =>
            ms == 0 ? "—" : $"{(ms > 0 ? positive : negative)} {Math.Abs(ms)} ms";
    }
}
