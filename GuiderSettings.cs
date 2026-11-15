using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces;

namespace NINA.Plugin.InternalGuider {
    /// <summary>
    /// Réglages du plugin, sauvegardés dans %LOCALAPPDATA%\NINA\InternalGuider\settings.json
    /// (hors du dossier du plugin, pour survivre aux mises à jour de la DLL).
    /// Remarque : ces réglages sont communs à tous les profils NINA.
    /// </summary>
    public static class GuiderSettings {
        public const double DefaultFocalLengthMm = 255;
        public const int DefaultMaxPulseMs = 250;
        public const int DefaultMultiStarCount = 6;
        public const int DefaultCloudFluxPercent = 50;
        public const int DefaultLostTimeoutMin = 10;
        public const double DefaultAggressivenessPercent = 30;

        private class Data {
            public string GuideCameraId { get; set; }
            public double FocalLengthMm { get; set; }
            public int CalibrationStepMs { get; set; }
            public int MaxRaMs { get; set; }
            public int MaxDecMs { get; set; }
            public double AggressivenessRaPercent { get; set; }
            public double AggressivenessDecPercent { get; set; }
            public double HysteresisRaPercent { get; set; }
            public double HysteresisDecPercent { get; set; }
            public int DecMode { get; set; }
            public int DecModeHandedness { get; set; }
            public double IntegralDecPercent { get; set; }
            public double? MinMovePx { get; set; }
            public int? PostFlipMaxDecMs { get; set; }
            public int? DecBacklashMs { get; set; }
            public bool PecApply { get; set; }
            public bool AiMode { get; set; }
            public int? AiLearnMin { get; set; }
            public int? AiEvalMin { get; set; }
            public int? AiRetuneMin { get; set; }
            public bool InterExpEnabled { get; set; }
            public bool MonoCamera { get; set; }
            public bool CameraDiag { get; set; }
            public int? InterExpBudgetMs { get; set; }
            public double? InterExpRotationDeg { get; set; }
            public double? InterExpScaleArcsec { get; set; }
            public double? InterExpJumpArcsec { get; set; }
            public double? DitherPixels { get; set; }
            public bool DitherRaOnly { get; set; }
            public bool PecRecord { get; set; }
            public int? PecStrengthPercent { get; set; }
            public bool? GuideAfterCalibration { get; set; }
            public bool AutoGainAtStart { get; set; }
            public bool ShowFwhm { get; set; }
            public bool? ShowStarProfile { get; set; }
            public bool UseDarkLibrary { get; set; }
            public bool GuideLogDisabled { get; set; }
            public bool UseMultiStar { get; set; }
            public int MultiStarCount { get; set; }
            public bool CloudPauseDisabled { get; set; }
            public int CloudFluxPercent { get; set; }
            public int? LostTimeoutMin { get; set; }
            public Dictionary<string, double> FocalByCamera { get; set; }
            public bool BackupStarDisabled { get; set; }
            public int StretchIndex { get; set; }
            public bool SimulateClouds { get; set; }
            public bool GraphInArcsec { get; set; }
        }

        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NINA", "InternalGuider", "settings.json");

        private static readonly object sync = new object();
        private static bool loaded;
        private static string guideCameraId;
        private static double focalLengthMm = DefaultFocalLengthMm;
        private static int calibrationStepMs;
        private static int maxRaMs = DefaultMaxPulseMs;
        private static int maxDecMs = DefaultMaxPulseMs;
        private static double aggressivenessRa = DefaultAggressivenessPercent;
        private static double aggressivenessDec = DefaultAggressivenessPercent;
        private static double hysteresisRa;
        private static double hysteresisDec;
        private static int decMode;
        private static int decModeHandedness;
        private static double integralDec;
        private static double minMovePx = 0.15;
        private static int postFlipMaxDecMs = 1000;
        private static int decBacklashMs = 0;
        private static bool pecApply;
        private static bool aiMode;
        private static int aiLearnMin = 15;
        private static int aiEvalMin = 8;
        private static int aiRetuneMin = 30;
        private static bool interExpEnabled;
        private static bool monoCamera;
        private static bool cameraDiag;
        private static int interExpBudgetMs = 3000;
        private static double interExpRotationDeg;
        private static double interExpScaleArcsec;
        private static double interExpJumpArcsec = 8;
        private static double ditherPixels = 3;
        private static bool ditherRaOnly;
        private static bool pecRecord;
        private static int pecStrengthPercent = 80;
        private static bool guideAfterCalibration = true;
        private static bool autoGainAtStart;
        private static bool showFwhm;
        private static bool showStarProfile = true;
        private static bool useDarkLibrary;
        private static bool guideLogDisabled;
        private static bool useMultiStar;
        private static int multiStarCount = DefaultMultiStarCount;
        private static bool cloudPauseDisabled;
        private static int cloudFluxPercent = DefaultCloudFluxPercent;
        private static int lostTimeoutMin = DefaultLostTimeoutMin;
        private static Dictionary<string, double> focalByCamera = new Dictionary<string, double>();
        private static bool backupStarDisabled;
        private static int stretchIndex;
        private static bool simulateClouds;
        private static bool graphInArcsec;

