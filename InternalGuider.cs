using NINA.Astrometry;
using NINA.Core.Interfaces;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using NINA.Equipment.Equipment.MyGuider;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Model;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.ViewModel.Equipment.Camera;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace NINA.Plugin.InternalGuider {
    /// <summary>
    /// Jalons 3 et 4 : la caméra de guidage choisie est mémorisée (fichier), "Connecter" / "Déconnecter"
    /// l'ouvrent et la ferment, et une fenêtre d'aperçu (poses courtes en boucle, détection d'étoiles)
    /// s'ouvre à la connexion. Pas encore de guidage.
    /// </summary>
    public class InternalGuider : IGuider, INotifyPropertyChanged {
        private readonly CameraChooserVM cameraChooser;
        private bool connected;
        private ICamera guideCamera;
        private PreviewViewModel preview;
        private PreviewWindow previewWindow;

        private readonly ITelescopeMediator telescopeMediator;

        private readonly IProfileService profileService;

        private readonly ICameraMediator cameraMediator;

        public InternalGuider(CameraChooserVM cameraChooser, ITelescopeMediator telescopeMediator, IProfileService profileService, ICameraMediator cameraMediator = null) {
            this.cameraMediator = cameraMediator;
            this.profileService = profileService;
            this.cameraChooser = cameraChooser;
            this.telescopeMediator = telescopeMediator;
        }

        public string Id => "InternalGuider";
        public string Name => "Internal Guider";
        public string DisplayName => Name;
        public string Category => "Internal Guider";
        public string Description => "Autoguidage intégré à NINA";
        public string DriverInfo => "Internal Guider 0.1";
        public string DriverVersion => "0.1.0";

        public bool Connected {
            get => connected;
            private set { connected = value; RaisePropertyChanged(); }
        }

        /// <summary>Caméra de guidage connectée (null si déconnecté). Utilisée par l'aperçu et le guidage.</summary>
        public ICamera GuideCamera => guideCamera;

        public bool HasSetupDialog => true;

        // Valeurs neutres : NINA lit ces propriétés dès que le guideur est sélectionné.
        private double pixelScale;

        /// <summary>Échelle de l'image du guidage en secondes d'arc par pixel (calculée à la connexion : 206,265 x taille du pixel / focale).</summary>
        public double PixelScale {
            get => pixelScale;
            set { pixelScale = value; RaisePropertyChanged(); }
        }
        public string State =>
            preview == null ? "Idle" : preview.IsGuiding ? "Guiding" : preview.IsCalibrating ? "Calibrating" : "Idle";
        public bool CanClearCalibration => true;
        public bool CanSetShiftRate => false;
        public bool ShiftEnabled => false;
        public bool CanGetLockPosition => false;
        public SiderealShiftTrackingRate ShiftRate => default;
        public IList<string> SupportedActions => new List<string>();

        public async Task<bool> Connect(CancellationToken token) {
            try {
                sharedCamera = false;
                if (GuiderSettings.MonoCamera) {
                    // Caméra unique : on utilise l'objet caméra déjà connecté dans NINA (une seconde connexion est refusée par les SDK natifs)
                    var main = FindMainCamera();
                    if (main == null) {
                        Notification.ShowError("Internal Guider : caméra unique, mais la caméra principale de NINA est introuvable. Voir le journal NINA.");
                        return false;
                    }
                    if (!main.Connected) {
                        Notification.ShowWarning("Internal Guider : connectez d'abord la caméra dans l'onglet Caméra de NINA.");
                        return false;
                    }
                    sharedCamera = true;
                    guideCamera = main;
                    PixelScale = main.PixelSizeX > 0 ? 206.265 * main.PixelSizeX / GuiderSettings.FocalLengthMm : 0;
                    Connected = true;
                    OpenPreview(main);
                    Logger.Info($"InternalGuider: caméra unique : {main.Name} partagée avec NINA (pixel {main.PixelSizeX} µm, focale {GuiderSettings.FocalLengthMm} mm, échelle {PixelScale:0.00} arcsec/px)");
                    return true;
                }

                var device = await ResolveCameraAsync();
                if (device is not ICamera camera) {
                    Logger.Warning("InternalGuider: aucune caméra de guidage sélectionnée");
                    Notification.ShowWarning("Internal Guider : choisissez une caméra de guidage avec le bouton Configuration.");
                    return false;
                }

                if (!await camera.Connect(token)) {
                    Logger.Error($"InternalGuider: échec de connexion à {camera.Name}");
                    Notification.ShowError($"Internal Guider : impossible de connecter {camera.Name}.");
                    return false;
                }

                guideCamera = camera;
                PixelScale = camera.PixelSizeX > 0
                    ? 206.265 * camera.PixelSizeX / GuiderSettings.FocalLengthMm
                    : 0;
                Connected = true;
                OpenPreview(camera);
                Logger.Info($"InternalGuider: caméra de guidage connectée : {camera.Name} " +
                            $"(pixel {camera.PixelSizeX} µm, focale {GuiderSettings.FocalLengthMm} mm, échelle {PixelScale:0.00} arcsec/px)");
                return true;
            } catch (OperationCanceledException) {
                return false;
            } catch (Exception ex) {
                Logger.Error(ex);
                Notification.ShowError($"Internal Guider : erreur de connexion ({ex.Message}).");
                return false;
            }
        }

        private bool sharedCamera;

        /// <summary>Pose via la chaîne de capture de NINA (CameraMediator.Capture), appelée par réflexion ; null si introuvable.</summary>
        /// <summary>Méthode du médiateur Caméra qui renvoie Task&lt;IExposureData&gt; (téléchargement de l'image après Capture).</summary>
        private System.Reflection.MethodInfo FindDownloadMethod() {
            return cameraMediator.GetType().GetMethods().FirstOrDefault(m => {
                if (m.Name == "Capture" || !m.ReturnType.IsGenericType || m.ReturnType.GetGenericTypeDefinition() != typeof(Task<>)) return false;
                if (!typeof(NINA.Image.Interfaces.IExposureData).IsAssignableFrom(m.ReturnType.GetGenericArguments()[0])) return false;
                return m.GetParameters().All(p => p.ParameterType == typeof(CancellationToken) || p.ParameterType == typeof(CaptureSequence));
            });
        }

        private Func<CaptureSequence, CancellationToken, Task<NINA.Image.Interfaces.IExposureData>> BuildMonoCapture() {
            if (cameraMediator == null) return null;
            var method = cameraMediator.GetType().GetMethods().FirstOrDefault(x => x.Name == "Capture" && x.GetParameters().Length == 3 &&
                                                                                   x.GetParameters()[0].ParameterType == typeof(CaptureSequence));
            if (method == null) {
                Logger.Warning("InternalGuider: CameraMediator.Capture introuvable : poses directes sur la caméra (plus risqué en caméra unique)");
                return null;
            }
            Logger.Info("InternalGuider: caméra unique : les poses passent par CameraMediator.Capture");
            return async (sequence, token) => {
                object progress = null;
                try {
                    var pt = method.GetParameters()[2].ParameterType;
                    if (pt.IsGenericType) progress = Activator.CreateInstance(typeof(Progress<>).MakeGenericType(pt.GetGenericArguments()[0]));
                } catch (Exception) { }
                var task = (Task)method.Invoke(cameraMediator, new object[] { sequence, token, progress });
                await task.ConfigureAwait(false);
                var raw = task.GetType().GetProperty("Result")?.GetValue(task);
                var result = raw as NINA.Image.Interfaces.IExposureData;
                if (result == null) {
                    // Capture() ne fait que lancer la pose : l'image se récupère par un second appel (Download) du médiateur
                    var dl = FindDownloadMethod();
                    if (dl != null) {
                        var args = dl.GetParameters().Select(p =>
                            p.ParameterType == typeof(CancellationToken) ? (object)token :
                            p.ParameterType == typeof(CaptureSequence) ? sequence :
                            (p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null)).ToArray();
                        var dtask = (Task)dl.Invoke(cameraMediator, args);
                        await dtask.ConfigureAwait(false);
                        result = dtask.GetType().GetProperty("Result")?.GetValue(dtask) as NINA.Image.Interfaces.IExposureData;
                        if (result == null) {
                            string info = $"InternalGuider: {dl.Name} n'a pas renvoyé d'image (méthode : {dl})";
                            Logger.Warning(info);
                            CameraDiagnostic.Log(info);
                        }
                    } else {
                        string names = string.Join(" | ", cameraMediator.GetType().GetMethods().Select(m => m.ToString()));
                        string info = "InternalGuider: aucune méthode de téléchargement trouvée dans le médiateur Caméra. Méthodes : " + names;
                        Logger.Warning(info);
                        CameraDiagnostic.Log(info);
                    }
                }
                return result;
            };
        }

        /// <summary>
        /// Retrouve l'objet caméra connecté dans NINA, à partir du médiateur Caméra (par réflexion : l'API publique ne le donne pas directement).
        /// Cherche une méthode GetDevice(), puis un membre de type ICamera dans le médiateur et dans l'objet qu'il contient (le gestionnaire de caméra).
        /// </summary>
        private ICamera FindMainCamera() {
            if (cameraMediator == null) { Logger.Warning("InternalGuider: médiateur Caméra indisponible"); return null; }
            const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            var seen = new List<string>();
            try {
                foreach (var m in cameraMediator.GetType().GetMethods(all)) {
                    if (m.GetParameters().Length == 0 && typeof(ICamera).IsAssignableFrom(m.ReturnType)) {
                        if (m.Invoke(cameraMediator, null) is ICamera c) { Logger.Info($"InternalGuider: caméra principale trouvée par {m.Name}()"); return c; }
                    }
                }
                var found = Search(cameraMediator, 0, "médiateur", seen);
                if (found != null) return found;
            } catch (Exception ex) {
                Logger.Error(ex);
            }
            string list = string.Join(", ", seen);
            Logger.Warning("InternalGuider: caméra principale introuvable. Membres examinés : " + list);
            CameraDiagnostic.Log("caméra principale introuvable ; membres examinés : " + list);
            return null;
        }

        private static ICamera Search(object obj, int depth, string path, List<string> seen) {
            const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly;
            for (var t = obj.GetType(); t != null && t != typeof(object); t = t.BaseType) {
                foreach (var f in t.GetFields(all)) {
                    object v = null;
                    try { v = f.GetValue(obj); } catch (Exception) { }
                    seen.Add($"{path}.{f.Name}:{f.FieldType.Name}");
                    if (v is ICamera c) { Logger.Info($"InternalGuider: caméra principale trouvée dans {path}.{f.Name}"); return c; }
                    if (v != null && depth < 2 && IsNinaObject(v)) { var r = Search(v, depth + 1, path + "." + f.Name, seen); if (r != null) return r; }
                }
                foreach (var p in t.GetProperties(all)) {
                    if (p.GetIndexParameters().Length > 0 || !p.CanRead) continue;
                    object v = null;
                    try { v = p.GetValue(obj); } catch (Exception) { }
                    seen.Add($"{path}.{p.Name}:{p.PropertyType.Name}");
                    if (v is ICamera c) { Logger.Info($"InternalGuider: caméra principale trouvée dans {path}.{p.Name}"); return c; }
                    if (v != null && depth < 1 && IsNinaObject(v)) { var r = Search(v, depth + 1, path + "." + p.Name, seen); if (r != null) return r; }
                }
            }
            return null;
        }

        /// <summary>
        /// Caméras connues du sélecteur de l'onglet Caméra de NINA (par réflexion). Le sélecteur du plugin relance l'énumération des pilotes,
        /// qui peut s'interrompre en route (erreur du pilote SBIG, par exemple) et ne plus lister les caméras natives suivantes (Player One…).
        /// </summary>
        public IList<IDevice> MainChooserDevices() {
            var list = new List<IDevice>();
            try {
                const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
                object handler = null;
                for (var t = cameraMediator?.GetType(); t != null && handler == null; t = t.BaseType) {
                    var f = t.GetField("handler", all | System.Reflection.BindingFlags.DeclaredOnly);
                    if (f != null) handler = f.GetValue(cameraMediator);
                }
                if (handler == null) return list;
                object chooserObj = null;
                for (var t = handler.GetType(); t != null && chooserObj == null; t = t.BaseType) {
                    var f = t.GetField("<DeviceChooserVM>k__BackingField", all | System.Reflection.BindingFlags.DeclaredOnly);
                    if (f != null) chooserObj = f.GetValue(handler);
                    else {
                        var p = t.GetProperty("DeviceChooserVM", all | System.Reflection.BindingFlags.DeclaredOnly);
                        if (p != null) chooserObj = p.GetValue(handler);
                    }
                }
                var devices = chooserObj?.GetType().GetProperty("Devices", all)?.GetValue(chooserObj) as System.Collections.IEnumerable;
                if (devices != null) foreach (var d in devices) if (d is IDevice dev) list.Add(dev);
            } catch (Exception ex) {
                Logger.Warning("InternalGuider: liste des caméras de NINA illisible : " + ex.Message);
            }
            return list;
        }

        private static bool IsNinaObject(object v) {
            string ns = v.GetType().Namespace ?? "";
            return ns.StartsWith("NINA") && !(v is System.Collections.IEnumerable);
        }

        public void Disconnect() {
            try {
                ClosePreview();
                if (!sharedCamera) {
                    try { guideCamera?.AbortExposure(); } catch (Exception) { /* aucune pose en cours */ }
                    guideCamera?.Disconnect();
                }
                Logger.Info("InternalGuider: caméra de guidage déconnectée");
            } catch (Exception ex) {
                Logger.Error(ex);
            } finally {
                guideCamera = null;
                Connected = false;
            }
        }

        private void OpenPreview(ICamera camera) {
            Application.Current.Dispatcher.Invoke(() => {
                preview = new PreviewViewModel {
                    PixelScale = PixelScale,
                    ProfileService = profileService,
                    Mount = telescopeMediator != null ? new NinaMount(telescopeMediator) : null,
                    MonoMode = GuiderSettings.MonoCamera
                };
                if (GuiderSettings.MonoCamera) {
                    preview.MonoCapture = BuildMonoCapture();
                    preview.NinaBusy = () => InterExposureService.Prop(cameraMediator?.GetInfo(), "IsExposing") as bool? == true;
                }
                CameraDiagnostic.StartMonitor(cameraMediator);
                previewWindow = new PreviewWindow(preview) { Owner = Application.Current.MainWindow };
                preview.PropertyChanged += (s, e) => {
                    if (e.PropertyName == nameof(PreviewViewModel.IsGuiding) || e.PropertyName == nameof(PreviewViewModel.IsCalibrating)) {
                        RaisePropertyChanged(nameof(State));
                    }
                };
                preview.SampleAvailable += RaiseGuideStep;
                previewWindow.Closed += (s, e) => {
                    preview?.Shutdown();
                    preview = null;
                    previewWindow = null;
                };
                previewWindow.Show();
                preview.Start(camera);
            });
        }

        private void ClosePreview() {
            Application.Current.Dispatcher.Invoke(() => {
                preview?.Shutdown();
                if (previewWindow != null) {
                    previewWindow.AllowClose = true;
                    previewWindow.Close();
                }
                previewWindow = null;
                preview = null;
            });
        }

        /// <summary>Retrouve la caméra choisie, y compris après un redémarrage de NINA (via l'Id sauvegardé).</summary>
        private async Task<IDevice> ResolveCameraAsync() {
            if (GuiderSettings.MonoCamera) {
                // Caméra unique : celle du profil NINA
                string mainId = InterExposureService.Prop(profileService?.ActiveProfile, "CameraSettings.Id") as string;
                if (!string.IsNullOrEmpty(mainId) && cameraChooser != null) {
                    await cameraChooser.GetEquipment();
                    var main = cameraChooser.Devices.FirstOrDefault(d => d.Id == mainId) ?? MainChooserDevices().FirstOrDefault(d => d.Id == mainId);
                    if (main != null) {
                        Logger.Info($"InternalGuider: caméra unique : {main.Name}");
                        return main;
                    }
                }
                Logger.Warning("InternalGuider: caméra unique : caméra principale du profil introuvable");
                Notification.ShowWarning("Internal Guider : mode caméra unique, mais la caméra principale du profil NINA est introuvable. Choisissez-la dans l'onglet Équipement.");
                return null;
            }
            if (GuiderSettings.SelectedCamera != null) return GuiderSettings.SelectedCamera;

            var id = GuiderSettings.GuideCameraId;
            if (string.IsNullOrEmpty(id) || cameraChooser == null) return null;

            await cameraChooser.GetEquipment();
            var device = cameraChooser.Devices.FirstOrDefault(d => d.Id == id) ?? MainChooserDevices().FirstOrDefault(d => d.Id == id);
            GuiderSettings.SelectedCamera = device;
            return device;
        }

        /// <summary>Bouton "Configuration" : ouvre la fenêtre de choix de la caméra.</summary>
        public void SetupDialog() {
            Application.Current.Dispatcher.Invoke(() => {
                var window = new ConfigWindow(cameraChooser, MainChooserDevices) { Owner = Application.Current.MainWindow };
                window.ShowDialog();
                RefreshPixelScale();
            });
        }

        /// <summary>Recalcule l'échelle (″/px) quand la focale a changé alors que la caméra est déjà connectée.</summary>
        private void RefreshPixelScale() {
            var camera = guideCamera;
            if (camera == null || camera.PixelSizeX <= 0 || camera.Id != GuiderSettings.GuideCameraId) return;
            PixelScale = 206.265 * camera.PixelSizeX / GuiderSettings.FocalLengthMm;
            if (preview != null) preview.PixelScale = PixelScale;
            Logger.Info($"InternalGuider: focale {GuiderSettings.FocalLengthMm} mm, échelle {PixelScale:0.00} arcsec/px");
        }

        public event PropertyChangedEventHandler PropertyChanged;
        public event EventHandler<IGuideStep> GuideEvent;

        private void RaisePropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        // --- Guidage piloté par NINA ---

        /// <summary>L'aperçu (et sa boucle de poses) est nécessaire au guidage : on le rouvre s'il a été fermé.</summary>
        private PreviewViewModel EnsurePreview() {
            if (preview == null && guideCamera != null) OpenPreview(guideCamera);
            return preview;
        }

        private int guideStepCount;

        /// <summary>Envoie chaque correction à NINA : c'est ce qui alimente le graphe de l'onglet Guidage.</summary>
        private void RaiseGuideStep(GuideSample sample) {
            try {
                GuideEvent?.Invoke(this, new InternalGuideStep(++guideStepCount, sample.Time,
                    sample.ErrRaPx, sample.ErrDecPx, sample.PulseRaMs, sample.PulseDecMs));
            } catch (Exception ex) {
                Logger.Error(ex);
            }
        }

        /// <summary>Efface le message de progression affiché en bas à gauche de NINA une fois l'opération terminée.</summary>
        private void ClearStatus(IProgress<ApplicationStatus> progress) =>
            progress?.Report(new ApplicationStatus { Status = string.Empty, Source = Name });

        public Task<bool> AutoSelectGuideStar() => Task.FromResult(preview?.LastResult?.Selected != null);

        public async Task<bool> StartGuiding(bool forceCalibration, IProgress<ApplicationStatus> progress, CancellationToken ct) {
            try {
                // Trace de l'appelant : quand NINA demande un guidage, le journal de NINA dit quelle fonction l'a demandé
                try {
                    var frames = new System.Diagnostics.StackTrace(1, false).GetFrames();
                    var caller = frames == null ? "?" : string.Join(" <- ", frames.Where(f => { var n = f.GetMethod()?.DeclaringType?.FullName ?? ""; return !n.StartsWith("System.Runtime") && !n.StartsWith("System.Threading"); }).Take(30).Select(f => f.GetMethod()?.DeclaringType?.FullName + "." + f.GetMethod()?.Name));
                    Logger.Info($"InternalGuider: NINA demande StartGuiding (forceCalibration={forceCalibration}) · appelant : {caller}");
                    GuideLog.AppendCalibration($"NINA demande un guidage (forceCalibration={forceCalibration}) · appelant : {caller}");
                } catch (Exception) { }
                if (!Connected || guideCamera == null) {
                    Notification.ShowError("Internal Guider : connectez d'abord le guideur (caméra de guidage).");
                    return false;
                }
                var vm = EnsurePreview();
                if (vm == null) return false;

                void Report(string text) => progress?.Report(new ApplicationStatus { Status = "Internal Guider : " + text, Source = Name });
                bool ok;
                try {
                    ok = await vm.StartExternalGuidingAsync(forceCalibration, Report, ct);
                } finally {
                    ClearStatus(progress);
                }
                if (!ok) Notification.ShowWarning("Internal Guider : le guidage n'a pas pu démarrer (voir la fenêtre d'aperçu).");
                return ok;
            } catch (OperationCanceledException) {
                return false;
            } catch (Exception ex) {
                Logger.Error(ex);
                Notification.ShowError($"Internal Guider : erreur au démarrage du guidage ({ex.Message}).");
                return false;
            }
        }

        public async Task<bool> StopGuiding(CancellationToken ct) {
            try {
                var vm = preview;
                if (vm != null) await vm.StopExternalGuidingAsync();
                return true;
            } catch (Exception ex) {
                Logger.Error(ex);
                return false;
            }
        }

        public async Task<bool> Dither(IProgress<ApplicationStatus> progress, CancellationToken ct) {
            try {
                var vm = preview;
                if (vm == null || !vm.IsGuiding) {
                    Logger.Warning("InternalGuider: dither demandé alors que le guidage n'est pas actif");
                    return false;
                }
                progress?.Report(new ApplicationStatus { Status = "Internal Guider : dither…", Source = Name });
                try {
                    return await vm.DitherExternalAsync(ct);
                } finally {
                    ClearStatus(progress);
                }
            } catch (OperationCanceledException) {
                return false;
            } catch (Exception ex) {
                Logger.Error(ex);
                return false;
            }
        }

        public Task<bool> ClearCalibration(CancellationToken ct) {
            CalibrationData.Clear();
            Logger.Info("InternalGuider: calibration effacée");
            return Task.FromResult(true);
        }

        public Task<bool> SetShiftRate(SiderealShiftTrackingRate shiftTrackingRate, CancellationToken ct) => Task.FromResult(false);

        public Task<bool> StopShifting(CancellationToken ct) => Task.FromResult(false);

        public Task<LockPosition> GetLockPosition() {
            var lockPosition = preview?.GetLockPosition();
            return Task.FromResult(lockPosition.HasValue
                ? new LockPosition((float)lockPosition.Value.X, (float)lockPosition.Value.Y)
                : null);
        }

        public string Action(string actionName, string actionParameters) => string.Empty;
        public string SendCommandString(string command, bool raw = true) => string.Empty;
        public bool SendCommandBool(string command, bool raw = true) => false;
        public void SendCommandBlind(string command, bool raw = true) { }
    }
}
