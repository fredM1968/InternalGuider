using System.Windows;

namespace NINA.Plugin.InternalGuider {
    /// <summary>
    /// Paramètres avancés. Les valeurs de cette fenêtre ne sont appliquées qu'avec OK ; Annuler (ou la fermeture) les ignore.
    /// Créer/Supprimer des darks agissent immédiatement.
    /// </summary>
    public partial class AdvancedWindow : Window {
        private readonly PreviewViewModel viewModel;
        private readonly bool busy;

        public AdvancedWindow(PreviewViewModel viewModel) {
            InitializeComponent();
            WindowTheme.Apply(this);
            Width = System.Math.Min(Width, SystemParameters.WorkArea.Width - 40);
            Height = System.Math.Min(Height, SystemParameters.WorkArea.Height - 40);
            this.viewModel = viewModel;
            busy = viewModel.IsCalibrating || viewModel.IsGuiding;
            SimulationBox.IsChecked = viewModel.Simulation;
            // L'onglet Caméra unique n'est accessible que si l'option est activée dans Configuration
            MonoTab.IsEnabled = GuiderSettings.MonoCamera;
            LoadFields();
            if (busy) {
                SimulationBox.IsEnabled = false;
                BusyNote.Visibility = Visibility.Visible;
            }
            RefreshLibrary();
            RefreshCalibration();
        }