        /// <summary>Id de la caméra de guidage choisie (persistant).</summary>
        public static string GuideCameraId {
            get { Load(); return guideCameraId; }
            set {
                Load();
                guideCameraId = value;
                Save();
            }
        }

        /// <summary>Focale du télescope de guidage en millimètres (persistant). Sert à calculer l'échelle en secondes d'arc par pixel.</summary>
        public static double FocalLengthMm {
            get {
                Load();
                // Chaque caméra de guidage garde sa focale (lunettes de guidage différentes) ; à défaut, la dernière saisie
                if (guideCameraId != null && focalByCamera.TryGetValue(guideCameraId, out var f) && f > 0) return f;
                return focalLengthMm;
            }
            set {
                Load();
                focalLengthMm = value > 0 ? value : DefaultFocalLengthMm;
                if (guideCameraId != null) focalByCamera[guideCameraId] = focalLengthMm;
                Save();
            }
        }

        /// <summary>Focale mémorisée pour cette caméra (null s'il n'y en a pas) : sert à préremplir la fenêtre de configuration.</summary>
        public static double? FocalLengthFor(string cameraId) {
            Load();
            return cameraId != null && focalByCamera.TryGetValue(cameraId, out var f) && f > 0 ? f : (double?)null;
        }

        /// <summary>
        /// Durée d'une impulsion de calibration en millisecondes (persistant). 0 = automatique : la durée est calculée
        /// d'après la vitesse mesurée de la monture (pas d'environ 6 pixels).
        /// </summary>
        public static int CalibrationStepMs {
            get { Load(); return calibrationStepMs; }
            set {
                Load();
                calibrationStepMs = value <= 0 ? 0 : Math.Max(50, Math.Min(10000, value));
                Save();
            }
        }

        /// <summary>Durée maximale d'une impulsion de correction en RA, en ms (persistant).</summary>
        public static int MaxRaMs {
            get { Load(); return maxRaMs; }
            set { Load(); maxRaMs = Math.Max(10, Math.Min(10000, value)); Save(); }
        }

        /// <summary>Durée maximale d'une impulsion de correction en Dec, en ms (persistant).</summary>
        public static int MaxDecMs {
            get { Load(); return maxDecMs; }
            set { Load(); maxDecMs = Math.Max(10, Math.Min(10000, value)); Save(); }
        }

        /// <summary>Agressivité en RA, en pourcentage (persistant).</summary>
        public static double AggressivenessRaPercent {
            get { Load(); return aggressivenessRa; }
            set { Load(); aggressivenessRa = Math.Max(1, Math.Min(100, value)); Save(); }
        }

        /// <summary>Agressivité en Dec, en pourcentage (persistant).</summary>
        public static double AggressivenessDecPercent {
            get { Load(); return aggressivenessDec; }
            set { Load(); aggressivenessDec = Math.Max(1, Math.Min(100, value)); Save(); }
        }

        /// <summary>Hystérésis en RA, en pourcentage (0 à 90, persistant).</summary>
        public static double HysteresisRaPercent {
            get { Load(); return hysteresisRa; }
            set { Load(); hysteresisRa = Math.Max(0, Math.Min(90, value)); Save(); }
        }

        public static double HysteresisDecPercent {
            get { Load(); return hysteresisDec; }
            set { Load(); hysteresisDec = Math.Max(0, Math.Min(90, value)); Save(); }
        }

        /// <summary>Sens de correction en Dec : 0 = Auto, 1 = Nord seul, 2 = Sud seul (persistant).</summary>
        public static int DecMode {
            get { Load(); return decMode; }
            set { Load(); decMode = value < 0 || value > 2 ? 0 : value; Save(); }
        }

