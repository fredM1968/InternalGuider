using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using NINA.Equipment.Interfaces;
using NINA.WPF.Base.ViewModel.Equipment.Camera;

namespace NINA.Plugin.InternalGuider {
    public class ConfigViewModel : INotifyPropertyChanged {
        private readonly CameraChooserVM chooser;
        private bool isLoading;
        private IDevice selectedCamera;

        /// <summary>Notre propre copie de la liste : indépendante des notifications du sélecteur NINA.</summary>
        public ObservableCollection<IDevice> Cameras { get; } = new ObservableCollection<IDevice>();

        public IDevice SelectedCamera {
            get => selectedCamera;
            set {
                selectedCamera = value;
                // Chaque caméra a sa focale : on affiche celle déjà mémorisée pour la caméra choisie
                var known = value != null ? GuiderSettings.FocalLengthFor(value.Id) : null;
                if (known.HasValue) {
                    FocalLengthText = known.Value.ToString("0.##", CultureInfo.InvariantCulture);
                    RaisePropertyChanged(nameof(FocalLengthText));
                }
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(CanOpenCameraSettings));
            }
        }

        /// <summary>Vrai si le pilote de la caméra choisie a une fenêtre de réglages (ASCOM, par exemple).</summary>
        public bool CanOpenCameraSettings => selectedCamera != null && selectedCamera.HasSetupDialog;

        /// <summary>
        /// Équivalent de la roue dentée de l'onglet Caméra et du bouton « Réglages Caméra » de PHD2 :
        /// ouvre la fenêtre du pilote (par exemple le sélecteur ASCOM d'Atik, pour choisir l'Atik GP).
        /// </summary>
        public void OpenCameraSettings() {
            var camera = selectedCamera;
            if (camera == null || !camera.HasSetupDialog) return;
            try {
                camera.SetupDialog();
            } catch (Exception ex) {
                Logger.Error(ex);
                Notification.ShowError($"Internal Guider : impossible d'ouvrir les réglages de {camera.Name} ({ex.Message}).");
            }
        }

        /// <summary>Caméra unique : la caméra principale du profil NINA sert au guidage.</summary>
        public bool MonoCamera {
            get => GuiderSettings.MonoCamera;
            set {
                GuiderSettings.MonoCamera = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(NotMono));
            }
        }

        public bool NotMono => !GuiderSettings.MonoCamera;

        /// <summary>Focale en mm, saisie sous forme de texte (accepte la virgule ou le point décimal).</summary>
        public string FocalLengthText { get; set; }

        /// <summary>Pas de calibration en ms (0 ou vide = automatique).</summary>

        public bool IsLoading {
            get => isLoading;
            private set { isLoading = value; RaisePropertyChanged(); }
        }

        private readonly Func<System.Collections.Generic.IList<IDevice>> extraDevices;

        public ConfigViewModel(CameraChooserVM chooser, Func<System.Collections.Generic.IList<IDevice>> extraDevices = null) {
            this.chooser = chooser;
            this.extraDevices = extraDevices;
            FocalLengthText = GuiderSettings.FocalLengthMm.ToString("0.##", CultureInfo.InvariantCulture);
        }

        public async Task LoadAsync() {
            if (chooser == null) {
                Logger.Warning("InternalGuider: sélecteur de caméras indisponible");
                return;
            }
            IsLoading = true;
            try {
                await chooser.GetEquipment();

                Cameras.Clear();
                foreach (var device in chooser.Devices) Cameras.Add(device);
                // Compléments : caméras vues par l'onglet Caméra de NINA mais absentes de la liste du plugin (énumération interrompue)
                try {
                    int added = 0;
                    foreach (var device in extraDevices?.Invoke() ?? new System.Collections.Generic.List<IDevice>()) {
                        if (device != null && !Cameras.Any(c => c.Id == device.Id)) { Cameras.Add(device); added++; }
                    }
                    if (added > 0) Logger.Info($"InternalGuider: {added} caméra(s) ajoutée(s) depuis la liste de l'onglet Caméra de NINA");
                } catch (Exception ex) { Logger.Warning("InternalGuider: " + ex.Message); }
                // Une caméra hors ligne (déclarée dans le profil mais absente) ne peut pas être utilisée : on ne la propose pas
                foreach (var off in Cameras.Where(c => c.Name != null && c.Name.IndexOf("(OFFLINE)", StringComparison.OrdinalIgnoreCase) >= 0).ToList()) Cameras.Remove(off);
                Logger.Info($"InternalGuider: {Cameras.Count} entrées dans la liste des caméras");

                var saved = GuiderSettings.GuideCameraId;
                SelectedCamera = (string.IsNullOrEmpty(saved)
                                     ? null
                                     : Cameras.FirstOrDefault(d => d.Id == saved))
                                 ?? Cameras.FirstOrDefault();
            } catch (Exception ex) {
                Logger.Error(ex);
            } finally {
                IsLoading = false;
            }
        }

        public void SaveSelection() {
            if (!GuiderSettings.MonoCamera) {
                GuiderSettings.GuideCameraId = SelectedCamera?.Id;   // persistant (fichier)
                GuiderSettings.SelectedCamera = SelectedCamera;      // instance de la session
            } else {
                GuiderSettings.SelectedCamera = null;                // la caméra sera retrouvée à la connexion
            }

            var text = (FocalLengthText ?? string.Empty).Trim().Replace(',', '.');
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var focal) && focal > 0) {
                GuiderSettings.FocalLengthMm = focal;
            } else {
                Logger.Warning($"InternalGuider: focale invalide « {FocalLengthText} », valeur précédente conservée");
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void RaisePropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
