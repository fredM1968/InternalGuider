using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace NINA.Plugin.InternalGuider {
    public partial class PreviewWindow : Window {
        private readonly PreviewViewModel viewModel;

        public PreviewWindow(PreviewViewModel viewModel) {
            InitializeComponent();
            WindowTheme.Apply(this);
            this.viewModel = viewModel;
            DataContext = viewModel;

            // Taille par défaut limitée à la zone utile de l'écran (petits écrans, mini-PC en bureau à distance)
            Width = System.Math.Min(Width, SystemParameters.WorkArea.Width);
            Height = System.Math.Min(Height, SystemParameters.WorkArea.Height);

            // Tant que le guideur est connecté, la croix ne ferme pas la fenêtre (on ne pourrait plus la rouvrir sans déconnecter) :
            // elle la réduit dans la barre des tâches, d'où on la restaure. Elle ne se ferme vraiment qu'à la déconnexion du guideur ou à la fermeture de NINA.
            Closing += (s, e) => {
                if (AllowClose) return;
                var app = Application.Current;
                if (app == null || app.Dispatcher.HasShutdownStarted || (Owner != null && !Owner.IsLoaded)) return;
                e.Cancel = true;
                WindowState = WindowState.Minimized;
            };
        }

        /// <summary>Vrai uniquement quand le plugin ferme lui-même la fenêtre (déconnexion du guideur).</summary>
        public bool AllowClose { get; set; }

        private void GainBox_KeyDown(object sender, KeyEventArgs e) {
            if (e.Key == Key.Enter && sender is TextBox box) {
                box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                Keyboard.ClearFocus();
            }
        }

        private async void AutoGain_Click(object sender, RoutedEventArgs e) {
            AutoGainResult result;
            try {
                result = await viewModel.AutoGainAsync();
            } catch (System.Exception ex) {
                MessageBox.Show(this, ex.Message, "Gain automatique", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (result == null) return;
            if (!result.DarkMissing) {
                MessageBox.Show(this, result.Summary, "Gain automatique", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var answer = MessageBox.Show(this, result.Summary + "\n\nCréer ce dark maintenant (10 images) ? Il faudra fermer ou obstruer la lunette de guidage (bouchon sur l'objectif ou la caméra), puis cliquer sur Oui.",
                "Gain automatique", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;
            try {
                await viewModel.GenerateDarksAsync(result.Exposure, result.Exposure, 10, (text, fraction) => viewModel.ShowStatus("Dark : " + text));
                MessageBox.Show(this, "Dark créé. Retirez le bouchon : le gain et le dark sont prêts.", "Gain automatique", MessageBoxButton.OK, MessageBoxImage.Information);
            } catch (System.Exception ex) {
                MessageBox.Show(this, ex.Message, "Gain automatique", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async void Calibrate_Click(object sender, RoutedEventArgs e) {
            // Calibration réussie : le guidage démarre aussitôt si l'option est activée (une calibration annulée ou échouée ne lance rien)
            // Ctrl + clic : pointe d'abord l'équateur céleste, 1 h après le méridien
            bool slew = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            // Dans ce cas on ne guide pas derrière : on n'est pas sur le champ à photographier, même si l'option est cochée
            bool done = await viewModel.CalibrateAsync(slew);
            if (done && !slew && GuiderSettings.GuideAfterCalibration) await viewModel.GuideAsync();
        }

        private async void Guide_Click(object sender, RoutedEventArgs e) =>
            await viewModel.GuideOrCalibrateAsync(reason => MessageBox.Show(this,
                reason + "\n\nCalibrer maintenant, puis lancer le guidage ?",
                "Calibration nécessaire", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes);

        // --- Zoom, déplacement et choix de l'étoile dans l'aperçu ---

        private const double MaxZoom = 8;
        private bool pressed;
        private bool dragging;
        private Point pressPoint;
        private double pressShiftX;
        private double pressShiftY;

        private double Zoom => ZoomScale.ScaleX;

        /// <summary>Garde l'image zoomée dans le cadre (pas de bord vide) et met à jour le texte d'aide.</summary>
        private void ClampZoom() {
            double w = ImageHost.ActualWidth;
            double h = ImageHost.ActualHeight;
            double s = Zoom;
            ZoomShift.X = s <= 1.0001 ? 0 : System.Math.Min(0, System.Math.Max(w * (1 - s), ZoomShift.X));
            ZoomShift.Y = s <= 1.0001 ? 0 : System.Math.Min(0, System.Math.Max(h * (1 - s), ZoomShift.Y));
            // À fort zoom, on voit les pixels (sinon l'interpolation les floute)
            RenderOptions.SetBitmapScalingMode(StarImage, s >= 3 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.Linear);
            ZoomHint.Text = s <= 1.0001
                ? "Molette : zoom · glisser : déplacer · clic milieu : ajuster"
                : $"×{s:0.0} · molette : zoom · glisser : déplacer · clic milieu : ajuster";
        }

        private void ResetZoom() {
            ZoomScale.ScaleX = ZoomScale.ScaleY = 1;
            ZoomShift.X = ZoomShift.Y = 0;
            ClampZoom();
        }

        private void ImageHost_SizeChanged(object sender, SizeChangedEventArgs e) => ClampZoom();

        /// <summary>Molette : zoom centré sur le pointeur.</summary>
        private void ImageHost_MouseWheel(object sender, MouseWheelEventArgs e) {
            var p = e.GetPosition(ImageHost);
            double s1 = Zoom;
            double s2 = System.Math.Max(1, System.Math.Min(MaxZoom, s1 * (e.Delta > 0 ? 1.25 : 1 / 1.25)));
            if (System.Math.Abs(s2 - s1) < 1e-9) return;
            // Le point de l'image sous le pointeur doit rester sous le pointeur
            double lx = (p.X - ZoomShift.X) / s1;
            double ly = (p.Y - ZoomShift.Y) / s1;
            ZoomScale.ScaleX = ZoomScale.ScaleY = s2;
            ZoomShift.X = p.X - lx * s2;
            ZoomShift.Y = p.Y - ly * s2;
            ClampZoom();
            e.Handled = true;
        }

        private void ImageHost_LeftDown(object sender, MouseButtonEventArgs e) {
            pressed = true;
            dragging = false;
            pressPoint = e.GetPosition(ImageHost);
            pressShiftX = ZoomShift.X;
            pressShiftY = ZoomShift.Y;
            ImageHost.CaptureMouse();
        }

        private void ImageHost_MouseMove(object sender, MouseEventArgs e) {
            if (!pressed || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(ImageHost);
            if (!dragging && (System.Math.Abs(p.X - pressPoint.X) > 4 || System.Math.Abs(p.Y - pressPoint.Y) > 4)) dragging = true;
            if (dragging && Zoom > 1.0001) {
                ZoomShift.X = pressShiftX + (p.X - pressPoint.X);
                ZoomShift.Y = pressShiftY + (p.Y - pressPoint.Y);
                ClampZoom();
            }
        }

        /// <summary>Relâchement sans déplacement = clic : choix de l'étoile.</summary>
        private void ImageHost_LeftUp(object sender, MouseButtonEventArgs e) {
            bool wasClick = pressed && !dragging;
            pressed = false;
            dragging = false;
            ImageHost.ReleaseMouseCapture();
            if (wasClick) SelectStarFromClick(e);
        }

        private void ImageHost_MouseDown(object sender, MouseButtonEventArgs e) {
            if (e.ChangedButton == MouseButton.Middle) ResetZoom();
            else if (e.ChangedButton == MouseButton.Right) viewModel.ClearStarSelection();
        }

        /// <summary>Convertit la position du clic (dans l'image zoomée) en coordonnées de l'image analysée, puis choisit l'étoile.</summary>
        private void SelectStarFromClick(MouseButtonEventArgs e) {
            var result = viewModel.LastResult;
            if (result == null) return;

            // GetPosition(StarImage) tient compte du zoom : coordonnées dans l'élément Image non transformé.
            // Image affichée en « Uniform » : même échelle dans les deux sens, centrée dans l'élément.
            var p = e.GetPosition(StarImage);
            double scale = System.Math.Min(StarImage.ActualWidth / result.Width, StarImage.ActualHeight / result.Height);
            if (scale <= 0) return;
            double offsetX = (StarImage.ActualWidth - result.Width * scale) / 2;
            double offsetY = (StarImage.ActualHeight - result.Height * scale) / 2;
            viewModel.SelectStarAt((p.X - offsetX) / scale, (p.Y - offsetY) / scale);
        }

        private void Stop_Click(object sender, RoutedEventArgs e) => viewModel.StopCurrent();

        private void Advanced_Click(object sender, RoutedEventArgs e) {
            var window = new AdvancedWindow(viewModel) { Owner = this };
            window.ShowDialog();
        }

        private void Assistant_Click(object sender, RoutedEventArgs e) {
            var window = new AssistantWindow(viewModel) { Owner = this };
            window.ShowDialog();
        }

        private void GraphClear_Click(object sender, RoutedEventArgs e) => viewModel.ClearGraph();

        private void GraphUnit_Click(object sender, RoutedEventArgs e) => viewModel.ToggleGraphUnit();
    }
}