        /// <summary>Erreur minimale (pixels) en dessous de laquelle on ne corrige pas (persistant).</summary>
        public static double MinMovePx {
            get { Load(); return minMovePx; }
            set { Load(); minMovePx = Math.Max(0.02, Math.Min(2.0, value)); Save(); }
        }

        /// <summary>Correction intégrale en Dec, en % de l'erreur ajoutée à chaque image (0 = désactivée, persistant).</summary>
        public static double IntegralDecPercent {
            get { Load(); return integralDec; }
            set { Load(); integralDec = Math.Max(0, Math.Min(50, value)); Save(); }
        }

        /// <summary>Après un clic sur Calibrer, lance le guidage dès que la calibration est réussie (persistant, activé par défaut).</summary>
        public static bool GuideAfterCalibration {
            get { Load(); return guideAfterCalibration; }
            set { Load(); guideAfterCalibration = value; Save(); }
        }

        /// <summary>Gain automatique avant la calibration ou le guidage, au plus une fois par heure (persistant, désactivé par défaut).</summary>
        public static bool AutoGainAtStart {
            get { Load(); return autoGainAtStart; }
            set { Load(); autoGainAtStart = value; Save(); }
        }

        /// <summary>Compensation du jeu en Dec : durée (ms) ajoutée à la première impulsion après une inversion de sens (0 = désactivée, persistant).</summary>
        public static int DecBacklashMs {
            get { Load(); return decBacklashMs; }
            set { Load(); decBacklashMs = value <= 0 ? 0 : Math.Min(5000, value); Save(); }
        }

        /// <summary>PEC logiciel : applique la courbe d'erreur périodique RA mémorisée (persistant).</summary>
        public static bool PecApply {
            get { Load(); return pecApply; }
            set { Load(); pecApply = value; Save(); }
        }

        /// <summary>PEC logiciel : enregistre la courbe pendant le prochain guidage d'au moins 10 minutes, puis se désactive (persistant).</summary>
        public static bool PecRecord {
            get { Load(); return pecRecord; }
            set { Load(); pecRecord = value; Save(); }
        }

        /// <summary>Mode IA : règle automatiquement les paramètres de guidage pendant le guidage (persistant).</summary>
        public static bool AiMode {
            get { Load(); return aiMode; }
            set { Load(); aiMode = value; Save(); }
        }

        /// <summary>Mode IA : durée de l'analyse initiale, en minutes (5 à 60, persistant).</summary>
        public static int AiLearnMin {
            get { Load(); return aiLearnMin; }
            set { Load(); aiLearnMin = Math.Max(5, Math.Min(60, value)); Save(); }
        }

        /// <summary>Mode IA : durée de vérification de chaque essai, en minutes (3 à 30, persistant).</summary>
        public static int AiEvalMin {
            get { Load(); return aiEvalMin; }
            set { Load(); aiEvalMin = Math.Max(3, Math.Min(30, value)); Save(); }
        }

        /// <summary>Mode IA : intervalle entre deux ajustements, en minutes (0 = un seul ajustement par guidage, sinon 10 à 240, persistant).</summary>
        public static int AiRetuneMin {
            get { Load(); return aiRetuneMin; }
            set { Load(); aiRetuneMin = value <= 0 ? 0 : Math.Max(10, Math.Min(240, value)); Save(); }
        }

        /// <summary>Caméra unique : le guidage utilise la caméra principale du profil NINA, sans la choisir dans la liste (persistant).</summary>
        public static bool MonoCamera {
            get { Load(); return monoCamera; }
            set { Load(); monoCamera = value; Save(); }
        }

        /// <summary>Journal de diagnostic des poses du plugin et de NINA sur une caméra commune (persistant).</summary>
        public static bool CameraDiag {
            get { Load(); return cameraDiag; }
            set { Load(); cameraDiag = value; Save(); }
        }

        /// <summary>Correction entre les poses : recalage du champ après chaque pose de lumière (persistant).</summary>
        public static bool InterExpEnabled {
            get { Load(); return interExpEnabled; }
            set { Load(); interExpEnabled = value; Save(); }
        }

