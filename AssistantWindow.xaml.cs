using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Windows;

namespace NINA.Plugin.InternalGuider {
    /// <summary>Assistant de guidage : mesure sans correction, rapport et réglages suggérés.</summary>
    public partial class AssistantWindow : Window {
        private readonly PreviewViewModel viewModel;
        private AssistantResult result;
        private bool busy;

        private static readonly int[] Minutes = { 2, 5, 10, 15 };

        public AssistantWindow(PreviewViewModel viewModel) {
            InitializeComponent();
            WindowTheme.Apply(this);
            this.viewModel = viewModel;
            var items = new List<string>();
            foreach (int m in Minutes) items.Add(m + " minutes");
            DurationBox.ItemsSource = items;
            DurationBox.SelectedIndex = 1;
        }

        protected override void OnClosing(CancelEventArgs e) {
            if (busy) e.Cancel = true;   // pas de fermeture pendant la mesure : utiliser « Interrompre »
            base.OnClosing(e);
        }

        private async void Start_Click(object sender, RoutedEventArgs e) {
            if (viewModel.IsCalibrating || viewModel.IsGuiding) {
                MessageBox.Show(this, "Une calibration ou un guidage est en cours : arrêtez-le d'abord.", "Assistant de guidage",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            int minutes = Minutes[Math.Max(0, Math.Min(Minutes.Length - 1, DurationBox.SelectedIndex))];
            bool backlash = BacklashBox.IsChecked == true;

            SetBusy(true);
            result = null;
            ApplyButton.IsEnabled = false;
            ReportBox.Text = "";
            ProgressText.Text = "démarrage…";
            Progress.Value = 0;
            try {
                result = await viewModel.RunAssistantAsync(minutes * 60, backlash, (text, fraction) =>
                    Dispatcher.BeginInvoke(new Action(() => { ProgressText.Text = text; Progress.Value = fraction; })));
                if (result != null) {
                    ReportBox.Text = result.Report;
                    ProgressText.Text = "Terminé. Lisez le rapport, puis appliquez les réglages suggérés si vous le souhaitez.";
                    Progress.Value = 1;
                    ApplyButton.IsEnabled = result.HasRecommendations;
                }
            } catch (OperationCanceledException) {
                ProgressText.Text = "Interrompu.";
            } catch (CalibrationException ex) {
                ProgressText.Text = "Impossible : " + ex.Message;
            } catch (Exception ex) {
                ProgressText.Text = "Échec : " + ex.Message;
            } finally {
                SetBusy(false);
            }
        }

        private void Apply_Click(object sender, RoutedEventArgs e) {
            if (result == null) return;
            viewModel.ApplyAssistantResult(result);
            ProgressText.Text = "Réglages appliqués (mouvement minimal, impulsions maximales" + (result.DecMode.HasValue ? ", sens Dec" : "") + ").";
            ApplyButton.IsEnabled = false;
        }

        private void SetBusy(bool value) {
            busy = value;
            StartButton.IsEnabled = !value;
            DurationBox.IsEnabled = !value;
            BacklashBox.IsEnabled = !value;
            StopButton.IsEnabled = value;
            CloseButton.IsEnabled = !value;
        }

        private void Stop_Click(object sender, RoutedEventArgs e) => viewModel.CancelCalibration();

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
