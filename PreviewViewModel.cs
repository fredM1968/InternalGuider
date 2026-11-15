using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NINA.Core.Model.Equipment;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Model;
using NINA.Image.Interfaces;
using NINA.Profile.Interfaces;

namespace NINA.Plugin.InternalGuider {
    /// <summary>
    /// Boucle de poses courtes sur la caméra de guidage, détection d'étoiles, et image d'aperçu
    /// (étirement automatique + cadres sur les étoiles, croix verte sur l'étoile de guidage choisie).
    /// Gère aussi la calibration (la boucle d'aperçu est alors suspendue puis relancée).
    /// Le rendu est fait ici (sans passer par IRenderedImage) pour rester indépendant de l'API de rendu de NINA.
    /// </summary>
    public partial class PreviewViewModel : INotifyPropertyChanged {
        private CancellationTokenSource cts;
        private Task loopTask;
        private CancellationTokenSource calibrationCts;
        private ICamera cameraRef;
        private bool shutdown;
        private bool calibrating;
        private int frameCounter;
        private ImageSource image;
        private string status = "En attente de la première image…";
        private volatile DetectionResult lastResult;
        private readonly SimMount simMount = new SimMount();
        private volatile string calibrationNotice;
        private int pendingGain = -1;
        private bool gainInitialized;
        private string gainText = "";
        private CancellationTokenSource guideCts;
        private GuideEngine activeEngine;
        private Task guideTask;
        private TaskCompletionSource<bool> lockedTcs;
        private bool guiding;
        private static readonly int[] HistoryChoices = { 25, 50, 100, 200, 400 };
        private static readonly double[] ScaleChoices = { 0.5, 1, 2, 4, 8, 16 };   // demi-hauteur du graphe, dans l'unité affichée
        private int historyIndex = 2;
        private bool graphInArcsec = GuiderSettings.GraphInArcsec;     // unité mémorisée d'une session à l'autre
        private int scaleIndex = GuiderSettings.GraphInArcsec ? 3 : 2;   // échelle de départ de même ordre dans l'unité choisie
        private readonly GuideParameters guideParameters = new GuideParameters();
        private int simGuideFrames;
        private readonly List<GuideSample> samples = new List<GuideSample>();
        private double sumRa;
        private double sumDec;
        private double linRa;     // sommes simples des erreurs : servent à retirer l'erreur moyenne du RMS
        private double linDec;
        private ImageSource graphImage;
        private string guideStats = "";
        private GuideLog guideLog;
        private string lastLoggedParams;

        public IList<double> ExposureChoices { get; } =
            new List<double> { 0.1, 0.2, 0.5, 0.75, 1, 1.5, 2, 2.5, 3, 4, 5 };

        /// <summary>Durée de pose en secondes (modifiable pendant l'aperçu).</summary>
        public double Exposure {
            get => exposure;
            set {
                if (value <= 0) return;
                exposure = value;
                RaisePropertyChanged();
                var camera = cameraRef;
                if (camera != null && exposureInitialized) GuideCameraSettings.SetExposure(camera.Id, value);   // mémorisée pour cette caméra
            }
        }
        private double exposure = 1.0;
        private bool exposureInitialized;

        /// <summary>Si vrai, ni la caméra ni la monture ne sont utilisées : ciel et monture synthétiques (tests en intérieur).</summary>
        public bool Simulation {
            get => simulation;
            set { simulation = value; PecStore.Simulation = value; RaisePropertyChanged(); }
        }
        private bool simulation;

        /// <summary>Échelle de l'image du capteur en secondes d'arc par pixel (0 si inconnue).</summary>
        public double PixelScale { get; set; }

        /// <summary>Profil NINA : fournit les réglages de dither et de stabilisation du guidage.</summary>
        public IProfileService ProfileService { get; set; }

        // --- Gain de la caméra de guidage ---

        public bool CanSetGain => cameraRef != null && GainIsAdjustable(cameraRef);
        public int GainMinimum => cameraRef?.GainMin ?? 0;
        public int GainMaximum => cameraRef?.GainMax ?? 0;

        /// <summary>Certains pilotes (ASCOM Atik, par exemple) annoncent un gain réglable avec une plage vide (-1 à -1).</summary>
        private static bool GainIsAdjustable(ICamera camera) => camera.CanSetGain && camera.GainMax > camera.GainMin;

        public string GainRangeText => CanSetGain ? $"(de {cameraRef.GainMin} à {cameraRef.GainMax})" : "(non réglable)";

        /// <summary>Gain saisi : appliqué à la prochaine pose (jamais pendant une pose) et mémorisé pour cette caméra.</summary>
        public string GainText {
            get => gainText;
            set {
                gainText = value;
                RaisePropertyChanged();
                var camera = cameraRef;
                if (camera == null || !GainIsAdjustable(camera)) return;
                if (int.TryParse((value ?? "").Trim(), out int gain)) {
                    gain = Math.Max(camera.GainMin, Math.Min(camera.GainMax, gain));
                    pendingGain = gain;
                    GuideCameraSettings.SetGain(camera.Id, gain);
                    if (gainText != gain.ToString()) { gainText = gain.ToString(); RaisePropertyChanged(); }
                }
            }
        }

        /// <summary>Reprend la dernière pose utilisée avec cette caméra (une seule fois par ouverture de l'aperçu).</summary>
        private void InitializeExposure(ICamera camera) {
            if (exposureInitialized || camera == null) return;
            var saved = GuideCameraSettings.GetExposure(camera.Id);
            if (saved.HasValue && ExposureChoices.Contains(saved.Value)) {
                exposure = saved.Value;
                RaisePropertyChanged(nameof(Exposure));
            }
            exposureInitialized = true;
        }

        private void InitializeGain(ICamera camera) {
            if (gainInitialized) return;
            gainInitialized = true;
            try {
                if (GainIsAdjustable(camera)) {
                    var saved = GuideCameraSettings.GetGain(camera.Id);
                    int gain = saved.HasValue
                        ? Math.Max(camera.GainMin, Math.Min(camera.GainMax, saved.Value))
                        : camera.Gain;
                    if (saved.HasValue) pendingGain = gain;
                    gainText = gain.ToString();
                }
            } catch (Exception ex) {
                Logger.Error(ex);
            }
            RaisePropertyChanged(nameof(GainText));
            RaisePropertyChanged(nameof(CanSetGain));
            RaisePropertyChanged(nameof(CanAutoGain));
            RaisePropertyChanged(nameof(GainRangeText));
        }

        private void ApplyPendingGain(ICamera camera) {
            int gain = Interlocked.Exchange(ref pendingGain, -1);
            if (gain < 0) return;
            try {
                if (camera.CanSetGain && camera.Gain != gain) {
                    camera.Gain = gain;
                    Logger.Info($"InternalGuider: gain de la caméra de guidage réglé à {gain}");
                }
            } catch (Exception ex) {
                Logger.Error(ex);
            }
        }

        /// <summary>Reporte l'amplitude du dither du plugin dans le profil NINA, pour que l'onglet Guidage l'affiche (« Guider Dither Pixels »).</summary>
        public void SyncDitherToProfile() {
            try {
                var g = ProfileService?.ActiveProfile?.GuiderSettings;
                if (g == null) return;
                g.DitherPixels = GuiderSettings.DitherPixels;
                g.DitherRAOnly = GuiderSettings.DitherRaOnly;
            } catch (Exception ex) {
                Logger.Error(ex);
            }
        }

        private (double Dither, bool RaOnly, double SettlePx, double SettleSec, double TimeoutSec) ReadGuideSettings() {
            var result = (Dither: GuideDefaults.DitherPixels, RaOnly: GuideDefaults.DitherRaOnly,
                          SettlePx: GuideDefaults.SettlePixels, SettleSec: GuideDefaults.SettleTimeSec,
                          TimeoutSec: GuideDefaults.SettleTimeoutSec);
            // Dither : réglages propres au plugin (ceux du profil NINA sont ceux de PHD2)
            result.Dither = GuiderSettings.DitherPixels;
            result.RaOnly = GuiderSettings.DitherRaOnly;
            try {
                var g = ProfileService?.ActiveProfile?.GuiderSettings;
                if (g != null) {
                    if (g.SettlePixels > 0) result.SettlePx = g.SettlePixels;
                    if (g.SettleTime > 0) result.SettleSec = g.SettleTime;
                    if (g.SettleTimeout > 0) result.TimeoutSec = g.SettleTimeout;
                }
            } catch (Exception ex) {
                Logger.Error(ex);
            }
            return result;
        }

        /// <summary>Monture réelle (via NINA), ou null si indisponible.</summary>
        public IGuideMount Mount { get; set; }

        /// <summary>Dernier résultat de détection (servira à la calibration et au guidage).</summary>
        public DetectionResult LastResult => lastResult;

        public bool IsCalibrating {
            get => calibrating;
            private set { calibrating = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(CanAutoGain)); RaisePropertyChanged(nameof(CanCalibrate)); RaisePropertyChanged(nameof(CanGuide)); RaisePropertyChanged(nameof(CanStop)); }
        }

        public bool CanCalibrate => !calibrating && !guiding;

        public ImageSource Image {
            get => image;
            private set { image = value; RaisePropertyChanged(); }
        }