        /// <summary>Correction entre les poses : durée totale maximale des impulsions envoyées entre deux poses, en ms (500 à 20000).</summary>
        public static int InterExpBudgetMs {
            get { Load(); return interExpBudgetMs; }
            set { Load(); interExpBudgetMs = Math.Max(500, Math.Min(20000, value)); Save(); }
        }

        /// <summary>Correction entre les poses : rotation de la caméra principale par rapport à la caméra de guidage, en degrés.</summary>
        public static double InterExpRotationDeg {
            get { Load(); return interExpRotationDeg; }
            set { Load(); interExpRotationDeg = Math.Max(-180, Math.Min(180, value)); Save(); }
        }

        /// <summary>Correction entre les poses : échelle de l'image principale en ″/px (0 = calculée avec la taille de pixel et la focale).</summary>
        public static double InterExpScaleArcsec {
            get { Load(); return interExpScaleArcsec; }
            set { Load(); interExpScaleArcsec = value <= 0 ? 0 : Math.Max(0.1, Math.Min(30, value)); Save(); }
        }

        /// <summary>Amplitude du dither, en pixels de la caméra de guidage (0,5 à 30 ; 3 par défaut, persistant).</summary>
        public static double DitherPixels {
            get { Load(); return ditherPixels; }
            set { Load(); ditherPixels = Math.Max(0.5, Math.Min(30, value)); Save(); }
        }

        /// <summary>Dither en ascension droite seulement (persistant).</summary>
        public static bool DitherRaOnly {
            get { Load(); return ditherRaOnly; }
            set { Load(); ditherRaOnly = value; Save(); }
        }

        /// <summary>Correction entre les poses : écart à la prévision (″) au-delà duquel on considère un dither ou un recentrage et on reprend une référence.</summary>
        public static double InterExpJumpArcsec {
            get { Load(); return interExpJumpArcsec; }
            set { Load(); interExpJumpArcsec = Math.Max(3, Math.Min(120, value)); Save(); }
        }

        /// <summary>Force de la PEC appliquée, de 20 à 100 % (persistant).</summary>
        public static int PecStrengthPercent {
            get { Load(); return pecStrengthPercent; }
            set { Load(); pecStrengthPercent = Math.Max(20, Math.Min(100, value)); Save(); }
        }

        /// <summary>Impulsion Dec maximale temporaire après un retournement, en ms (0 = désactivée, persistant).</summary>
        public static int PostFlipMaxDecMs {
            get { Load(); return postFlipMaxDecMs; }
            set { Load(); postFlipMaxDecMs = value <= 0 ? 0 : Math.Max(50, Math.Min(10000, value)); Save(); }
        }

        /// <summary>Sens de rotation RA → Dec de la calibration pour laquelle le sens Dec à sens unique a été choisi (0 = pas encore noté).</summary>
        public static int DecModeHandedness {
            get { Load(); return decModeHandedness; }
            set { Load(); decModeHandedness = Math.Max(-1, Math.Min(1, value)); Save(); }
        }

        /// <summary>Soustraire le dark correspondant (même pose et même gain) des images de guidage (persistant).</summary>
        public static bool UseDarkLibrary {
            get { Load(); return useDarkLibrary; }
            set { Load(); useDarkLibrary = value; Save(); }
        }

        /// <summary>Enregistrer un journal CSV de chaque session de guidage (activé par défaut, persistant).</summary>
        public static bool GuideLogEnabled {
            get { Load(); return !guideLogDisabled; }
            set { Load(); guideLogDisabled = !value; Save(); }
        }

        /// <summary>Guidage sur plusieurs étoiles (persistant, désactivé par défaut).</summary>
        public static bool UseMultiStar {
            get { Load(); return useMultiStar; }
            set { Load(); useMultiStar = value; Save(); }
        }

        /// <summary>Nombre maximal d'étoiles suivies en mode multi-étoiles (2 à 12, persistant).</summary>
        public static int MultiStarCount {
            get { Load(); return multiStarCount; }
            set { Load(); multiStarCount = Math.Max(2, Math.Min(12, value)); Save(); }
        }

        /// <summary>Suspend les corrections quand le flux des étoiles chute (nuage), puis reprend (persistant, activé par défaut).</summary>
        public static bool CloudPauseEnabled {
            get { Load(); return !cloudPauseDisabled; }
            set { Load(); cloudPauseDisabled = !value; Save(); }
        }

