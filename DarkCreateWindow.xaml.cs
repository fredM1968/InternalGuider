using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;

namespace NINA.Plugin.InternalGuider {
    /// <summary>Création de darks : « De xs à ys nbre N », puis Générer.</summary>
    public partial class DarkCreateWindow : Window {
        private readonly PreviewViewModel viewModel;
        private bool busy;

        public DarkCreateWindow(PreviewViewModel viewModel) {
            InitializeComponent();
            WindowTheme.Apply(this);
            this.viewModel = viewModel;
            FromBox.ItemsSource = viewModel.ExposureChoices;
            ToBox.ItemsSource = viewModel.ExposureChoices;
            FromBox.SelectedItem = viewModel.ExposureChoices.Contains(1.0) ? 1.0 : viewModel.ExposureChoices.First();
            ToBox.SelectedItem = viewModel.ExposureChoices.Contains(2.0) ? 2.0 : FromBox.SelectedItem;
            GainSeriesPanel.IsEnabled = viewModel.CanSetGain;
            if (!viewModel.CanSetGain) SeriesInfo.Text = "Le gain de cette caméra n'est pas réglable : un seul dark par pose.";
            foreach (var box in new[] { PctFromBox, PctToBox, PctStepBox, CountBox }) box.TextChanged += (s, e) => UpdateSeriesInfo();
            FromBox.SelectionChanged += (s, e) => UpdateSeriesInfo();
            ToBox.SelectionChanged += (s, e) => UpdateSeriesInfo();
        }

        private void Series_Changed(object sender, RoutedEventArgs e) => UpdateSeriesInfo();

        /// <summary>Gains de la série (en % du gain maximum), ou null si la série n'est pas demandée ; liste vide si les valeurs sont invalides.</summary>
        private System.Collections.Generic.List<int> SeriesGains() {
            if (SeriesBox.IsChecked != true || !viewModel.CanSetGain) return null;
            var list = new System.Collections.Generic.List<int>();
            var c = System.Globalization.CultureInfo.InvariantCulture;
            if (!double.TryParse(PctFromBox.Text.Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float, c, out double from) ||
                !double.TryParse(PctToBox.Text.Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float, c, out double to) ||
                !double.TryParse(PctStepBox.Text.Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float, c, out double step) ||
                from < 0 || to > 100 || from > to || step < 1) return list;
            int gMin = viewModel.GainMinimum, gMax = viewModel.GainMaximum;
            for (double p = from; p <= to + 1e-9; p += step) {
                int g = (int)System.Math.Round(gMin + (gMax - gMin) * p / 100.0);
                if (!list.Contains(g)) list.Add(g);
            }
            return list;
        }

        private void UpdateSeriesInfo() {
            if (!viewModel.CanSetGain) return;
            var gains = SeriesGains();
            if (gains == null) { SeriesInfo.Text = ""; return; }
            if (gains.Count == 0) { SeriesInfo.Text = "Valeurs de la série invalides (de 0 à 100 %, pas d'au moins 1 %)."; return; }
            int exposures = FromBox.SelectedItem != null && ToBox.SelectedItem != null
                ? viewModel.ExposureChoices.Count(x => x >= (double)FromBox.SelectedItem - 1e-9 && x <= (double)ToBox.SelectedItem + 1e-9) : 1;
            int.TryParse(CountBox.Text.Trim(), out int n);
            SeriesInfo.Text = $"{gains.Count} gains ({string.Join(", ", gains.Take(8))}{(gains.Count > 8 ? ", …" : "")}) × {exposures} pose(s) × {n} images = {gains.Count * exposures * n} images.";
        }

        protected override void OnClosing(CancelEventArgs e) {
            if (busy) e.Cancel = true;   // pas de fermeture pendant la prise : utiliser « Interrompre »
            base.OnClosing(e);
        }

        private async void Generate_Click(object sender, RoutedEventArgs e) {
            if (FromBox.SelectedItem == null || ToBox.SelectedItem == null) return;
            double from = (double)FromBox.SelectedItem;
            double to = (double)ToBox.SelectedItem;
            if (from > to) {
                MessageBox.Show(this, "La première pose doit être inférieure ou égale à la seconde.", "Créer des darks",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!int.TryParse(CountBox.Text.Trim(), out int count) || count < 1 || count > 100) {
                MessageBox.Show(this, "Le nombre de poses par durée doit être compris entre 1 et 100.", "Créer des darks",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (viewModel.Simulation) {
                MessageBox.Show(this, "Les darks ne peuvent pas être pris en mode simulation. Désactivez la simulation dans les paramètres avancés.",
                    "Créer des darks", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (viewModel.IsCalibrating || viewModel.IsGuiding) {
                MessageBox.Show(this, "Une calibration ou un guidage est en cours : arrêtez-le d'abord.", "Créer des darks",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var gains = SeriesGains();
            if (gains != null && gains.Count == 0) {
                MessageBox.Show(this, "Valeurs de la série invalides : de 0 à 100 % du gain maximum, pas d'au moins 1 %.", "Créer des darks", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var answer = MessageBox.Show(this,
                "Fermez ou obstruez l'ouverture de la lunette de guidage (bouchon sur l'objectif ou la caméra), " +
                "pour qu'aucune lumière n'arrive sur le capteur.\n\nCliquez sur OK quand c'est fait pour lancer la prise des darks.",
                "Créer des darks", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.OK) return;

            SetBusy(true);
            try {
                int created = await viewModel.GenerateDarksAsync(from, to, count, (text, fraction) =>
                    Dispatcher.BeginInvoke(new Action(() => { ProgressText.Text = text; Progress.Value = fraction; })), gains);
                ProgressText.Text = $"{created} dark(s) créé(s). Vous pouvez rouvrir la lunette de guidage.";
                Progress.Value = 1;
            } catch (OperationCanceledException) {
                ProgressText.Text = "Interrompu. Les darks déjà terminés sont conservés.";
            } catch (Exception ex) {
                ProgressText.Text = "Échec : " + ex.Message;
            } finally {
                SetBusy(false);
            }
        }

        private void SetBusy(bool value) {
            busy = value;
            GenerateButton.IsEnabled = !value;
            FromBox.IsEnabled = !value;
            ToBox.IsEnabled = !value;
            CountBox.IsEnabled = !value;
            GainSeriesPanel.IsEnabled = !value && viewModel.CanSetGain;
            StopButton.IsEnabled = value;
            CloseButton.IsEnabled = !value;
        }

        private void Stop_Click(object sender, RoutedEventArgs e) => viewModel.CancelCalibration();

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