        public string Status {
            get => status;
            private set { status = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(StatusDisplay)); }
        }

        private string meridianText = "";

        /// <summary>Ligne d'état affichée : le message courant suivi du temps restant avant le passage au méridien (monture réelle).</summary>
        public string StatusDisplay {
            get {
                string s = status ?? "";
                foreach (var extra in new[] { aiText, pecText, meridianText }) {
                    if (string.IsNullOrEmpty(extra)) continue;
                    s = s.Length == 0 ? extra : s + " · " + extra;
                }
                return s;
            }
        }

        private string pecText = "";
        private string aiText = "";

        /// <summary>Reprend l'état de la PEC du moteur de guidage (vide hors guidage ou PEC inactive).</summary>
        private void UpdatePec() {
            string t = "", ai = "";
            try { t = activeEngine?.PecStatus ?? ""; ai = activeEngine?.AiStatus ?? ""; } catch (Exception) { }
            if (t != pecText || ai != aiText) {
                pecText = t;
                aiText = ai;
                RaisePropertyChanged(nameof(StatusDisplay));
            }
        }

        /// <summary>Recalcule le texte « méridien dans … » (appelé à chaque image).</summary>
        private void UpdateMeridian() {
            string t = "";
            try {
                double? h = Simulation ? null : Mount?.HoursToMeridian;
                if (h.HasValue) {
                    int minutes = (int)Math.Round(Math.Abs(h.Value) * 60.0);
                    string d = minutes >= 60 ? $"{minutes / 60} h {minutes % 60:00} min" : $"{minutes} min";
                    t = h.Value >= 0 ? $"méridien dans {d}" : $"méridien passé depuis {d}";
                }
            } catch (Exception) { }
            if (t != meridianText) {
                meridianText = t;
                RaisePropertyChanged(nameof(StatusDisplay));
            }
        }

        // FWHM moyen des étoiles (estimé : 0,94 x HFD, valable pour une étoile gaussienne), lissé sur quelques images
        private double fwhmPx = double.NaN;

        public bool ShowFwhm {
            get => GuiderSettings.ShowFwhm;
            set { GuiderSettings.ShowFwhm = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(FwhmText)); }
        }

        public string FwhmText {
            get {
                if (!ShowFwhm) return "";
                double f = fwhmPx;
                if (double.IsNaN(f)) return "FWHM moyen : -";
                return PixelScale > 0 ? $"FWHM moyen : {f:0.0} px ({f * PixelScale:0.0}\u2033)" : $"FWHM moyen : {f:0.0} px";
            }
        }

        // Profil de l'étoile (zoom, courbe, historique du HFD, mesures) : calculé avec chaque image, affiché dans le panneau de droite
        private readonly StarProfileRenderer starProfile = new StarProfileRenderer();
        private volatile StarProfileData starProfileData = new StarProfileData { Text = "Aucune étoile de guidage" };

        public bool ShowStarProfile {
            get => GuiderSettings.ShowStarProfile;
            set { GuiderSettings.ShowStarProfile = value; RaisePropertyChanged(); }
        }
        public ImageSource StarZoomImage => starProfileData.Zoom;
        public ImageSource StarCurveImage => starProfileData.Curve;
        public ImageSource StarHistoryImage => starProfileData.History;
        public string StarProfileText => starProfileData.Text;

        // Bilan de calibration (onglet du panneau latéral) : graphique, tableau, onglet affiché
        private ImageSource calibrationPlot;
        private IList<ReportRow> calibrationRows = new List<ReportRow>();
        private string calibrationHeader = "Aucune calibration enregistrée.";
        private int sideTabIndex;

        public ImageSource CalibrationPlotImage => calibrationPlot;
        public IList<ReportRow> CalibrationRows => calibrationRows;
        public string CalibrationHeader => calibrationHeader;
        /// <summary>Onglet du panneau latéral : 0 = Profil de l'étoile, 1 = Bilan calibration.</summary>
        public int SideTabIndex {
            get => sideTabIndex;
            set { sideTabIndex = value; RaisePropertyChanged(); }
        }

        /// <summary>Affiche le bilan d'une calibration dans le panneau latéral ; select = bascule sur l'onglet (fin de calibration).</summary>
        private void ShowCalibrationReport(CalibrationData d, bool select) {
            ImageSource plot = null;
            IList<ReportRow> rows = new List<ReportRow>();
            string header = "Aucune calibration enregistrée.";
            try {
                if (d != null) {
                    plot = CalibrationReport.Plot(d);
                    rows = CalibrationReport.Rows(d);
                    header = plot != null ? "Dernière calibration (RA vert, Dec rouge ; points pleins : aller, anneaux : retour)"
                                          : "Dernière calibration (graphique indisponible : calibration enregistrée avant cette version)";
                }
            } catch (Exception ex) { Logger.Warning("InternalGuider: bilan de calibration : " + ex.Message); }
            Application.Current?.Dispatcher.BeginInvoke(new Action(() => {
                calibrationPlot = plot;
                calibrationRows = rows;
                calibrationHeader = header;
                RaisePropertyChanged(nameof(CalibrationPlotImage));
                RaisePropertyChanged(nameof(CalibrationRows));
                RaisePropertyChanged(nameof(CalibrationHeader));
                if (select && d != null) {
                    if (!GuiderSettings.ShowStarProfile) GuiderSettings.ShowStarProfile = true;
                    RaisePropertyChanged(nameof(ShowStarProfile));
                    SideTabIndex = 1;
                }
            }));
        }

        private void RefreshStarProfile(DetectionResult r) {
            if (!GuiderSettings.ShowStarProfile) return;
            starProfileData = starProfile.Render(r, PixelScale);
        }

        private void UpdateFwhm(DetectionResult r) {
            var list = r.Tracked != null && r.Tracked.Count > 0 ? r.Tracked : r.Stars.Where(st => !st.Saturated && st.Snr >= 10).ToList();
            double sum = 0;
            int n = 0;
            foreach (var st in list) {
                if (st.Hfd > 0) { sum += st.Hfd; n++; }
            }
            if (n == 0) return;
            double now = 0.94 * sum / n;
            double old = fwhmPx;
            fwhmPx = double.IsNaN(old) ? now : 0.7 * old + 0.3 * now;
        }

        /// <summary>Caméra unique : l'aperçu en direct ne tourne que si l'utilisateur le demande, pour laisser la caméra à NINA.</summary>
        public bool MonoMode { get; set; }

        /// <summary>Caméra unique : pose passant par la chaîne de capture de NINA (même téléchargement et mêmes états que ses propres poses).</summary>
        public Func<CaptureSequence, CancellationToken, Task<NINA.Image.Interfaces.IExposureData>> MonoCapture { get; set; }

        /// <summary>Caméra unique : vrai si NINA est en train de poser (hors poses du plugin).</summary>
        public Func<bool> NinaBusy { get; set; }

        private volatile bool ownExposure;
        private DateTime lastNinaBusy = DateTime.MinValue;

        /// <summary>Caméra unique : attend que NINA ait fini de poser (et 1 s de calme) avant de prendre la caméra.</summary>
        private async Task WaitNinaIdleAsync(CancellationToken token) {
            bool noted = false;
            while (true) {
                token.ThrowIfCancellationRequested();
                bool busy = false;
                try { busy = NinaBusy != null && NinaBusy(); } catch (Exception) { }
                if (busy) lastNinaBusy = DateTime.UtcNow;
                if ((DateTime.UtcNow - lastNinaBusy).TotalSeconds >= 1) return;
                if (!noted) { noted = true; CameraDiagnostic.Log("plugin : caméra occupée par NINA, attente"); Publish(null, "Caméra unique : en attente, NINA utilise la caméra."); }
                await Task.Delay(200, token).ConfigureAwait(false);
            }
        }

        private bool liveView;

        public bool LiveView {
            get => liveView;
            set {
                if (value == liveView) return;
                if (value && (calibrating || guiding || cameraRef == null)) { RaisePropertyChanged(); return; }
                liveView = value;
                RaisePropertyChanged();
                // Caméra unique : aucune pose du plugin, on affiche les images que NINA produit
                InterExposureService.NinaImage -= OnNinaImage;
                if (value) {
                    InterExposureService.NinaImage += OnNinaImage;
                    Publish(null, "Caméra unique : en attente d'une image de NINA (pose, mise au point, astrométrie…).");
                } else {
                    Publish(null, "Caméra unique : l'aperçu n'affiche plus les images de NINA.");
                }
            }
        }

        public bool NotMonoMode => !MonoMode;

        public void Start(ICamera camera) {
            cameraRef = camera;
            SyncDitherToProfile();
            CalibrationData.CameraId = camera?.Id;     // les calibrations sont propres à chaque caméra de guidage
            ShowCalibrationReport(CalibrationData.Load(), false);
            InitializeExposure(camera);
            InitializeGain(camera);
            Stop();
            if (MonoMode) {
                // Pas de boucle de poses : la caméra reste à NINA (le guidage et la calibration prennent la caméra sur demande)
                Publish(null, liveView ? "Caméra unique : en attente d'une image de NINA (pose, mise au point, astrométrie…)."
                                       : "Caméra unique : la caméra reste à NINA. Cochez « Images de NINA » pour voir ses images ici.");
                return;
            }
            cts = new CancellationTokenSource();
            var token = cts.Token;
            loopTask = Task.Run(() => LoopAsync(camera, token));
        }

        public void Stop() {
            try {
                cts?.Cancel();
                loopTask?.Wait(TimeSpan.FromSeconds(3));
            } catch (Exception) {
                // annulation attendue
            } finally {
                cts?.Dispose();
                cts = null;
                loopTask = null;
            }
        }

        /// <summary>Arrêt définitif (fermeture de la fenêtre ou déconnexion) : annule aussi une calibration en cours.</summary>
        private int ninaFrames;
        private int ninaBusyFlag;

        /// <summary>Caméra unique : affiche une image prise par NINA, avec ses étoiles (image réduite).</summary>
        private void OnNinaImage(ushort[] small, int w, int h, int depth, int factor, double exposure) {
            if (calibrating || guiding) return;
            if (Interlocked.Exchange(ref ninaBusyFlag, 1) == 1) return;
            Task.Run(() => {
                try {
                    var r = StarDetector.Analyze(small, w, h, depth);
                    int n = Interlocked.Increment(ref ninaFrames);
                    string reduced = factor > 1 ? $" · réduite ×{factor}" : "";
                    Publish(BuildImage(r), $"Image de NINA {n} · pose {exposure:0.0##} s · {r.Stars.Count} étoiles{reduced}");
                } catch (Exception ex) {
                    Logger.Error(ex);
                } finally {
                    Interlocked.Exchange(ref ninaBusyFlag, 0);
                }
            });
        }

        public void Shutdown() {
            InterExposureService.NinaImage -= OnNinaImage;
            shutdown = true;
            try { calibrationCts?.Cancel(); } catch (Exception) { }
            try { guideCts?.Cancel(); } catch (Exception) { }
            Stop();
        }

        public void CancelCalibration() {
            try { calibrationCts?.Cancel(); } catch (Exception) { }
        }

        /// <param name="slewFirst">Vrai (Ctrl + clic sur Calibrer) : pointe d'abord l'équateur céleste, 1 h après le méridien.</param>
        public async Task<bool> CalibrateAsync(bool slewFirst = false) {
            if (calibrating || guiding || cameraRef == null) return false;
            await MaybeAutoGainAsync().ConfigureAwait(false);
            if (calibrating || guiding || cameraRef == null) return false;
            IGuideMount mount = Simulation ? simMount : Mount;
            if (mount == null || !mount.IsAvailable) {
                Publish(null, "Calibration impossible : la monture n'est pas connectée dans NINA (ou cochez Simulation pour tester).");
                return false;
            }

            calibrationNotice = null;
            bool ok = false;
            startMark = null;
            IsCalibrating = true;
            var camera = cameraRef;
            bool simulated = Simulation;
            try {
                await Task.Run(() => Stop());
                if (shutdown) return false;

                calibrationCts = new CancellationTokenSource();
                var token = calibrationCts.Token;
                if (slewFirst && !simulated) {
                    try {
                        await mount.SlewToCalibrationFieldAsync(text => Publish(null, "Calibration : " + text), token);
                    } catch (OperationCanceledException) {
                        throw;
                    } catch (Exception ex) {
                        Logger.Warning("InternalGuider: pointage avant calibration impossible : " + ex.Message);
                        GuideLog.AppendCalibration("Pointage avant calibration impossible : " + ex.Message);
                        calibrationNotice = "Calibration non lancée : " + ex.Message;
                        Publish(null, calibrationNotice);
                        return false;
                    }
                    GuideLog.AppendCalibration("Pointage préalable : équateur céleste, 1 h après le méridien.");
                }
                var engine = new CalibrationEngine(
                    ct => CaptureAsync(camera, ct), mount,
                    text => Publish(null, (simulated ? "SIMULATION · " : "") + "Calibration : " + text),
                    r => { CaptureStartMark(r); Publish(BuildImage(r), null); });

                var data = await engine.RunAsync(PixelScale, GuiderSettings.FocalLengthMm, simulated, token);
                CalibrationData.Save(data);
                ShowCalibrationReport(data, true);
                ok = true;
                Logger.Info("InternalGuider: calibration terminée : " + data.Summary());
                GuideLog.AppendCalibration((simulated ? "SIMULATION · " : "") + "Calibration terminée : " + data.Summary());
                calibrationNotice = (simulated ? "SIMULATION · " : "") + "Calibration terminée : " + data.Summary();
                Publish(null, calibrationNotice);
            } catch (OperationCanceledException) {
                Publish(null, "Calibration annulée.");
            } catch (CalibrationException ex) {
                Logger.Warning("InternalGuider: calibration échouée : " + ex.Message);
                GuideLog.AppendCalibration("Calibration échouée : " + ex.Message);
                calibrationNotice = "Calibration échouée : " + ex.Message;
                Publish(null, calibrationNotice);
            } catch (Exception ex) {
                Logger.Error(ex);
                Publish(null, "Calibration : erreur : " + ex.Message);
            } finally {
                calibrationCts?.Dispose();
                calibrationCts = null;
                startMark = null;
                IsCalibrating = false;
                if (!shutdown) Start(camera);
            }
            return ok;
        }

        // --- Assistant de guidage ---

        /// <summary>Relit les réglages enregistrés (après le chargement d'un fichier de configuration) et rafraîchit l'affichage.</summary>
        public void ReloadSettings() {
            guideParameters.AggressivenessRa = (float)(GuiderSettings.AggressivenessRaPercent / 100.0);
            guideParameters.AggressivenessDec = (float)(GuiderSettings.AggressivenessDecPercent / 100.0);
            guideParameters.HysteresisRa = (float)(GuiderSettings.HysteresisRaPercent / 100.0);
            guideParameters.HysteresisDec = (float)(GuiderSettings.HysteresisDecPercent / 100.0);
            guideParameters.DecMode = GuiderSettings.DecMode;
            guideParameters.DecModeApplied = GuiderSettings.DecMode;
            guideParameters.MinMovePx = (float)GuiderSettings.MinMovePx;
            guideParameters.MaxRaMs = GuiderSettings.MaxRaMs;
            guideParameters.MaxDecMs = GuiderSettings.MaxDecMs;
            guideParameters.DecBacklashMs = GuiderSettings.DecBacklashMs;
            graphInArcsec = GuiderSettings.GraphInArcsec && PixelScale > 0;
            RaisePropertyChanged(string.Empty);   // tous les champs liés sont relus
            RefreshGraph();
        }

        /// <summary>Reprend la compensation du jeu Dec des réglages (modifiable pendant le guidage).</summary>
        public void ReloadBacklash() { guideParameters.DecBacklashMs = GuiderSettings.DecBacklashMs; }

        /// <summary>Reprend le mouvement minimal des réglages (au démarrage, après réglage dans les paramètres avancés ou l'assistant).</summary>
        public void ApplyMinMove() {
            guideParameters.MinMovePx = (float)GuiderSettings.MinMovePx;
        }

        /// <summary>
        /// Mesure sans correction (dérive, seeing, erreur périodique, alignement polaire) et, si demandé, du jeu en Dec ; renvoie le rapport
        /// et des réglages suggérés. Lève CalibrationException si la mesure est impossible, OperationCanceledException si elle est interrompue.
        /// </summary>
        public async Task<AssistantResult> RunAssistantAsync(int durationSec, bool measureBacklash, Action<string, double> progress) {
            if (calibrating || guiding || cameraRef == null) throw new CalibrationException("une calibration ou un guidage est en cours, ou la caméra de guidage n'est pas connectée.");
            IGuideMount mount = Simulation ? simMount : Mount;
            if (mount == null || !mount.IsAvailable) throw new CalibrationException("la monture n'est pas connectée dans NINA (ou cochez Simulation pour tester).");
            string problem = CheckPrerequisites(mount, out var cal);
            if (problem != null) throw new CalibrationException(problem);

            var camera = cameraRef;
            bool simulated = Simulation;
            AssistantResult result = null;
            IsCalibrating = true;
            try {
                await Task.Run(() => Stop());
                if (shutdown) return null;
                if (simulated) simMount.SetDrift(true);
                calibrationCts = new CancellationTokenSource();
                var token = calibrationCts.Token;

                string context = $"Version du plugin : {typeof(PreviewViewModel).Assembly.GetName().Version}\n" +
                                 $"Début : {DateTime.Now:yyyy-MM-dd HH:mm:ss}{(simulated ? " · SIMULATION" : "")}\n" +
                                 $"Caméra : {camera.Name} · pose {Exposure:0.###} s\n" +
                                 $"Focale : {GuiderSettings.FocalLengthMm:0.#} mm · échelle {(PixelScale > 0 ? PixelScale.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " arcsec/px" : "inconnue")}\n" +
                                 $"Monture : côté du pier {mount.PierSide ?? "inconnu"} · déclinaison {(mount.DeclinationDegrees.HasValue ? mount.DeclinationDegrees.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "°" : "inconnue")}\n" +
                                 $"Étoiles : {(GuiderSettings.UseMultiStar ? "multi-étoiles, maximum " + GuiderSettings.MultiStarCount : "une seule étoile")}\n" +
                                 $"Calibration : {cal.Summary().Replace('\n', ' ')}";

                var assistant = new GuidingAssistant(
                    ct => CaptureAsync(camera, ct), mount, cal, PixelScale,
                    GuiderSettings.UseMultiStar ? GuiderSettings.MultiStarCount : 1,
                    (text, fraction) => {
                        Publish(null, (simulated ? "SIMULATION · " : "") + "Assistant : " + text);
                        progress?.Invoke(text, fraction);
                    },
                    r => Publish(BuildImage(r), null));
                result = await assistant.RunAsync(durationSec, measureBacklash, context, token);
                Logger.Info("InternalGuider: assistant de guidage terminé");
                GuideLog.AppendCalibration((simulated ? "SIMULATION · " : "") + "Assistant de guidage : " + result.Report.Replace('\r', ' ').Replace('\n', ' '));
                Publish(null, "Assistant de guidage terminé.");
            } catch (OperationCanceledException) {
                Publish(null, "Assistant de guidage interrompu.");
                throw;
            } catch (CalibrationException ex) {
                Logger.Warning("InternalGuider: assistant de guidage : " + ex.Message);
                GuideLog.AppendCalibration("Assistant de guidage : impossible : " + ex.Message);
                Publish(null, "Assistant de guidage : " + ex.Message);
                throw;
            } finally {
                if (simulated) simMount.SetDrift(false);
                calibrationCts?.Dispose();
                calibrationCts = null;
                IsCalibrating = false;
                if (!shutdown) Start(camera);
            }
            return result;
        }

        /// <summary>Applique les réglages suggérés par l'assistant (et les mémorise).</summary>
        public void ApplyAssistantResult(AssistantResult r) {
            if (r == null) return;
            if (r.MinMovePx.HasValue) { GuiderSettings.MinMovePx = r.MinMovePx.Value; ApplyMinMove(); }
            if (r.MaxRaMs.HasValue) MaxRaDurationText = r.MaxRaMs.Value.ToString();
            if (r.MaxDecMs.HasValue) MaxDecDurationText = r.MaxDecMs.Value.ToString();
            if (r.DecBacklashMs.HasValue) { GuiderSettings.DecBacklashMs = r.DecBacklashMs.Value; guideParameters.DecBacklashMs = r.DecBacklashMs.Value; }
            if (r.DecMode.HasValue) SelectedDecMode = DecModeOptions[Math.Max(0, Math.Min(2, r.DecMode.Value))];
        }

        // --- Guidage ---

        private static readonly double[] BaseAggressivenessChoices = { 5, 10, 15, 20, 25, 30, 40, 50, 60, 70, 80, 100 };

        public System.Collections.ObjectModel.ObservableCollection<double> AggressivenessChoices { get; } =
            new System.Collections.ObjectModel.ObservableCollection<double>(BaseAggressivenessChoices);

        /// <summary>Ajoute à la liste déroulante une valeur d'agressivité qui n'y figure pas (réglée par le mode IA), pour qu'elle s'affiche.</summary>
        private void EnsureAggressivenessChoice(double v) {
            foreach (var c in AggressivenessChoices) if (Math.Abs(c - v) < 0.05) return;
            int i = 0;
            while (i < AggressivenessChoices.Count && AggressivenessChoices[i] < v) i++;
            AggressivenessChoices.Insert(i, v);
        }

        /// <summary>Met à jour les champs affichés après une modification faite par le mode IA (ou leur restauration en fin de guidage).</summary>
        private void RefreshTunedDisplay(bool trimChoices) {
            Application.Current?.Dispatcher.BeginInvoke(new Action(() => {
                double ra = Math.Round(guideParameters.AggressivenessRa * 100.0, 0), dec = Math.Round(guideParameters.AggressivenessDec * 100.0, 0);
                if (trimChoices) {
                    for (int i = AggressivenessChoices.Count - 1; i >= 0; i--) {
                        double c = AggressivenessChoices[i];
                        if (Array.IndexOf(BaseAggressivenessChoices, c) < 0 && Math.Abs(c - ra) > 0.05 && Math.Abs(c - dec) > 0.05) AggressivenessChoices.RemoveAt(i);
                    }
                }
                EnsureAggressivenessChoice(ra);
                EnsureAggressivenessChoice(dec);
                RaisePropertyChanged(nameof(AggressivenessRaPercent));
                RaisePropertyChanged(nameof(AggressivenessDecPercent));
                RaisePropertyChanged(nameof(HysteresisRaText));
                RaisePropertyChanged(nameof(HysteresisDecText));
            }));
        }

        /// <summary>Fin du guidage en mode IA : les réglages validés deviennent ceux de l'utilisateur (enregistrés) ; sinon on reprend les siens.</summary>
        private void KeepOrRestoreTuning(TuneSet kept) {
            if (kept != null) {
                GuiderSettings.AggressivenessRaPercent = Math.Round(kept.AggrRa * 100.0);
                GuiderSettings.AggressivenessDecPercent = Math.Round(kept.AggrDec * 100.0);
                GuiderSettings.HysteresisRaPercent = Math.Round(kept.HystRa * 100.0);
                GuiderSettings.HysteresisDecPercent = Math.Round(kept.HystDec * 100.0);
                GuiderSettings.MinMovePx = Math.Round(kept.MinMove, 2);
                GuiderSettings.IntegralDecPercent = Math.Round(kept.IntegDec * 100.0);
            }
            RestoreUserTuning();
        }

        /// <summary>Reprend les réglages enregistrés de l'utilisateur.</summary>
        private void RestoreUserTuning() {
            guideParameters.AggressivenessRa = (float)(GuiderSettings.AggressivenessRaPercent / 100.0);
            guideParameters.AggressivenessDec = (float)(GuiderSettings.AggressivenessDecPercent / 100.0);
            guideParameters.HysteresisRa = (float)(GuiderSettings.HysteresisRaPercent / 100.0);
            guideParameters.HysteresisDec = (float)(GuiderSettings.HysteresisDecPercent / 100.0);
            guideParameters.MinMovePx = (float)GuiderSettings.MinMovePx;
            guideParameters.IntegralDec = (float)(GuiderSettings.IntegralDecPercent / 100.0);
            RefreshTunedDisplay(true);
        }

        /// <summary>Agressivité RA en pourcentage (modifiable pendant le guidage).</summary>
        public double AggressivenessRaPercent {
            get => Math.Round(guideParameters.AggressivenessRa * 100.0, 0);
            set { guideParameters.AggressivenessRa = (float)(value / 100.0); GuiderSettings.AggressivenessRaPercent = value; RaisePropertyChanged(); }
        }

        public double AggressivenessDecPercent {
            get => Math.Round(guideParameters.AggressivenessDec * 100.0, 0);
            set { guideParameters.AggressivenessDec = (float)(value / 100.0); GuiderSettings.AggressivenessDecPercent = value; RaisePropertyChanged(); }
        }

        /// <summary>Durée maximale d'une impulsion de correction en RA (ms), 250 par défaut.</summary>
        public string MaxRaDurationText {
            get => guideParameters.MaxRaMs.ToString();
            set {
                if (int.TryParse((value ?? "").Trim(), out var ms) && ms >= 10) { guideParameters.MaxRaMs = Math.Min(ms, 10000); GuiderSettings.MaxRaMs = guideParameters.MaxRaMs; }
                RaisePropertyChanged();   // réaffiche la valeur retenue
            }
        }

        public string MaxDecDurationText {
            get => guideParameters.MaxDecMs.ToString();
            set {
                if (int.TryParse((value ?? "").Trim(), out var ms) && ms >= 10) { guideParameters.MaxDecMs = Math.Min(ms, 10000); GuiderSettings.MaxDecMs = guideParameters.MaxDecMs; }
                RaisePropertyChanged();
            }
        }

        /// <summary>Hystérésis RA en % (0 = aucune), comme dans PHD2.</summary>
        public string HysteresisRaText {
            get => Math.Round(guideParameters.HysteresisRa * 100.0, 0).ToString();
            set {
                if (double.TryParse((value ?? "").Trim(), out var h) && h >= 0 && h <= 90) {
                    guideParameters.HysteresisRa = (float)(h / 100.0);
                    GuiderSettings.HysteresisRaPercent = h;
                }
                RaisePropertyChanged();
            }
        }

        public string HysteresisDecText {
            get => Math.Round(guideParameters.HysteresisDec * 100.0, 0).ToString();
            set {
                if (double.TryParse((value ?? "").Trim(), out var h) && h >= 0 && h <= 90) {
                    guideParameters.HysteresisDec = (float)(h / 100.0);
                    GuiderSettings.HysteresisDecPercent = h;
                }
                RaisePropertyChanged();
            }
        }

        public IList<string> DecModeOptions { get; } = new List<string> { "Auto (Nord et Sud)", "Nord seul", "Sud seul" };

        /// <summary>Sens des corrections en Dec (Auto, Nord seul, Sud seul).</summary>
        public string SelectedDecMode {
            get => DecModeOptions[Math.Max(0, Math.Min(2, guideParameters.DecMode))];
            set {
                int i = DecModeOptions.IndexOf(value);
                if (i < 0) return;
                guideParameters.DecMode = i;
                GuiderSettings.DecMode = i;
                // Le sens choisi se rapporte au côté du pier actuel : on note le sens de rotation RA → Dec de sa calibration
                var calNow = CalibrationData.LoadFor(CalibrationData.NormalizeSide(Mount?.PierSide));
                GuiderSettings.DecModeHandedness = i == 0 ? 0 : (calNow?.GetHandedness() ?? 0);
                guideParameters.DecModeApplied = i;
                RaisePropertyChanged();
            }
        }

        public bool GuideDec {
            get => guideParameters.GuideDec;
            set { guideParameters.GuideDec = value; RaisePropertyChanged(); }
        }

        public bool IsGuiding {
            get => guiding;
            private set {
                guiding = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(CanAutoGain));
                RaisePropertyChanged(nameof(CanCalibrate));
                RaisePropertyChanged(nameof(CanGuide));
                RaisePropertyChanged(nameof(CanStop));
            }
        }

        public bool CanGuide => !calibrating && !guiding;

        /// <summary>Vrai pendant une calibration ou un guidage (le bouton Stop arrête ce qui est en cours).</summary>
        public bool CanStop => calibrating || guiding;

        /// <summary>Arrête la calibration si elle est en cours, sinon le guidage.</summary>
        public void StopCurrent() {
            if (calibrating) CancelCalibration();
            else if (guiding) StopGuiding();
        }

        public ImageSource GraphImage {
            get => graphImage;
            private set { graphImage = value; RaisePropertyChanged(); }
        }

        public string GuideStats {
            get => guideStats;
            private set { guideStats = value; RaisePropertyChanged(); }
        }

        /// <summary>Vérifie qu'une calibration utilisable existe. Renvoie le problème (texte) ou null si tout va bien.</summary>
        private string CheckPrerequisites(IGuideMount mount, out CalibrationData cal) {
            string side = Simulation ? null : CalibrationData.NormalizeSide(mount.PierSide);
            cal = side != null ? CalibrationData.LoadFor(side) : CalibrationData.Load();
            if (cal == null) {
                return side != null && CalibrationData.Load() != null
                    ? $"aucune calibration pour ce côté du pier ({side}) : calibrez (la vérification après retournement n'a pas pu la déduire)."
                    : "aucune calibration enregistrée : cliquez d'abord sur Calibrer.";
            }
            if (cal.Simulated != Simulation) {
                return cal.Simulated
                    ? "la calibration enregistrée a été faite en simulation : recalibrez avec la vraie monture."
                    : "la calibration enregistrée vient de la vraie monture : recalibrez en simulation (ou décochez Simulation).";
            }
            if (Math.Abs(cal.FocalLengthMm - GuiderSettings.FocalLengthMm) > 0.5) {
                return $"la focale a changé depuis la calibration ({cal.FocalLengthMm:0.#} → {GuiderSettings.FocalLengthMm:0.#} mm) : recalibrez.";
            }
            return null;
        }

        /// <summary>
        /// Après un retournement au méridien : s'il n'y a pas de calibration pour le côté actuel du pier mais qu'il y en a une
        /// pour l'autre côté, la déduit après une courte vérification (une impulsion par axe). En cas d'échec, rien n'est
        /// enregistré : le guidage lancera une calibration complète.
        /// </summary>
        private async Task EnsureCalibrationForSideAsync(IGuideMount mount, Action<string> progress, CancellationToken ct) {
            if (Simulation || calibrating || guiding || cameraRef == null) return;
            string side = CalibrationData.NormalizeSide(mount.PierSide);
            if (side == null || CalibrationData.LoadFor(side) != null) return;
            var source = CalibrationData.Load();
            if (source == null || source.Simulated || CalibrationData.NormalizeSide(source.PierSide) == null ||
                CalibrationData.NormalizeSide(source.PierSide) == side) return;
            if (Math.Abs(source.FocalLengthMm - GuiderSettings.FocalLengthMm) > 0.5) return;

            progress?.Invoke("retournement détecté : vérification de la calibration…");
            var camera = cameraRef;
            IsCalibrating = true;
            try {
                await Task.Run(() => Stop());
                if (shutdown) return;
                calibrationCts = new CancellationTokenSource();
                using (ct.Register(() => { try { calibrationCts?.Cancel(); } catch (Exception) { } })) {
                    var engine = new CalibrationEngine(
                        c => CaptureAsync(camera, c), mount,
                        text => Publish(null, "Retournement : " + text),
                        r => Publish(BuildImage(r), null));
                    engine.Log = t => { Logger.Info("InternalGuider: " + t); GuideLog.AppendCalibration("Retournement : " + t); };
                    var derived = await engine.DeriveForFlipAsync(source, mount.PierSide, calibrationCts.Token);
                    if (derived != null) {
                        CalibrationData.Save(derived);
                        ShowCalibrationReport(derived, false);
                        Logger.Info("InternalGuider: calibration déduite après retournement : " + derived.Summary());
                        GuideLog.AppendCalibration("Retournement : " + derived.Summary());
                        Publish(null, "Retournement : " + derived.Warnings);
                    } else {
                        Logger.Warning("InternalGuider: vérification après retournement impossible, calibration complète nécessaire");
                        Publish(null, "Retournement : vérification impossible, calibration complète nécessaire.");
                    }
                }
            } catch (OperationCanceledException) {
                // annulé
            } catch (CalibrationException ex) {
                Logger.Warning("InternalGuider: vérification après retournement échouée : " + ex.Message);
                Publish(null, "Retournement : " + ex.Message);
            } catch (Exception ex) {
                Logger.Error(ex);
            } finally {
                calibrationCts?.Dispose();
                calibrationCts = null;
                IsCalibrating = false;
                if (!shutdown) Start(camera);
            }
        }

        // --- Pilotage depuis NINA (séquenceur, boutons de guidage) ---

        /// <summary>Un échantillon de guidage est disponible (sert à alimenter le graphe de NINA).</summary>
        public event Action<GuideSample> SampleAvailable;

        public async Task<bool> StartExternalGuidingAsync(bool forceCalibration, Action<string> progress, CancellationToken ct) {
            if (guiding) {
                // Déjà en guidage : on répond vrai, avec une courte pause pour ne pas saturer NINA si le séquenceur boucle sur cet appel.
                await Task.Delay(1000, ct);
                return true;
            }
            IGuideMount mount = Simulation ? simMount : Mount;
            if (mount == null || !mount.IsAvailable) {
                progress?.Invoke("la monture n'est pas connectée dans NINA");
                return false;
            }

            if (!forceCalibration) await EnsureCalibrationForSideAsync(mount, progress, ct);

            string problem = CheckPrerequisites(mount, out var cal);
            if (forceCalibration || problem != null) {
                progress?.Invoke("calibration en cours…");
                if (!await CalibrateAsync()) return false;
                problem = CheckPrerequisites(mount, out cal);
                if (problem != null) {
                    Publish(null, "Guidage impossible : " + problem);
                    return false;
                }
            }
            ct.ThrowIfCancellationRequested();

            lockedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            guideTask = GuideAsync();
            using (ct.Register(StopGuiding)) {
                if (!await lockedTcs.Task) return false;
                var engine = activeEngine;
                if (engine == null) return false;

                progress?.Invoke("stabilisation du guidage…");
                var cfg = ReadGuideSettings();
                bool settled = await engine.DitherAsync(0, false, cfg.SettlePx, cfg.SettleSec, cfg.TimeoutSec, ct);
                if (!settled && !ct.IsCancellationRequested) {
                    Logger.Warning("InternalGuider: le guidage ne s'est pas stabilisé dans le délai imparti (il continue)");
                }
                return guiding;
            }
        }

        /// <summary>Position verrouillée actuelle en coordonnées du capteur (null si le guidage n'est pas actif).</summary>
        public (double X, double Y)? GetLockPosition() {
            var engine = activeEngine;
            return guiding && engine != null ? (engine.LockX + 0.5, engine.LockY + 0.5) : ((double, double)?)null;
        }

        public async Task StopExternalGuidingAsync() {
            StopGuiding();
            var task = guideTask;
            if (task != null) {
                try { await Task.WhenAny(task, Task.Delay(8000)); } catch (Exception) { }
            }
        }

        public Task<bool> DitherExternalAsync(CancellationToken ct) {
            var engine = activeEngine;
            if (!guiding || engine == null) return Task.FromResult(false);
            var cfg = ReadGuideSettings();
            guideLog?.Event($"dither demandé par NINA : {cfg.Dither:0.#} px, stabilisation sous {cfg.SettlePx:0.#} px pendant {cfg.SettleSec:0.#} s, délai {cfg.TimeoutSec:0} s");
            return engine.DitherAsync(cfg.Dither, cfg.RaOnly, cfg.SettlePx, cfg.SettleSec, cfg.TimeoutSec, ct);
        }

        public void StopGuiding() {
            guideLog?.Event("arrêt demandé");
            try { guideCts?.Cancel(); } catch (Exception) { }
        }

        /// <summary>
        /// Bouton Guider : si la calibration manque ou n'est plus valable, demande (via confirm, qui reçoit la raison) s'il faut
        /// calibrer d'abord ; si oui, calibre puis lance le guidage.
        /// </summary>
        public async Task GuideOrCalibrateAsync(Func<string, bool> confirm) {
            if (calibrating || guiding || cameraRef == null) return;
            IGuideMount mount = Simulation ? simMount : Mount;
            if (mount != null && mount.IsAvailable) {
                await EnsureCalibrationForSideAsync(mount, null, CancellationToken.None);
                string problem = CheckPrerequisites(mount, out _);
                if (problem != null) {
                    if (!confirm(problem)) return;
                    if (!await CalibrateAsync()) return;
                }
            }
            await GuideAsync();
        }

        public async Task GuideAsync() {
            if (calibrating || guiding || cameraRef == null) return;
            await MaybeAutoGainAsync().ConfigureAwait(false);
            if (calibrating || guiding || cameraRef == null) return;
            IGuideMount mount = Simulation ? simMount : Mount;
            if (mount == null || !mount.IsAvailable) {
                Publish(null, "Guidage impossible : la monture n'est pas connectée dans NINA (ou cochez Simulation pour tester).");
                return;
            }

            await EnsureCalibrationForSideAsync(mount, null, CancellationToken.None);

            string problem = CheckPrerequisites(mount, out var cal);
            if (problem != null) {
                calibrationNotice = null;
                Publish(null, "Guidage impossible : " + problem);
                return;
            }

            calibrationNotice = null;
            samples.Clear();
            sumRa = sumDec = linRa = linDec = 0;
            startMark = null;
            IsGuiding = true;
            GuideActivity.Guiding = true;
            var camera = cameraRef;
            bool simulated = Simulation;
            string tag = simulated ? "SIMULATION · " : "";
            try {
                await Task.Run(() => Stop());
                if (shutdown) return;

                if (simulated) simMount.SetDrift(true);
                guideParameters.DecModeApplied = ResolveDecMode(cal, out string decNote);
                guideParameters.IntegralDec = (float)(GuiderSettings.IntegralDecPercent / 100.0);
                guideParameters.MinMovePx = (float)GuiderSettings.MinMovePx;
                guideParameters.AggressivenessRa = (float)(GuiderSettings.AggressivenessRaPercent / 100.0);
                guideParameters.AggressivenessDec = (float)(GuiderSettings.AggressivenessDecPercent / 100.0);
                guideParameters.HysteresisRa = (float)(GuiderSettings.HysteresisRaPercent / 100.0);
                guideParameters.HysteresisDec = (float)(GuiderSettings.HysteresisDecPercent / 100.0);
                guideParameters.PostFlipMaxDecMs = GuiderSettings.PostFlipMaxDecMs;
                guideParameters.DecBacklashMs = GuiderSettings.DecBacklashMs;
                guideParameters.PecApply = GuiderSettings.PecApply;
                PecStore.Simulation = simulated;   // en simulation, la courbe PEC est mémorisée à part
                guideParameters.PecRecord = GuiderSettings.PecRecord;
                guideParameters.PecStrength = (float)(GuiderSettings.PecStrengthPercent / 100.0);
                guideParameters.PixelScale = PixelScale;
                guideParameters.AiMode = GuiderSettings.AiMode;
                guideParameters.AiLearnMin = GuiderSettings.AiLearnMin;
                guideParameters.AiEvalMin = GuiderSettings.AiEvalMin;
                guideParameters.AiRetuneMin = GuiderSettings.AiRetuneMin;
                string sideNow = CalibrationData.NormalizeSide(mount.PierSide);
                guideParameters.PostFlipBoost = !simulated && sideNow != null && lastGuidedSide != null && sideNow != lastGuidedSide;
                if (sideNow != null) lastGuidedSide = sideNow;
                guideParameters.MultiStarMax = GuiderSettings.UseMultiStar ? GuiderSettings.MultiStarCount : 1;
                guideParameters.CloudPause = GuiderSettings.CloudPauseEnabled;
                guideParameters.BackupStar = GuiderSettings.BackupStarEnabled;
                guideParameters.CloudFluxRatio = GuiderSettings.CloudFluxPercent / 100f;
                guideParameters.LostTimeoutSec = GuiderSettings.LostTimeoutMin * 60;
                guideCts = new CancellationTokenSource();
                var engine = new GuideEngine(
                    ct => CaptureAsync(camera, ct), mount, cal, guideParameters,
                    text => Publish(null, tag + "Guidage : " + text),
                    r => {
                        // Repère de départ = position verrouillée au démarrage (hors dither)
                        if (startMark == null) { var en = activeEngine; if (en != null && en.LockX > 0) startMark = Tuple.Create(en.LockX + 0.5, en.LockY + 0.5); }
                        Publish(BuildImage(r), null);
                    },
                    OnGuideSample,
                    () => lockedTcs?.TrySetResult(true));
                activeEngine = engine;
                engine.ParametersChanged += () => RefreshTunedDisplay(false);

                lastLoggedParams = null;
                guideLog = GuideLog.TryCreate(BuildLogHeader(mount, cal, camera));
                if (guideLog != null) {
                    Logger.Info("InternalGuider: journal de guidage : " + guideLog.Path);
                    var log = guideLog;
                    engine.LogEvent += text => log.Event(text);
                    log.Event("guidage démarré");
                }
                await engine.RunAsync(guideCts.Token);
            } catch (OperationCanceledException) {
                // arrêt demandé
            } catch (CalibrationException ex) {
                Logger.Warning("InternalGuider: guidage arrêté : " + ex.Message);
                calibrationNotice = "Guidage arrêté : " + ex.Message;
            } catch (Exception ex) {
                Logger.Error(ex);
                calibrationNotice = "Guidage : erreur : " + ex.Message;
            } finally {
                var closingLog = guideLog;
                guideLog = null;
                if (closingLog != null) {
                    closingLog.Event("guidage terminé" + (calibrationNotice != null ? " : " + calibrationNotice : "") +
                                     (samples.Count > 0 ? " · " + StatsText(true) : ""));
                    closingLog.Dispose();
                }
                if (simulated) simMount.SetDrift(false);
                if (activeEngine != null && activeEngine.PecRecordCompleted) GuiderSettings.PecRecord = false;
                if (guideParameters.AiMode) KeepOrRestoreTuning(activeEngine?.AiRetained);
                activeEngine = null;
                pecText = "";
                aiText = "";
                lockedTcs?.TrySetResult(false);
                guideCts?.Dispose();
                guideCts = null;
                startMark = null;
                GuideActivity.Guiding = false;
                IsGuiding = false;
                if (samples.Count > 0) {
                    calibrationNotice = (calibrationNotice != null ? calibrationNotice + " · " : "") + $"{tag}Guidage terminé · " + StatsText(true);
                    Logger.Info("InternalGuider: " + calibrationNotice);
                }
                if (calibrationNotice != null) Publish(null, calibrationNotice);
                if (!shutdown) Start(camera);
            }
        }

        private void OnGuideSample(GuideSample s) {
            lock (samples) {
                samples.Add(s);
                sumRa += s.ErrRaPx * s.ErrRaPx;
                sumDec += s.ErrDecPx * s.ErrDecPx;
                linRa += s.ErrRaPx;
                linDec += s.ErrDecPx;
            }
            var log = guideLog;
            if (log != null) {
                string p = ParametersText();
                if (p != lastLoggedParams) {
                    lastLoggedParams = p;
                    log.Event("réglages : " + p);
                }
                log.Sample(s);
            }
            RefreshGraph();
            try { SampleAvailable?.Invoke(s); } catch (Exception ex) { Logger.Error(ex); }
        }

        private void RefreshGraph() {
            ImageSource graph;
            string text;
            lock (samples) {
                int size = HistoryChoices[historyIndex];
                int from = Math.Max(0, samples.Count - size);
                graph = BuildGraph(samples.GetRange(from, samples.Count - from), size);
                text = StatsText();
            }
            Application.Current?.Dispatcher.BeginInvoke(new Action(() => {
                GraphImage = graph;
                GuideStats = text;
            }));
        }

        // --- Réglages du graphe (trois boutons, comme dans l'onglet Guidage de NINA) ---

        public string GraphUnitText => graphInArcsec ? "Unité : arcsec" : "Unité : pixels";

        public IList<int> HistoryOptions { get; } = new List<int>(HistoryChoices);
        public IList<double> ScaleOptions { get; } = new List<double>(ScaleChoices);

        /// <summary>Nombre de corrections affichées dans le graphe.</summary>
        public int SelectedHistory {
            get => HistoryChoices[historyIndex];
            set {
                int i = Array.IndexOf(HistoryChoices, value);
                if (i < 0 || i == historyIndex) return;
                historyIndex = i;
                GraphSettingsChanged();
            }
        }

        /// <summary>Demi-amplitude verticale du graphe, dans l'unité affichée.</summary>
        public double SelectedScale {
            get => ScaleChoices[scaleIndex];
            set {
                int i = Array.IndexOf(ScaleChoices, value);
                if (i < 0 || i == scaleIndex) return;
                scaleIndex = i;
                GraphSettingsChanged();
            }
        }

        public string GraphScaleUnit => graphInArcsec ? "″" : "px";

        public PreviewViewModel() {
            guideParameters.AggressivenessRa = (float)(GuiderSettings.AggressivenessRaPercent / 100.0);
            guideParameters.AggressivenessDec = (float)(GuiderSettings.AggressivenessDecPercent / 100.0);
            guideParameters.HysteresisRa = (float)(GuiderSettings.HysteresisRaPercent / 100.0);
            guideParameters.HysteresisDec = (float)(GuiderSettings.HysteresisDecPercent / 100.0);
            guideParameters.DecMode = GuiderSettings.DecMode;
            guideParameters.MinMovePx = (float)GuiderSettings.MinMovePx;
            guideParameters.MaxRaMs = GuiderSettings.MaxRaMs;
            guideParameters.MaxDecMs = GuiderSettings.MaxDecMs;
            RefreshGraph();   // graduations visibles dès l'ouverture
        }

        private void GraphSettingsChanged() {
            RaisePropertyChanged(nameof(GraphUnitText));
            RaisePropertyChanged(nameof(GraphScaleUnit));
            RaisePropertyChanged(nameof(SelectedHistory));
            RaisePropertyChanged(nameof(SelectedScale));
            RefreshGraph();
        }

        /// <summary>Efface le graphe et les statistiques (RMS) affichés ; le guidage en cours n'est pas interrompu.</summary>
        public void ClearGraph() {
            lock (samples) {
                samples.Clear();
                sumRa = 0;
                sumDec = 0;
                linRa = 0;
                linDec = 0;
            }
            RefreshGraph();
        }

        public void ToggleGraphUnit() {
            if (!graphInArcsec && PixelScale <= 0) return;   // pas d'échelle en arcsec/pixel connue
            graphInArcsec = !graphInArcsec;
            GuiderSettings.GraphInArcsec = graphInArcsec;
            // Garde une échelle de même ordre de grandeur dans la nouvelle unité
            scaleIndex = graphInArcsec ? Math.Min(ScaleChoices.Length - 1, scaleIndex + 1) : Math.Max(0, scaleIndex - 1);
            GraphSettingsChanged();
        }

        private string ParametersText() =>
            $"agressivité RA {guideParameters.AggressivenessRa * 100:0.#} %, Dec {guideParameters.AggressivenessDec * 100:0.#} % · " +
            $"hystérésis RA {guideParameters.HysteresisRa * 100:0} %, Dec {guideParameters.HysteresisDec * 100:0} % · " +
            $"max RA {guideParameters.MaxRaMs} ms, Dec {guideParameters.MaxDecMs} ms · " +
            $"mouvement minimal {guideParameters.MinMovePx:0.##} px · guidage Dec {(guideParameters.GuideDec ? "oui" : "non")} · " +
            $"intégrale Dec {guideParameters.IntegralDec * 100:0.#} % · max Dec après retournement {(guideParameters.PostFlipMaxDecMs > 0 ? guideParameters.PostFlipMaxDecMs + " ms" : "désactivé")} · " +
            $"compensation jeu Dec {(guideParameters.DecBacklashMs > 0 ? guideParameters.DecBacklashMs + " ms" : "désactivée")} · " +
            $"mode IA {(guideParameters.AiMode ? "oui" : "non")} · " +
            $"PEC {(guideParameters.PecRecord ? "enregistrement de la courbe" : guideParameters.PecApply ? "appliquée à " + (guideParameters.PecStrength * 100).ToString("0") + " %" : "désactivée")} · " +
            $"sens Dec {DecModeOptions[Math.Max(0, Math.Min(2, guideParameters.DecMode))]}" +
            (guideParameters.DecModeApplied != guideParameters.DecMode ? $" (appliqué : {DecModeOptions[Math.Max(0, Math.Min(2, guideParameters.DecModeApplied))]}, inversé après retournement)" : "");

        private string decModeNote;
        private string lastGuidedSide;   // côté du pier au dernier démarrage du guidage (détecte un retournement)

        /// <summary>
        /// « Nord seul » / « Sud seul » se rapportent à une direction du ciel. Après un retournement, si le pilote de la monture
        /// n'inverse pas les impulsions Dec, une impulsion « Nord » va dans l'autre sens du ciel : le sens de rotation RA → Dec
        /// de la calibration change. On inverse alors le sens autorisé pour garder la même direction du ciel.
        /// </summary>
        private int ResolveDecMode(CalibrationData cal, out string note) {
            note = null;
            int mode = guideParameters.DecMode;
            decModeNote = null;
            if (mode == 0 || cal == null) return mode;
            int h = cal.GetHandedness();
            int reference = GuiderSettings.DecModeHandedness;
            if (h == 0) return mode;
            if (reference == 0) { GuiderSettings.DecModeHandedness = h; return mode; }
            if (h == reference) return mode;
            int inverted = mode == 1 ? 2 : 1;
            note = $"sens Dec inversé automatiquement ({DecModeOptions[mode]} → {DecModeOptions[inverted]}) : après le retournement, les impulsions Dec agissent dans l'autre sens du ciel";
            decModeNote = note;
            Logger.Info("InternalGuider: " + note);
            return inverted;
        }

        private string BuildLogHeader(IGuideMount mount, CalibrationData cal, ICamera camera) {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Internal Guider - journal de guidage");
            sb.AppendLine($"Version du plugin : {typeof(PreviewViewModel).Assembly.GetName().Version}");
            sb.AppendLine($"Début : {DateTime.Now:yyyy-MM-dd HH:mm:ss}{(Simulation ? " · SIMULATION" : "")}");
            sb.AppendLine($"Caméra : {camera.Name} · pose {Exposure:0.###} s · gain {(GainIsAdjustable(camera) ? GainKey(camera).ToString() : "non réglable")}");
            sb.AppendLine($"Focale : {GuiderSettings.FocalLengthMm:0.#} mm · échelle {(PixelScale > 0 ? PixelScale.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " arcsec/px" : "inconnue")}");
            sb.AppendLine($"Sélection de l'étoile : {(StarSelection.IsManual ? "choix manuel (souris)" : "automatique")}");
            sb.AppendLine($"Étoiles de guidage : {(guideParameters.MultiStarMax > 1 ? "multi-étoiles, maximum " + guideParameters.MultiStarMax : "une seule étoile")}");
            sb.AppendLine($"Étoile de secours : {(guideParameters.MultiStarMax > 1 ? "sans objet (multi-étoiles)" : (guideParameters.BackupStar ? "activée" : "désactivée"))}");
            sb.AppendLine($"Nuages : {(guideParameters.CloudPause ? "pause des corrections sous " + GuiderSettings.CloudFluxPercent + " % du flux" : "pas de pause")} · attente max. {(GuiderSettings.LostTimeoutMin > 0 ? GuiderSettings.LostTimeoutMin + " min" : "illimitée")}");
            sb.AppendLine($"Darks : {(GuiderSettings.UseDarkLibrary ? "bibliothèque activée" : "non utilisés")}");
            sb.AppendLine($"Monture : côté du pier {mount.PierSide ?? "inconnu"} · déclinaison {(mount.DeclinationDegrees.HasValue ? mount.DeclinationDegrees.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "°" : "inconnue")}");
            sb.AppendLine($"Calibration du {cal.Date:yyyy-MM-dd HH:mm} : {cal.Summary()}");
            sb.AppendLine($"Réglages : {ParametersText()}");
            sb.AppendLine("Convention : erreur RA positive = l'étoile s'est déplacée dans le sens d'une impulsion Ouest ; erreur Dec positive = sens Nord.");
            sb.AppendLine("Impulsion positive = Est (RA) ou Sud (Dec) ; négative = Ouest ou Nord.");
            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// RMS affiché sous l'image. Pendant le guidage : sur les dernières corrections (même fenêtre que le graphe, « Historique »),
        /// comme l'onglet Guidage de NINA, pour que les deux valeurs se suivent. Au bilan de fin : sur tout le guidage.
        /// </summary>
        private string StatsText(bool wholeSession = false) {
            double ra, dec;
            int n, total;
            lock (samples) {
                total = samples.Count;
                if (total == 0) return "";
                n = wholeSession ? total : Math.Min(total, HistoryChoices[historyIndex]);
                int from = total - n;
                double sr = 0, sd = 0, lr = 0, ld = 0;
                for (int i = from; i < total; i++) {
                    var q = samples[i];
                    sr += q.ErrRaPx * q.ErrRaPx; sd += q.ErrDecPx * q.ErrDecPx;
                    lr += q.ErrRaPx; ld += q.ErrDecPx;
                }
                // RMS autour de la moyenne (écart-type), comme PHD2 et NINA : un décalage constant n'est pas du bruit de guidage
                ra = Math.Sqrt(Math.Max(0, sr / n - (lr / n) * (lr / n)));
                dec = Math.Sqrt(Math.Max(0, sd / n - (ld / n) * (ld / n)));
            }
            double tot = Math.Sqrt(ra * ra + dec * dec);
            string Fmt(double px) => PixelScale > 0 ? $"{px:0.00} px ({px * PixelScale:0.00}″)" : $"{px:0.00} px";
            string scope = wholeSession || n == total ? $"{total} corrections" : $"{total} corrections · RMS sur les {n} dernières";
            return $"{scope} · RMS RA {Fmt(ra)} · Dec {Fmt(dec)} · total {Fmt(tot)}";
        }

        private static FormattedText Label(string text, Color color) =>
            new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 11, new SolidColorBrush(color), 1.0);

        /// <summary>Courbes de l'erreur en RA (bleu) et en Dec (rouge) sur les dernières mesures, avec graduations.</summary>
        private ImageSource BuildGraph(IList<GuideSample> data, int historySize) {
            const double W = 880;
            const double H = 170;
            const double left = 44;   // marge pour les graduations
            double unit = graphInArcsec && PixelScale > 0 ? PixelScale : 1.0;
            string suffix = graphInArcsec && PixelScale > 0 ? "″" : "px";
            double max = ScaleChoices[scaleIndex];
            double plotW = W - left;

            var group = new DrawingGroup();
            group.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(24, 24, 28)), null,
                new RectangleGeometry(new Rect(0, 0, W, H))));

            var axis = new Pen(new SolidColorBrush(Color.FromRgb(120, 120, 126)), 1);
            var faint = new Pen(new SolidColorBrush(Color.FromRgb(55, 55, 60)), 1);
            axis.Freeze();
            faint.Freeze();
            var textColor = Color.FromRgb(200, 200, 205);

            // Lignes à -max, -max/2, 0, +max/2, +max
            for (int k = -2; k <= 2; k++) {
                double v = max * k / 2.0;
                double y = H / 2 - v / max * (H / 2 - 6);
                group.Children.Add(new GeometryDrawing(null, k == 0 ? axis : faint,
                    new LineGeometry(new Point(left, y), new Point(W, y))));
                var label = Label((v > 0 ? "+" : "") + v.ToString("0.##") + " " + suffix, textColor);
                group.Children.Add(new GeometryDrawing(new SolidColorBrush(textColor), null,
                    label.BuildGeometry(new Point(2, Math.Min(H - label.Height, Math.Max(0, y - label.Height / 2))))));
            }

            if (data.Count >= 2) {
                AddCurve(group, data, s => s.ErrRaPx * unit, max, left, plotW, H, historySize, Color.FromRgb(74, 163, 255));
                AddCurve(group, data, s => s.ErrDecPx * unit, max, left, plotW, H, historySize, Color.FromRgb(255, 106, 90));
            }

            // Légende
            double lx = W - 120;
            foreach (var (name, col) in new[] { ("RA", Color.FromRgb(74, 163, 255)), ("Dec", Color.FromRgb(255, 106, 90)) }) {
                var l = Label(name, col);
                group.Children.Add(new GeometryDrawing(new SolidColorBrush(col), null, l.BuildGeometry(new Point(lx, 2))));
                lx += 50;
            }
            group.ClipGeometry = new RectangleGeometry(new Rect(0, 0, W, H));
            group.Freeze();
            var drawing = new DrawingImage(group);
            drawing.Freeze();
            return drawing;
        }

        private static void AddCurve(DrawingGroup group, IList<GuideSample> data, Func<GuideSample, double> value,
                                     double max, double left, double plotW, double H, int historySize, Color color) {
            var figure = new PathFigure();
            // Le graphe se remplit depuis la gauche, puis défile une fois plein
            for (int i = 0; i < data.Count; i++) {
                double x = left + plotW * i / (historySize - 1.0);
                double y = H / 2 - Math.Max(-max * 1.05, Math.Min(max * 1.05, value(data[i]))) / max * (H / 2 - 6);
                if (i == 0) figure.StartPoint = new Point(x, y);
                else figure.Segments.Add(new LineSegment(new Point(x, y), true));
            }
            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            var pen = new Pen(new SolidColorBrush(color), 1.6);
            pen.Freeze();
            group.Children.Add(new GeometryDrawing(null, pen, geometry));
        }

        /// <summary>Une pose brute de la caméra (sans détection d'étoiles).</summary>
        private async Task<(ushort[] Raw, int Width, int Height, int BitDepth)> CaptureRawAsync(ICamera camera, double exposure, CancellationToken token) {
            var sequence = new CaptureSequence(exposure, CaptureSequence.ImageTypes.SNAPSHOT,
                new FilterInfo(), new BinningMode(1, 1), 1);

            if (MonoCapture != null) {
                await WaitNinaIdleAsync(token).ConfigureAwait(false);
                CameraDiagnostic.Log($"plugin : début de pose via NINA ({exposure:0.###} s)");
                ownExposure = true;
                try {
                    var ed = await MonoCapture(sequence, token).ConfigureAwait(false);
                    if (ed != null) {
                        var img = await ed.ToImageData(null, token).ConfigureAwait(false);
                        CameraDiagnostic.Log("plugin : pose terminée");
                        return (img.Data.FlatArray, img.Properties.Width, img.Properties.Height, camera.BitDepth);
                    }
                    CameraDiagnostic.Log("plugin : CameraMediator.Capture n'a pas renvoyé d'image, pose directe sur la caméra");
                } catch (Exception ex) when (!(ex is OperationCanceledException)) {
                    CameraDiagnostic.Log($"plugin : ERREUR de pose : {ex.Message}");
                    throw;
                } finally {
                    ownExposure = false;
                }
            }

            ApplyPendingGain(camera);
            CameraDiagnostic.Log($"plugin : début de pose ({exposure:0.###} s)");
            try {
                await StartExposureWhenIdleAsync(camera, sequence, exposure, token).ConfigureAwait(false);
            } catch (Exception ex) {
                CameraDiagnostic.Log($"plugin : ERREUR au démarrage de la pose : {ex.Message}");
                throw;
            }
            try {
                await camera.WaitUntilExposureIsReady(token).ConfigureAwait(false);
                var exposureData = await camera.DownloadExposure(token).ConfigureAwait(false);
                if (exposureData == null) throw new InvalidOperationException("la caméra n'a pas renvoyé d'image");
                var imageData = await exposureData.ToImageData(null, token).ConfigureAwait(false);
                CameraDiagnostic.Log("plugin : pose terminée");
                return (imageData.Data.FlatArray, imageData.Properties.Width, imageData.Properties.Height, camera.BitDepth);
            } catch (OperationCanceledException) {
                // Arrêt demandé en pleine pose : on libère la caméra, sinon la pose suivante est refusée (« Camera not idle »)
                try { camera.AbortExposure(); } catch (Exception) { /* aucune pose en cours */ }
                throw;
            }
        }

        /// <summary>
        /// Lance une pose ; si le pilote répond que la caméra n'est pas au repos (pose précédente interrompue encore en cours),
        /// l'annule et réessaie pendant quelques secondes.
        /// </summary>
        private static async Task StartExposureWhenIdleAsync(ICamera camera, CaptureSequence sequence, double exposure, CancellationToken token) {
            var limit = DateTime.UtcNow.AddSeconds(exposure + 8);
            while (true) {
                try {
                    camera.StartExposure(sequence);
                    return;
                } catch (Exception ex) when (DateTime.UtcNow < limit && !token.IsCancellationRequested &&
                                             ex.Message.IndexOf("idle", StringComparison.OrdinalIgnoreCase) >= 0) {
                    CameraDiagnostic.Log("plugin : caméra non au repos (« idle »), pose annulée puis nouvel essai");
                    try { camera.AbortExposure(); } catch (Exception) { /* rien à annuler */ }
                    await Task.Delay(400, token).ConfigureAwait(false);
                }
            }
        }

        // --- Choix de l'étoile à la souris ---

        private volatile string selectionNote = "";
        private DateTime selectionNoteUntil = DateTime.MinValue;

        private void NoteSelection(string text) {
            selectionNote = text;
            selectionNoteUntil = DateTime.UtcNow.AddSeconds(8);
            Publish(null, text);
        }

        /// <summary>Clic gauche dans l'aperçu : choisit l'étoile la plus proche (coordonnées de l'image analysée, bord du pixel inclus).</summary>
        public void SelectStarAt(double imageX, double imageY) {
            if (calibrating || guiding) {
                NoteSelection("Arrêtez la calibration ou le guidage pour changer d'étoile.");
                return;
            }
            var r = lastResult;
            if (r == null) return;

            StarInfo best = null;
            double bestD = StarSelection.MatchRadiusPx * StarSelection.MatchRadiusPx;
            foreach (var s in r.Stars) {
                double dx = s.X + 0.5 - imageX, dy = s.Y + 0.5 - imageY;   // le centre d'une étoile est dessiné à (X + 0,5 ; Y + 0,5)
                double d = dx * dx + dy * dy;
                if (d <= bestD) { best = s; bestD = d; }
            }
            if (best == null) {
                NoteSelection("Aucune étoile détectée à cet endroit.");
                return;
            }
            string problem = StarSelection.Validate(best, r.Width, r.Height);
            if (problem != null) {
                NoteSelection("Étoile refusée : " + problem + ".");
                return;
            }
            StarSelection.Set(best.X, best.Y);
            NoteSelection($"Étoile choisie à la souris : x={best.X + 0.5:0.0} y={best.Y + 0.5:0.0} · SNR {best.Snr:0} · HFD {best.Hfd:0.0} px.");
        }

        /// <summary>Clic droit dans l'aperçu : retour à la sélection automatique.</summary>
        public void ClearStarSelection() {
            if (calibrating || guiding) return;
            StarSelection.Clear();
            NoteSelection("Sélection automatique de l'étoile.");
        }

        // --- Bibliothèque de darks ---

        private DarkFrame darkCache;
        private string darkCacheKey;
        private volatile string darkState = "";

        /// <summary>Identifiant de la caméra de guidage (null si aucune caméra n'est connectée).</summary>
        public string CameraKey => cameraRef?.Id;

        public string CameraName => cameraRef?.Name;

        private static int GainKey(ICamera camera) {
            try { return GainIsAdjustable(camera) ? camera.Gain : -1; } catch (Exception) { return -1; }
        }

        /// <summary>Soustrait le dark de même pose et même gain, si la bibliothèque est activée et qu'il existe.</summary>
        private void ApplyDark(ICamera camera, ushort[] raw, int width, int height, double exposure) {
            if (!GuiderSettings.UseDarkLibrary) { darkState = ""; return; }
            try {
                int gain = GainKey(camera);
                string key = $"{camera.Id}|{exposure:0.###}|{gain}|{width}x{height}|{DarkLibrary.Version}";
                if (key != darkCacheKey) {
                    darkCache = DarkLibrary.TryLoad(camera.Id, exposure, gain, width, height);
                    darkCacheKey = key;
                }
                if (darkCache != null) {
                    DarkLibrary.Apply(raw, darkCache);
                    darkState = " · dark appliqué";
                } else {
                    darkState = " · pas de dark pour cette pose/gain";
                }
            } catch (Exception ex) {
                Logger.Error(ex);
                darkState = "";
            }
        }

        /// <summary>Texte des darks disponibles pour la caméra connectée.</summary>
        public string DarkLibraryInfo() => cameraRef == null ? "" : DarkLibrary.Describe(cameraRef.Id);

        /// <summary>
        /// Prend <paramref name="count"/> poses noires pour chaque durée de pose de la liste comprise entre <paramref name="from"/> et
        /// <paramref name="to"/>, avec le gain actuel, et enregistre leur médiane. Renvoie le nombre de darks créés.
        /// Le bouton Stop (ou CancelCalibration) l'interrompt.
        /// </summary>
        public async Task<int> GenerateDarksAsync(double from, double to, int count, Action<string, double> progress, IList<int> gains = null) {
            if (calibrating || guiding || cameraRef == null) throw new InvalidOperationException("calibration ou guidage en cours, ou caméra non connectée");
            if (Simulation) throw new InvalidOperationException("indisponible en mode simulation");

            var exposures = ExposureChoices.Where(e => e >= from - 1e-9 && e <= to + 1e-9).ToList();
            if (exposures.Count == 0) throw new InvalidOperationException("aucune durée de pose dans cet intervalle");

            var camera = cameraRef;
            string cameraKey = camera.Id;
            int created = 0;
            int originalGain = GainKey(camera);
            IsCalibrating = true;
            try {
                await Task.Run(() => Stop());
                calibrationCts = new CancellationTokenSource();
                var token = calibrationCts.Token;
                var gainList = gains != null && gains.Count > 0 && GainIsAdjustable(camera) ? gains.Select(x => (int?)x).ToList() : new List<int?> { null };
                int total = exposures.Count * count * gainList.Count;
                int done = 0;

                foreach (int? targetGain in gainList) {
                    foreach (double exposure in exposures) {
                        var frames = new List<ushort[]>();
                        int width = 0, height = 0, gain = -1;
                        string gainLabel = targetGain.HasValue ? $" gain {targetGain}" : "";
                        if (targetGain.HasValue) pendingGain = targetGain.Value;
                        for (int i = 0; i < count; i++) {
                            progress?.Invoke($"Pose {exposure:0.###} s{gainLabel} : image {i + 1}/{count}…", (double)done / total);
                            var shot = await CaptureRawAsync(camera, exposure, token).ConfigureAwait(false);
                            if (i == 0) { width = shot.Width; height = shot.Height; gain = GainKey(camera); }
                            else if (shot.Width != width || shot.Height != height) throw new InvalidOperationException("la taille de l'image a changé pendant la prise");
                            frames.Add((ushort[])shot.Raw.Clone());
                            done++;
                        }
                        progress?.Invoke($"Pose {exposure:0.###} s{gainLabel} : calcul du dark maître…", (double)done / total);
                        var dark = await Task.Run(() => DarkLibrary.Combine(frames, width, height, exposure, gain), token).ConfigureAwait(false);
                        DarkLibrary.Save(cameraKey, dark);
                        created++;
                        Logger.Info($"InternalGuider: dark créé : pose {exposure:0.###} s, gain {gain}, {count} images");
                    }
                }
                progress?.Invoke("Terminé.", 1.0);
            } finally {
                if (gains != null && originalGain >= 0) pendingGain = originalGain;   // le gain réglé avant la série est rétabli
                calibrationCts?.Dispose();
                calibrationCts = null;
                IsCalibrating = false;
                if (!shutdown) Start(camera);
            }
            return created;
        }

        /// <summary>Une image : pose de la caméra, ou ciel synthétique si Simulation est cochée ; puis détection d'étoiles.</summary>
        private async Task<DetectionResult> CaptureAsync(ICamera camera, CancellationToken token) {
            double exposure = Exposure;
            ushort[] raw;
            int width;
            int height;
            int bitDepth;

            if (Simulation) {
                width = camera.CameraXSize > 0 ? camera.CameraXSize : 1304;
                height = camera.CameraYSize > 0 ? camera.CameraYSize : 976;
                bitDepth = 16;
                int frame = Interlocked.Increment(ref frameCounter);
                await Task.Delay(TimeSpan.FromSeconds(exposure), token).ConfigureAwait(false);
                // Option « Passage de nuages » : pendant un guidage simulé, des nuages passent en boucle (cycle de 100 images) pour tester la pause des corrections
                double transmission = 1.0;
                if (IsGuiding && GuiderSettings.SimulateClouds) {
                    int phase = Interlocked.Increment(ref simGuideFrames) % 100;
                    if (phase >= 40 && phase < 48) transmission = 0.02;          // nuage épais : étoiles perdues
                    else if (phase >= 55 && phase < 70) transmission = 0.3;       // voile : étoiles faibles
                }
                raw = SyntheticSky.Generate(width, height, frame, simMount.OffsetX, simMount.OffsetY, false, transmission);
            } else {
                Interlocked.Increment(ref frameCounter);
                (raw, width, height, bitDepth) = await CaptureRawAsync(camera, exposure, token).ConfigureAwait(false);
                ApplyDark(camera, raw, width, height, exposure);
                // Dernière image réelle, pour l'outil « Champ du guidage » de Planitario
                GuideFrameExport.Publish(raw, width, height, bitDepth, exposure, PixelScale);
            }

            var result = StarDetector.Analyze(raw, width, height, bitDepth);
            StarSelection.ApplyToResult(result);
            lastResult = result;
            return result;
        }

        private async Task LoopAsync(ICamera camera, CancellationToken token) {
            int errors = 0;
            while (!token.IsCancellationRequested) {
                try {
                    var result = await CaptureAsync(camera, token).ConfigureAwait(false);
                    errors = 0;
                    Publish(BuildImage(result), Describe(frameCounter, Exposure, result));
                } catch (OperationCanceledException) {
                    break;
                } catch (Exception ex) {
                    Logger.Error(ex);
                    errors++;
                    Publish(null, $"Erreur : {ex.Message}");
                    if (errors >= 3) break;
                    try { await Task.Delay(1000, token).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
                }
            }
            Logger.Info("InternalGuider: boucle d'aperçu terminée");
        }

        private string Describe(int frame, double exposure, DetectionResult r) {
            string sim = Simulation ? " · SIMULATION" : "";
            var s = r.Selected;
            string star;
            if (s == null) {
                star = "aucune étoile utilisable";
            } else {
                // Les coordonnées de l'image analysée sont décalées d'un demi-pixel par rapport au capteur.
                star = $"étoile choisie : x={s.X + 0.5:0.00} y={s.Y + 0.5:0.00} · SNR {s.Snr:0} · HFD {s.Hfd:0.0} px";
                if (PixelScale > 0) star += $" ({s.Hfd * PixelScale:0.0}″)";
            }
            string line = $"Image {frame} · pose {exposure:0.0##} s{sim} · {r.Stars.Count} étoiles · {star}{(StarSelection.IsManual ? " (choix manuel)" : "")}{darkState}";
            var notice = calibrationNotice;
            if (notice != null) line = notice + "\n" + line;
            if (DateTime.UtcNow < selectionNoteUntil) line = selectionNote + "\n" + line;
            return line;
        }

        private void Publish(ImageSource source, string text) {
            Application.Current?.Dispatcher.BeginInvoke(new Action(() => {
                if (source != null) {
                    Image = source;
                    UpdateMeridian();
                    UpdatePec();
                    RaisePropertyChanged(nameof(FwhmText));
                    if (GuiderSettings.ShowStarProfile) {
                        RaisePropertyChanged(nameof(StarZoomImage));
                        RaisePropertyChanged(nameof(StarCurveImage));
                        RaisePropertyChanged(nameof(StarHistoryImage));
                        RaisePropertyChanged(nameof(StarProfileText));
                    }
                }
                if (text != null) Status = text;
            }));
        }

        /// <summary>
        /// Image d'aperçu : fond = médiane, blanc = 99,95e centile (au moins fond + 20 x bruit), gamma 0,5,
        /// avec un cadre jaune sur chaque étoile détectée et un cadre vert à croix sur l'étoile de guidage.
        /// </summary>
        public IList<string> StretchOptions { get; } = new List<string> { "Auto", "Doux", "Fort", "Très fort" };

        /// <summary>Contraste de l'image d'aperçu (s'applique à la prochaine image) ; mémorisé.</summary>
        public string SelectedStretch {
            get => StretchOptions[GuiderSettings.StretchIndex];
            set {
                int i = StretchOptions.IndexOf(value);
                if (i < 0) return;
                GuiderSettings.StretchIndex = i;
                RaisePropertyChanged();
            }
        }

        // Repère de la position initiale de l'étoile de guidage (coordonnées image, centre du pixel), pendant la calibration et le guidage
        private volatile Tuple<double, double> startMark;

        /// <summary>Mémorise la position de l'étoile à la première image d'une calibration ou d'un guidage.</summary>
        private void CaptureStartMark(DetectionResult r) {
            if (startMark == null && r?.Selected != null) startMark = Tuple.Create(r.Selected.X + 0.5, r.Selected.Y + 0.5);
        }

        private ImageSource BuildImage(DetectionResult r) {
            try { UpdateFwhm(r); } catch (Exception) { }
            try { RefreshStarProfile(r); } catch (Exception) { }
            int gw = r.Width;
            int gh = r.Height;
            float[] gray = r.Gray;

            int step = Math.Max(1, gray.Length / 20000);
            if (step % 2 == 0) step++;
            var sample = new float[(gray.Length + step - 1) / step];
            int n = 0;
            for (int i = 0; i < gray.Length; i += step) sample[n++] = gray[i];
            Array.Sort(sample, 0, n);

            // Point noir = 0,1e centile (et non la médiane) : sur une caméra à fort dégradé de fond (Atik GP, lueur d'ampli),
            // tout ce qui est sous la médiane deviendrait noir et les bords de l'image paraîtraient assombris.
            double low = Math.Min(r.Background, sample[(int)(n * 0.001)]);
            double high = Math.Max(sample[Math.Min(n - 1, (int)(n * 0.9995))], r.Background + 20.0 * r.Noise);
            // Contraste choisi dans la fenêtre : 0 = auto, 1 = doux, 2 = fort, 3 = très fort
            double expo = 0.5;
            switch (GuiderSettings.StretchIndex) {
                case 1: high = Math.Max(high, sample[n - 1]); expo = 0.75; break;
                case 2: high = Math.Max(low + 1, Math.Min(high, r.Background + 8.0 * r.Noise)); break;
                case 3: high = Math.Max(low + 1, Math.Min(high, r.Background + 3.0 * r.Noise)); expo = 0.4; break;
            }
            double range = Math.Max(1e-6, high - low);

            var lut = new byte[4096];
            for (int i = 0; i < lut.Length; i++) lut[i] = (byte)(255.0 * Math.Pow(i / 4095.0, expo));

            var bytes = new byte[gray.Length];
            for (int i = 0; i < gray.Length; i++) {
                double v = (gray[i] - low) / range;
                int k = v <= 0 ? 0 : (v >= 1 ? 4095 : (int)(v * 4095));
                bytes[i] = lut[k];
            }

            var bitmap = BitmapSource.Create(gw, gh, 96, 96, PixelFormats.Gray8, null, bytes, gw);
            bitmap.Freeze();

            // Surimpression, dessinée en unités de pixels de l'image (l'épaisseur suit la taille de l'image)
            double t = Math.Max(1.0, gw / 450.0);
            var dim = new Pen(new SolidColorBrush(Color.FromRgb(255, 200, 0)), t);
            dim.Freeze();
            var good = new Pen(new SolidColorBrush(Color.FromRgb(0, 255, 90)), t * 1.3);
            good.Freeze();
            var also = new Pen(new SolidColorBrush(Color.FromRgb(0, 200, 120)), t * 1.1);
            also.Freeze();

            var group = new DrawingGroup();
            group.Children.Add(new ImageDrawing(bitmap, new Rect(0, 0, gw, gh)));

            double half = StarDetector.ApertureRadius + 3;
            foreach (var s in r.Stars) {
                bool selected = ReferenceEquals(s, r.Selected);
                bool alsoTracked = !selected && r.Tracked != null && r.Tracked.Contains(s);   // étoiles supplémentaires du guidage multi-étoiles
                var pen = selected ? good : (alsoTracked ? also : dim);
                double cx = s.X + 0.5;
                double cy = s.Y + 0.5;
                group.Children.Add(new GeometryDrawing(null, pen,
                    new RectangleGeometry(new Rect(cx - half, cy - half, 2 * half, 2 * half))));
                if (selected) {
                    double far = half * 2.5;
                    group.Children.Add(new GeometryDrawing(null, good, new LineGeometry(new Point(cx - far, cy), new Point(cx - half, cy))));
                    group.Children.Add(new GeometryDrawing(null, good, new LineGeometry(new Point(cx + half, cy), new Point(cx + far, cy))));
                    group.Children.Add(new GeometryDrawing(null, good, new LineGeometry(new Point(cx, cy - far), new Point(cx, cy - half))));
                    group.Children.Add(new GeometryDrawing(null, good, new LineGeometry(new Point(cx, cy + half), new Point(cx, cy + far))));
                }
            }
            // Repère de départ : petit cercle et croix, en cyan, autour de la position initiale de l'étoile
            var mark = startMark;
            if (mark != null && (calibrating || guiding)) {
                var markPen = new Pen(new SolidColorBrush(Color.FromRgb(0, 220, 255)), t);
                markPen.Freeze();
                double rr = half * 0.55;
                double arm = half * 1.6;
                group.Children.Add(new GeometryDrawing(null, markPen, new EllipseGeometry(new Point(mark.Item1, mark.Item2), rr, rr)));
                group.Children.Add(new GeometryDrawing(null, markPen, new LineGeometry(new Point(mark.Item1 - arm, mark.Item2), new Point(mark.Item1 + arm, mark.Item2))));
                group.Children.Add(new GeometryDrawing(null, markPen, new LineGeometry(new Point(mark.Item1, mark.Item2 - arm), new Point(mark.Item1, mark.Item2 + arm))));
            }
            group.Freeze();

            var drawing = new DrawingImage(group);
            drawing.Freeze();
            return drawing;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void RaisePropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