        /// <summary>Seuil de chute de flux (en % du flux normal, 20 à 90) en dessous duquel les corrections sont suspendues.</summary>
        public static int CloudFluxPercent {
            get { Load(); return cloudFluxPercent; }
            set { Load(); cloudFluxPercent = Math.Max(20, Math.Min(90, value)); Save(); }
        }

        /// <summary>Durée (minutes) pendant laquelle on attend le retour de l'étoile avant d'arrêter le guidage (0 = sans limite).</summary>
        public static int LostTimeoutMin {
            get { Load(); return lostTimeoutMin; }
            set { Load(); lostTimeoutMin = Math.Max(0, Math.Min(480, value)); Save(); }
        }

        /// <summary>Étoile de secours en guidage mono-étoile (persistant, activée par défaut).</summary>
        public static bool BackupStarEnabled {
            get { Load(); return !backupStarDisabled; }
            set { Load(); backupStarDisabled = !value; Save(); }
        }

        /// <summary>En simulation, fait passer des nuages en boucle pendant le guidage (persistant, désactivé par défaut).</summary>
        public static bool SimulateClouds {
            get { Load(); return simulateClouds; }
            set { Load(); simulateClouds = value; Save(); }
        }

        /// <summary>Unité du graphe de l'aperçu : vrai = secondes d'arc, faux = pixels (persistant).</summary>
        public static bool GraphInArcsec {
            get { Load(); return graphInArcsec; }
            set { Load(); graphInArcsec = value; Save(); }
        }

        /// <summary>Affiche le FWHM moyen des étoiles à côté du contraste (persistant, désactivé par défaut).</summary>
        /// <summary>Panneau « Profil de l'étoile » à droite de l'aperçu (activé par défaut).</summary>
        public static bool ShowStarProfile {
            get { Load(); return showStarProfile; }
            set { Load(); showStarProfile = value; Save(); }
        }

        public static bool ShowFwhm {
            get { Load(); return showFwhm; }
            set { Load(); showFwhm = value; Save(); }
        }

        /// <summary>Contraste de l'aperçu : 0 = auto, 1 = doux, 2 = fort, 3 = très fort (persistant).</summary>
        public static int StretchIndex {
            get { Load(); return stretchIndex; }
            set { Load(); stretchIndex = Math.Max(0, Math.Min(3, value)); Save(); }
        }

        /// <summary>Instance de la caméra choisie, valable pendant la session NINA seulement.</summary>
        public static IDevice SelectedCamera { get; set; }