        /// <summary>Affiche les réglages enregistrés dans les champs de la fenêtre.</summary>
        private void LoadFields() {
            CloudSimBox.IsChecked = GuiderSettings.SimulateClouds;
            UseBox.IsChecked = GuiderSettings.UseDarkLibrary;
            LogBox.IsChecked = GuiderSettings.GuideLogEnabled;
            MultiBox.IsChecked = GuiderSettings.UseMultiStar;
            GuideAfterCalBox.IsChecked = GuiderSettings.GuideAfterCalibration;
            AutoGainBox.IsChecked = GuiderSettings.AutoGainAtStart;
            MultiCountBox.Text = GuiderSettings.MultiStarCount.ToString();
            BackupBox.IsChecked = GuiderSettings.BackupStarEnabled;
            StepBox.Text = GuiderSettings.CalibrationStepMs.ToString();
            CloudBox.IsChecked = GuiderSettings.CloudPauseEnabled;
            CloudPercentBox.Text = GuiderSettings.CloudFluxPercent.ToString();
            LostMinBox.Text = GuiderSettings.LostTimeoutMin.ToString();
            IntegralBox.Text = GuiderSettings.IntegralDecPercent.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture);
            PostFlipBox.Text = GuiderSettings.PostFlipMaxDecMs.ToString();
            BacklashBox.Text = GuiderSettings.DecBacklashMs.ToString();
            AiBox.IsChecked = GuiderSettings.AiMode;
            AiLearnBox.Text = GuiderSettings.AiLearnMin.ToString();
            AiEvalBox.Text = GuiderSettings.AiEvalMin.ToString();
            AiRetuneBox.Text = GuiderSettings.AiRetuneMin.ToString();
            DitherPxBox.Text = GuiderSettings.DitherPixels.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture);
            DitherRaBox.IsChecked = GuiderSettings.DitherRaOnly;
            InterExpBox.IsChecked = GuiderSettings.InterExpEnabled;
            CameraDiagBox.IsChecked = GuiderSettings.CameraDiag;
            InterExpBudgetBox.Text = GuiderSettings.InterExpBudgetMs.ToString();
            InterExpRotBox.Text = GuiderSettings.InterExpRotationDeg.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture);
            InterExpScaleBox.Text = GuiderSettings.InterExpScaleArcsec.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture);
            InterExpJumpBox.Text = GuiderSettings.InterExpJumpArcsec.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture);
            PecRecordBox.IsChecked = GuiderSettings.PecRecord;
            PecApplyBox.IsChecked = GuiderSettings.PecApply;
            PecStrengthBox.Text = GuiderSettings.PecStrengthPercent.ToString();
            RefreshPec();
            MinMoveBox.Text = GuiderSettings.MinMovePx.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture);
        }

        /// <summary>Met à jour la liste des darks et l'état des boutons.</summary>
        private void RefreshLibrary() {
            bool any = DarkLibrary.HasAny();
            string info = viewModel.DarkLibraryInfo();
            LibraryInfo.Text = viewModel.CameraKey == null
                ? "Aucune caméra de guidage connectée : connectez-la pour créer des darks."
                : (string.IsNullOrEmpty(info) ? "Aucun dark pour la caméra " + viewModel.CameraName + "." : "Darks de " + viewModel.CameraName + " : " + info);

            CreateButton.IsEnabled = !busy && viewModel.CameraKey != null;
            DeleteButton.IsEnabled = !busy && any;
            UseBox.IsEnabled = any;                 // utilisable seulement s'il existe une bibliothèque
            if (!any) UseBox.IsChecked = false;
        }

        /// <summary>Affiche la calibration enregistrée et active le bouton d'effacement.</summary>
        private void RefreshCalibration() {
            var cal = CalibrationData.Load();
            ClearCalButton.IsEnabled = !busy && cal != null;
            CalInfo.Text = cal == null ? "Aucune calibration enregistrée." : "Dernière calibration : " + cal.Date.ToString("yyyy-MM-dd HH:mm") + (cal.Simulated ? " (simulation)" : "");
        }

        /// <summary>Affiche la courbe PEC mémorisée (ou son absence).</summary>
        private void RefreshPec() {
            var c = PecStore.Load();
            string sim = PecStore.Simulation ? " (courbe de SIMULATION, séparée de votre vraie courbe)" : "";
            PecInfo.Text = c == null ? "Aucune courbe PEC enregistrée" + sim + "." : "Courbe enregistrée" + sim + " : " + c.Describe() + ".";
            PecClearButton.IsEnabled = c != null;
            PecAnalyseButton.IsEnabled = c != null;
        }

        private void PecAnalyse_Click(object sender, RoutedEventArgs e) {
            var c = PecStore.Load();
            if (c == null) { RefreshPec(); return; }
            new PecWindow(c) { Owner = this }.ShowDialog();
        }

        private void PecClear_Click(object sender, RoutedEventArgs e) {
            if (MessageBox.Show(this, "Effacer la courbe PEC enregistrée ?", "Internal Guider", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            PecStore.Delete();
            RefreshPec();
        }

        private void ClearCal_Click(object sender, RoutedEventArgs e) {
            var answer = MessageBox.Show(this,
                "Effacer les calibrations enregistrées (tous les côtés du pier) ?\nLe prochain guidage demandera de calibrer.",
                "Effacer la calibration", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;
            CalibrationData.Clear();
            RefreshCalibration();
        }

        private void OpenLogs_Click(object sender, RoutedEventArgs e) {
            try {
                System.IO.Directory.CreateDirectory(GuideLog.Directory);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + GuideLog.Directory + "\"") { UseShellExecute = true });
            } catch (System.Exception ex) {
                MessageBox.Show(this, "Impossible d'ouvrir le dossier : " + ex.Message, "Journal de guidage", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void Create_Click(object sender, RoutedEventArgs e) {
            new DarkCreateWindow(viewModel) { Owner = this }.ShowDialog();
            RefreshLibrary();
        }

        private void Delete_Click(object sender, RoutedEventArgs e) {
            var answer = MessageBox.Show(this,
                "Supprimer toutes les bibliothèques de darks (pour toutes les caméras) ?\nCette action est définitive.",
                "Supprimer les darks", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;
            DarkLibrary.DeleteAll();
            GuiderSettings.UseDarkLibrary = false;   // plus de bibliothèque : l'option est désactivée tout de suite
            RefreshLibrary();
        }

        private void Ok_Click(object sender, RoutedEventArgs e) {
            if (!busy) viewModel.Simulation = SimulationBox.IsChecked == true;
            ApplyFields();
            DialogResult = true;
        }

        /// <summary>Enregistre les réglages saisis dans la fenêtre.</summary>
        private void ApplyFields() {
            GuiderSettings.SimulateClouds = CloudSimBox.IsChecked == true;
            GuiderSettings.GuideLogEnabled = LogBox.IsChecked == true;
            GuiderSettings.UseMultiStar = MultiBox.IsChecked == true;
            GuiderSettings.GuideAfterCalibration = GuideAfterCalBox.IsChecked == true;
            GuiderSettings.AutoGainAtStart = AutoGainBox.IsChecked == true;
            GuiderSettings.BackupStarEnabled = BackupBox.IsChecked == true;
            if (int.TryParse(MultiCountBox.Text.Trim(), out int starCount)) GuiderSettings.MultiStarCount = starCount;
            var stepText = StepBox.Text.Trim();
            if (stepText.Length == 0) GuiderSettings.CalibrationStepMs = 0;
            else if (int.TryParse(stepText, out int stepMs) && stepMs >= 0) GuiderSettings.CalibrationStepMs = stepMs;
            GuiderSettings.CloudPauseEnabled = CloudBox.IsChecked == true;
            if (int.TryParse(CloudPercentBox.Text.Trim(), out int cloudPercent)) GuiderSettings.CloudFluxPercent = cloudPercent;
            if (int.TryParse(LostMinBox.Text.Trim(), out int lostMin)) GuiderSettings.LostTimeoutMin = lostMin;
            if (double.TryParse(IntegralBox.Text.Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double integral)) GuiderSettings.IntegralDecPercent = integral;
            if (int.TryParse(PostFlipBox.Text.Trim(), out int postFlip)) GuiderSettings.PostFlipMaxDecMs = postFlip;
            if (int.TryParse(BacklashBox.Text.Trim(), out int backlash)) { GuiderSettings.DecBacklashMs = backlash; viewModel.ReloadBacklash(); }
            GuiderSettings.AiMode = AiBox.IsChecked == true;
            if (int.TryParse(AiLearnBox.Text.Trim(), out int aiLearn)) GuiderSettings.AiLearnMin = aiLearn;
            if (int.TryParse(AiEvalBox.Text.Trim(), out int aiEval)) GuiderSettings.AiEvalMin = aiEval;
            if (int.TryParse(AiRetuneBox.Text.Trim(), out int aiRetune)) GuiderSettings.AiRetuneMin = aiRetune;
            if (double.TryParse(DitherPxBox.Text.Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double ditherPx)) GuiderSettings.DitherPixels = ditherPx;
            GuiderSettings.DitherRaOnly = DitherRaBox.IsChecked == true;
            viewModel.SyncDitherToProfile();
            GuiderSettings.InterExpEnabled = InterExpBox.IsChecked == true;
            GuiderSettings.CameraDiag = CameraDiagBox.IsChecked == true;
            if (int.TryParse(InterExpBudgetBox.Text.Trim(), out int ieBudget)) GuiderSettings.InterExpBudgetMs = ieBudget;
            var ieCulture = System.Globalization.CultureInfo.InvariantCulture;
            if (double.TryParse(InterExpRotBox.Text.Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float, ieCulture, out double ieRot)) GuiderSettings.InterExpRotationDeg = ieRot;
            if (double.TryParse(InterExpScaleBox.Text.Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float, ieCulture, out double ieScale)) GuiderSettings.InterExpScaleArcsec = ieScale;
            if (double.TryParse(InterExpJumpBox.Text.Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float, ieCulture, out double ieJump)) GuiderSettings.InterExpJumpArcsec = ieJump;
            GuiderSettings.PecRecord = PecRecordBox.IsChecked == true;
            GuiderSettings.PecApply = PecApplyBox.IsChecked == true;
            if (int.TryParse(PecStrengthBox.Text.Trim(), out int pecStrength)) GuiderSettings.PecStrengthPercent = pecStrength;
            if (double.TryParse(MinMoveBox.Text.Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double minMove)) {
                GuiderSettings.MinMovePx = minMove;
                viewModel.ApplyMinMove();
            }
            GuiderSettings.UseDarkLibrary = UseBox.IsEnabled && UseBox.IsChecked == true;
        }


        private void SaveConfig_Click(object sender, RoutedEventArgs e) {
            try {
                ApplyFields();   // prend en compte les réglages saisis dans cette fenêtre
                string dir = GuiderSettings.DefaultConfigurationDirectory;
                System.IO.Directory.CreateDirectory(dir);
                var dialog = new Microsoft.Win32.SaveFileDialog {
                    Title = "Sauvegarde de la configuration",
                    InitialDirectory = dir,
                    FileName = GuiderSettings.DefaultConfigurationFileName(),
                    DefaultExt = ".json",
                    Filter = "Configuration Internal Guider (*.json)|*.json"
                };
                if (dialog.ShowDialog(this) != true) return;
                GuiderSettings.ExportConfiguration(dialog.FileName);
                MessageBox.Show(this, "Configuration enregistrée :\n" + dialog.FileName, "Configuration", MessageBoxButton.OK, MessageBoxImage.Information);
            } catch (System.Exception ex) {
                MessageBox.Show(this, "Impossible d'enregistrer la configuration : " + ex.Message, "Configuration", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void LoadConfig_Click(object sender, RoutedEventArgs e) {
            if (viewModel.IsCalibrating || viewModel.IsGuiding) {
                MessageBox.Show(this, "Une calibration ou un guidage est en cours : arrêtez-le d'abord.", "Configuration", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            try {
                string dir = GuiderSettings.DefaultConfigurationDirectory;
                var dialog = new Microsoft.Win32.OpenFileDialog {
                    Title = "Charger une configuration",
                    InitialDirectory = System.IO.Directory.Exists(dir) ? dir : System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments),
                    DefaultExt = ".json",
                    Filter = "Configuration Internal Guider (*.json)|*.json|Tous les fichiers (*.*)|*.*",
                    CheckFileExists = true
                };
                if (dialog.ShowDialog(this) != true) return;
                var answer = MessageBox.Show(this,
                    "Les réglages actuels seront remplacés par ceux du fichier :\n" + dialog.FileName + "\n\nLes calibrations et le choix de la caméra de guidage ne changent pas. Continuer ?",
                    "Charger une configuration", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes) return;
                GuiderSettings.ImportConfiguration(dialog.FileName);
                viewModel.ReloadSettings();
                LoadFields();
                RefreshLibrary();
                MessageBox.Show(this, "Configuration chargée.", "Configuration", MessageBoxButton.OK, MessageBoxImage.Information);
            } catch (System.IO.InvalidDataException ex) {
                MessageBox.Show(this, "Configuration non chargée : " + ex.Message, "Configuration", MessageBoxButton.OK, MessageBoxImage.Warning);
            } catch (System.Exception ex) {
                MessageBox.Show(this, "Impossible de charger la configuration : " + ex.Message, "Configuration", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) {
            DialogResult = false;
        }
    }
}