        /// <summary>Applique des réglages lus dans un fichier (les valeurs absentes ou invalides gardent leur valeur actuelle).</summary>
        private static void Apply(Data data, bool includeCamera) {
                if (data == null) return;
            if (includeCamera) guideCameraId = data.GuideCameraId;
                if (data != null && data.FocalLengthMm > 0) focalLengthMm = data.FocalLengthMm;
                if (data != null && data.AggressivenessRaPercent >= 1) aggressivenessRa = Math.Min(100, data.AggressivenessRaPercent);
                if (data != null && data.AggressivenessDecPercent >= 1) aggressivenessDec = Math.Min(100, data.AggressivenessDecPercent);
                if (data != null) useDarkLibrary = data.UseDarkLibrary;
                if (data != null) guideLogDisabled = data.GuideLogDisabled;
                if (data != null) useMultiStar = data.UseMultiStar;
                if (data != null && data.MultiStarCount >= 2) multiStarCount = Math.Min(12, data.MultiStarCount);
                if (data != null) cloudPauseDisabled = data.CloudPauseDisabled;
                if (data?.FocalByCamera != null) focalByCamera = new Dictionary<string, double>(data.FocalByCamera);
                if (data != null) backupStarDisabled = data.BackupStarDisabled;
                if (data != null) stretchIndex = Math.Max(0, Math.Min(3, data.StretchIndex));
                if (data != null) simulateClouds = data.SimulateClouds;
                if (data != null) graphInArcsec = data.GraphInArcsec;
                if (data != null && data.CloudFluxPercent >= 20) cloudFluxPercent = Math.Min(90, data.CloudFluxPercent);
                if (data?.LostTimeoutMin != null) lostTimeoutMin = Math.Max(0, Math.Min(480, data.LostTimeoutMin.Value));
                if (data != null && data.DecMode >= 0 && data.DecMode <= 2) decMode = data.DecMode;
                if (data != null) decModeHandedness = Math.Max(-1, Math.Min(1, data.DecModeHandedness));
                if (data != null) integralDec = Math.Max(0, Math.Min(50, data.IntegralDecPercent));
                if (data?.MinMovePx != null) minMovePx = Math.Max(0.02, Math.Min(2.0, data.MinMovePx.Value));
                if (data != null) { showFwhm = data.ShowFwhm; if (data.ShowStarProfile.HasValue) showStarProfile = data.ShowStarProfile.Value; }
                autoGainAtStart = data != null && data.AutoGainAtStart;
                if (data?.GuideAfterCalibration != null) guideAfterCalibration = data.GuideAfterCalibration.Value;
                if (data != null) { pecApply = data.PecApply; pecRecord = data.PecRecord; aiMode = data.AiMode; }
                if (data?.AiLearnMin != null) aiLearnMin = Math.Max(5, Math.Min(60, data.AiLearnMin.Value));
                if (data?.AiRetuneMin != null) aiRetuneMin = data.AiRetuneMin.Value <= 0 ? 0 : Math.Max(10, Math.Min(240, data.AiRetuneMin.Value));
                interExpEnabled = data != null && data.InterExpEnabled;
                monoCamera = data != null && data.MonoCamera;
                cameraDiag = data != null && data.CameraDiag;
                if (data?.InterExpBudgetMs != null) interExpBudgetMs = Math.Max(500, Math.Min(20000, data.InterExpBudgetMs.Value));
                if (data?.InterExpRotationDeg != null) interExpRotationDeg = Math.Max(-180, Math.Min(180, data.InterExpRotationDeg.Value));
                if (data?.InterExpScaleArcsec != null) interExpScaleArcsec = data.InterExpScaleArcsec.Value <= 0 ? 0 : Math.Max(0.1, Math.Min(30, data.InterExpScaleArcsec.Value));
                if (data?.DitherPixels != null) ditherPixels = Math.Max(0.5, Math.Min(30, data.DitherPixels.Value));
                ditherRaOnly = data != null && data.DitherRaOnly;
                if (data?.InterExpJumpArcsec != null) interExpJumpArcsec = Math.Max(3, Math.Min(120, data.InterExpJumpArcsec.Value));
                if (data?.AiEvalMin != null) aiEvalMin = Math.Max(3, Math.Min(30, data.AiEvalMin.Value));
                if (data?.PecStrengthPercent != null) pecStrengthPercent = Math.Max(20, Math.Min(100, data.PecStrengthPercent.Value));
                if (data?.DecBacklashMs != null) decBacklashMs = data.DecBacklashMs.Value <= 0 ? 0 : Math.Min(5000, data.DecBacklashMs.Value);
                if (data?.PostFlipMaxDecMs != null) postFlipMaxDecMs = data.PostFlipMaxDecMs.Value <= 0 ? 0 : Math.Max(50, Math.Min(10000, data.PostFlipMaxDecMs.Value));
                if (data != null) {
                    hysteresisRa = Math.Max(0, Math.Min(90, data.HysteresisRaPercent));
                    hysteresisDec = Math.Max(0, Math.Min(90, data.HysteresisDecPercent));
                }
                if (data != null && data.MaxRaMs >= 10) maxRaMs = Math.Min(10000, data.MaxRaMs);
                if (data != null && data.MaxDecMs >= 10) maxDecMs = Math.Min(10000, data.MaxDecMs);
                if (data != null && data.CalibrationStepMs > 0) calibrationStepMs = Math.Max(50, Math.Min(10000, data.CalibrationStepMs));
        }

        private static void Load() {
            lock (sync) {
                if (loaded) return;
                loaded = true;
                try {
                    if (File.Exists(FilePath)) {
                        var data = JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath));
                        Apply(data, true);
                    }
                    // Le sens Dec (Auto / Nord seul / Sud seul) repart toujours de Auto au démarrage : un sens unique dépend de la dérive
                    // de la nuit et du côté du pilier, il ne doit pas survivre d'une session à l'autre.
                    decMode = 0;
                    decModeHandedness = 0;
                } catch (Exception ex) {
                    Logger.Error(ex);
                }
            }
        }

        private static Data Snapshot() {
            return new Data { GuideCameraId = guideCameraId, FocalLengthMm = focalLengthMm, CalibrationStepMs = calibrationStepMs, MaxRaMs = maxRaMs, MaxDecMs = maxDecMs,
                            AggressivenessRaPercent = aggressivenessRa, AggressivenessDecPercent = aggressivenessDec,
                            HysteresisRaPercent = hysteresisRa, HysteresisDecPercent = hysteresisDec, DecMode = decMode, DecModeHandedness = decModeHandedness, IntegralDecPercent = integralDec, MinMovePx = minMovePx, PostFlipMaxDecMs = postFlipMaxDecMs, DecBacklashMs = decBacklashMs, PecApply = pecApply, PecRecord = pecRecord, AiMode = aiMode, AiLearnMin = aiLearnMin, AiEvalMin = aiEvalMin, AiRetuneMin = aiRetuneMin, InterExpEnabled = interExpEnabled, MonoCamera = monoCamera, CameraDiag = cameraDiag, InterExpBudgetMs = interExpBudgetMs, InterExpRotationDeg = interExpRotationDeg, InterExpScaleArcsec = interExpScaleArcsec, InterExpJumpArcsec = interExpJumpArcsec, DitherPixels = ditherPixels, DitherRaOnly = ditherRaOnly, PecStrengthPercent = pecStrengthPercent, GuideAfterCalibration = guideAfterCalibration, AutoGainAtStart = autoGainAtStart, ShowFwhm = showFwhm, ShowStarProfile = showStarProfile, UseDarkLibrary = useDarkLibrary, GuideLogDisabled = guideLogDisabled,
                            UseMultiStar = useMultiStar, MultiStarCount = multiStarCount,
                            CloudPauseDisabled = cloudPauseDisabled, CloudFluxPercent = cloudFluxPercent, LostTimeoutMin = lostTimeoutMin,
                            FocalByCamera = focalByCamera, BackupStarDisabled = backupStarDisabled, StretchIndex = stretchIndex, SimulateClouds = simulateClouds, GraphInArcsec = graphInArcsec };
        }

        private static void Save() {
            lock (sync) {
                try {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                    File.WriteAllText(FilePath, JsonSerializer.Serialize(Snapshot()));
                } catch (Exception ex) {
                    Logger.Error(ex);
                }
            }
        }

        // --- Sauvegarde et chargement de la configuration complète ---

        private const string ConfigFormat = "InternalGuiderConfiguration";

        private class ConfigFile {
            public string Format { get; set; }
            public int Version { get; set; }
            public string Plugin { get; set; }
            public string Date { get; set; }
            public Data Settings { get; set; }
            public JsonElement? Camera { get; set; }
        }

        /// <summary>Dossier proposé par défaut : Documents\NINA.</summary>
        public static string DefaultConfigurationDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NINA");

        /// <summary>Nom proposé : date_heure_configuration.json (par exemple 20261031_113700_configuration.json).</summary>
        public static string DefaultConfigurationFileName() => DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_configuration.json";

        /// <summary>Écrit tous les réglages (et les gains et poses par caméra) dans un fichier. Les calibrations n'en font pas partie : elles dépendent du matériel.</summary>
        public static void ExportConfiguration(string path) {
            Load();
            Data data;
            lock (sync) data = Snapshot();
            var file = new ConfigFile {
                Format = ConfigFormat,
                Version = 1,
                Plugin = typeof(GuiderSettings).Assembly.GetName().Version.ToString(),
                Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Settings = data,
                Camera = JsonDocument.Parse(GuideCameraSettings.ToJson()).RootElement.Clone()
            };
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true }));
        }

        /// <summary>Lit un fichier de configuration et applique ses réglages. Le choix de la caméra de guidage, propre à chaque PC, n'est pas modifié.</summary>
        public static void ImportConfiguration(string path) {
            ConfigFile file;
            try {
                file = JsonSerializer.Deserialize<ConfigFile>(File.ReadAllText(path));
            } catch (JsonException) {
                throw new InvalidDataException("ce fichier n'est pas une configuration Internal Guider.");
            }
            if (file == null || file.Format != ConfigFormat || file.Settings == null) {
                throw new InvalidDataException("ce fichier n'est pas une configuration Internal Guider.");
            }
            Load();
            lock (sync) {
                Apply(file.Settings, false);
                decModeHandedness = 0;   // propre à la monture et à la calibration : noté à nouveau au prochain changement du sens Dec
                Save();
            }
            if (file.Camera.HasValue) GuideCameraSettings.FromJson(file.Camera.Value.GetRawText());
        }
    }
}
