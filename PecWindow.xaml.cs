using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace NINA.Plugin.InternalGuider {
    /// <summary>Analyse de la courbe PEC mémorisée : graphique sur deux tours de vis avec repères du cycle, puis bilan détaillé.</summary>
    public partial class PecWindow : Window {
        private const double SiderealArcsecPerSec = 15.041;

        private readonly PecCurve curve;
        private readonly double[] sample;     // une période échantillonnée
        private const int N = 720;
        private double vMin, vMax, vRms, tMin, tMax, slopeMax, tSlope;

        private static readonly Brush Fore = Hex("#FFE8E8E8");
        private static readonly Brush Dim = Hex("#FF9AA7AB");
        private static readonly Brush GridBrush = Hex("#FF3B4A4F");
        private static readonly Brush CurveBrush = Hex("#FF7FD0FF");
        private static readonly Brush CycleBrush = Hex("#FFF2C14E");
        private static readonly Brush MaxBrush = Hex("#FFE5675B");
        private static readonly Brush MinBrush = Hex("#FF5BD18B");

        private static Brush Hex(string hex) {
            var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex);
            b.Freeze();
            return b;
        }

        public PecWindow(PecCurve curve) {
            InitializeComponent();
            WindowTheme.Apply(this);
            this.curve = curve;
            sample = new double[N];
            Compute();
            SummaryText.Text = (PecStore.Simulation ? "SIMULATION · " : "") + curve.Describe();
            BuildReport();
            Loaded += (s, e) => DrawChart();
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
        private void ChartHost_SizeChanged(object sender, SizeChangedEventArgs e) => DrawChart();

        // ---------- Calculs ----------

        private void Compute() {
            double P = curve.PeriodSec;
            vMin = double.MaxValue; vMax = double.MinValue;
            double sum = 0, sum2 = 0;
            for (int i = 0; i < N; i++) {
                double v = curve.Value(P * i / N);
                sample[i] = v;
                sum += v; sum2 += v * v;
                if (v < vMin) { vMin = v; tMin = P * i / N; }
                if (v > vMax) { vMax = v; tMax = P * i / N; }
            }
            double mean = sum / N;
            vRms = Math.Sqrt(Math.Max(0, sum2 / N - mean * mean));
            slopeMax = 0;
            for (int i = 0; i < N; i++) {
                double s = Math.Abs(sample[(i + 1) % N] - sample[i]) / (P / N);
                if (s > slopeMax) { slopeMax = s; tSlope = P * (i + 0.5) / N; }
            }
        }

        // ---------- Graphique ----------

        private TextBlock Label(string text, double x, double y, Brush brush, double size = 12, bool centerX = false, bool bold = false) {
            var tb = new TextBlock { Text = text, Foreground = brush, FontSize = size, FontWeight = bold ? FontWeights.Bold : FontWeights.Normal };
            tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(tb, centerX ? x - tb.DesiredSize.Width / 2 : x);
            Canvas.SetTop(tb, y);
            ChartHost.Children.Add(tb);
            return tb;
        }

        private void DrawLine(double x1, double y1, double x2, double y2, Brush brush, double thickness = 1, bool dashed = false) {
            var l = new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = brush, StrokeThickness = thickness };
            if (dashed) l.StrokeDashArray = new DoubleCollection { 5, 4 };
            ChartHost.Children.Add(l);
        }

        private static double NiceStep(double range, int target) {
            double raw = range / target;
            double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
            double f = raw / mag;
            double nice = f < 1.5 ? 1 : f < 3.5 ? 2 : f < 7.5 ? 5 : 10;
            return nice * mag;
        }

        private void DrawChart() {
            ChartHost.Children.Clear();
            double W = ChartHost.ActualWidth, H = ChartHost.ActualHeight;
            if (W < 300 || H < 180 || curve == null) return;
            double P = curve.PeriodSec;
            const double left = 62, right = 20, top = 34, bottom = 62;
            double pw = W - left - right, ph = H - top - bottom;

            double ext = Math.Max(Math.Abs(vMin), Math.Abs(vMax)) * 1.25;
            if (ext < 0.5) ext = 0.5;
            double step = NiceStep(2 * ext, 6);
            ext = Math.Ceiling(ext / step) * step;
            Func<double, double> X = t => left + pw * t / (2 * P);
            Func<double, double> Y = v => top + ph * (1 - (v + ext) / (2 * ext));

            // Bandes : un tour sur deux légèrement éclairci, pour repérer les cycles
            var band = new Rectangle { Width = pw / 2, Height = ph, Fill = Hex("#14FFFFFF") };
            Canvas.SetLeft(band, left); Canvas.SetTop(band, top);
            ChartHost.Children.Add(band);

            // Grille horizontale et axe des ordonnées
            for (double v = -ext; v <= ext + 1e-9; v += step) {
                DrawLine(left, Y(v), left + pw, Y(v), Math.Abs(v) < 1e-9 ? Dim : GridBrush, Math.Abs(v) < 1e-9 ? 1.2 : 1);
                string txt = (v > 1e-9 ? "+" : "") + v.ToString("0.#") + "″";
                var tb = Label(txt, 0, Y(v) - 8, Dim, 11);
                Canvas.SetLeft(tb, left - 8 - tb.DesiredSize.Width);
            }
            var yTitle = Label("Erreur RA (″)", 6, 6, Dim, 11);

            // Repères du cycle : quarts de tour, début et fin de chaque tour de vis
            for (int q = 0; q <= 8; q++) {
                double t = P * q / 4.0;
                bool turn = q % 4 == 0;
                if (turn) DrawLine(X(t), top - 6, X(t), top + ph + 6, CycleBrush, 1.6, true);
                else DrawLine(X(t), top, X(t), top + ph, GridBrush, 1, true);
                Label(t.ToString("0") + " s", X(t), top + ph + 8, turn ? Fore : Dim, 11, true);
                int angle = (q % 4) * 90;
                Label(turn ? (q == 8 ? "360°" : "0°") : angle + "°", X(t), top + ph + 24, turn ? CycleBrush : Dim, 11, true);
            }
            Label("Temps (s)", left + pw - 50, top + ph + 42, Dim, 11);
            Label("Angle de la vis sans fin", left, top + ph + 42, CycleBrush, 11);
            Label("Tour 1", X(P / 2), top - 24, CycleBrush, 12, true, true);
            Label("Tour 2", X(1.5 * P), top - 24, CycleBrush, 12, true, true);

            // Courbe sur deux tours
            var poly = new Polyline { Stroke = CurveBrush, StrokeThickness = 2.2, StrokeLineJoin = PenLineJoin.Round };
            for (int i = 0; i <= 2 * N; i++) poly.Points.Add(new Point(X(P * i / N), Y(sample[i % N])));
            ChartHost.Children.Add(poly);

            // Maximum et minimum de chaque tour
            for (int c = 0; c < 2; c++) {
                MarkPoint(X(tMax + c * P), Y(vMax), MaxBrush, c == 0 ? $"max {vMax:+0.0;-0.0}″" : null, true);
                MarkPoint(X(tMin + c * P), Y(vMin), MinBrush, c == 0 ? $"min {vMin:+0.0;-0.0}″" : null, false);
            }
        }

        private void MarkPoint(double x, double y, Brush brush, string text, bool above) {
            var dot = new Ellipse { Width = 9, Height = 9, Fill = brush, Stroke = Hex("#FF263033"), StrokeThickness = 1 };
            Canvas.SetLeft(dot, x - 4.5); Canvas.SetTop(dot, y - 4.5);
            ChartHost.Children.Add(dot);
            if (text != null) Label(text, x + 8, above ? y - 22 : y + 6, brush, 12, false, true);
        }

        // ---------- Bilan ----------

        private void Heading(string text) {
            ReportPanel.Children.Add(new TextBlock { Text = text, FontWeight = FontWeights.Bold, FontSize = 14, Margin = new Thickness(0, 14, 0, 4), Foreground = Fore });
        }

        private void ReportLine(string text, string level = null) {
            Brush b = Fore;
            string prefix = "• ";
            if (level == "ok") { b = Hex("#FF5BD18B"); }
            else if (level == "warn") { b = Hex("#FFF2C14E"); }
            else if (level == "bad") { b = Hex("#FFE5675B"); }
            ReportPanel.Children.Add(new TextBlock { Text = prefix + text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 1, 0, 1), Foreground = b });
        }

        private static string F(double v, string fmt = "0.0") => v.ToString(fmt);

        private void BuildReport() {
            double P = curve.PeriodSec;
            double half = curve.AmplitudeArcsec;
            double ptp = vMax - vMin;

            // --- Courbe ---
            Heading("Courbe enregistrée");
            ReportLine($"Période (un tour de la vis sans fin) : {F(P, "0.0")} s. Comparez cette valeur à la période de votre vis (durée d'un tour de vis, donnée par le constructeur de la monture) : un écart important signale un enregistrement douteux.");
            ReportLine($"Amplitude : {F(ptp)}″ crête à crête (±{F(half)}″), valeur efficace (RMS) {F(vRms)}″.");
            ReportLine($"Maximum de {F(vMax, "+0.0;-0.0")}″ à {F(tMax, "0")} s du cycle ({F(360 * tMax / P, "0")}° de la vis), minimum de {F(vMin, "+0.0;-0.0")}″ à {F(tMin, "0")} s ({F(360 * tMin / P, "0")}°).");
            ReportLine($"Enregistrée le {curve.Recorded}, sur {F(curve.DurationSec / 60, "0")} min, soit {F(curve.DurationSec / P, "0.0")} tours de vis.");
            if (curve.HasDeclination) ReportLine($"Déclinaison pendant l'enregistrement : {F(curve.DeclinationDeg, "0")}°. En guidage, l'amplitude est automatiquement corrigée du cosinus de la déclinaison.");
            else ReportLine("Déclinaison inconnue à l'enregistrement : l'amplitude est appliquée telle quelle.", "warn");

            // --- Fiabilité ---
            Heading("Qualité de l'ajustement");
            double curveVar = vRms * vRms;
            double resVar = curve.ResidualRmsArcsec * curve.ResidualRmsArcsec;
            double explained = curveVar + resVar > 0 ? curveVar / (curveVar + resVar) : 0;
            ReportLine($"Bruit non expliqué par la courbe : {F(curve.ResidualRmsArcsec)}″ RMS (turbulence et erreurs aléatoires). La courbe explique environ {F(explained * 100, "0")} % de la variation de l'erreur RA enregistrée.",
                explained >= 0.6 ? "ok" : explained >= 0.3 ? "warn" : "bad");
            double turns = curve.DurationSec / P;
            if (turns >= 5) ReportLine($"Durée d'enregistrement suffisante ({F(turns, "0.0")} tours) : la courbe est une moyenne fiable sur plusieurs tours.", "ok");
            else if (turns >= 3) ReportLine($"Durée d'enregistrement correcte ({F(turns, "0.0")} tours). Un enregistrement plus long, de 5 tours ou plus, moyennerait mieux le bruit.", "warn");
            else ReportLine($"Enregistrement court ({F(turns, "0.0")} tours) : refaites-en un plus long, sur un ciel stable, pour une courbe plus fiable.", "bad");
            if (P < PecMath.MinPeriodSec * 1.1 || P > PecMath.MaxPeriodSec * 0.97) ReportLine($"La période est proche des bornes de recherche ({PecMath.MinPeriodSec:0} à {PecMath.MaxPeriodSec:0} s) : vérifiez qu'elle correspond bien à votre vis ou à votre engrenage.", "warn");

            // --- Harmoniques ---
            Heading("Composition de la courbe");
            int h = Math.Min(curve.Sin.Length, curve.Cos.Length);
            var amps = new double[h];
            double total = 0;
            for (int k = 0; k < h; k++) { amps[k] = Math.Sqrt(curve.Sin[k] * curve.Sin[k] + curve.Cos[k] * curve.Cos[k]); total += amps[k] * amps[k]; }
            for (int k = 0; k < h; k++) {
                double share = total > 0 ? amps[k] * amps[k] / total * 100 : 0;
                string name = k == 0 ? "Fondamentale" : $"Harmonique {k + 1}";
                ReportLine($"{name} (période {F(P / (k + 1), "0.0")} s) : ±{F(amps[k])}″, {F(share, "0")} % de l'énergie de la courbe.");
            }
            if (h >= 2 && amps[0] > 0 && amps[1] > 1.5 * amps[0])
                ReportLine("Le 2ᵉ harmonique domine la fondamentale : la vraie période de l'erreur pourrait être la moitié de celle trouvée (" + F(P / 2, "0.0") + " s). À vérifier avec la fiche de la monture.", "warn");
            else if (h >= 1 && total > 0 && amps[0] * amps[0] / total > 0.7)
                ReportLine("Courbe presque sinusoïdale : forme simple, facile à suivre pour l'anticipation.", "ok");

            // --- Impact sur le guidage ---
            Heading("Effet sur le guidage");
            if (half < 1) ReportLine($"Erreur périodique faible (±{F(half)}″) : le gain à attendre de la PEC est limité, le guidage la corrige déjà seul.", "ok");
            else if (half < 3) ReportLine($"Erreur périodique modérée (±{F(half)}″) : l'anticipation peut améliorer sensiblement le RMS en RA.", "warn");
            else ReportLine($"Erreur périodique forte (±{F(half)}″) : l'anticipation devrait apporter un net gain en RA.", "bad");
            ReportLine($"Sans PEC, cette erreur périodique contribue pour {F(vRms)}″ RMS à l'erreur RA. Avec une anticipation bien synchronisée, on vise à en retirer l'essentiel : le RMS RA ne peut cependant pas descendre sous le bruit de turbulence.");
            ReportLine($"Pente maximale de la courbe : {F(slopeMax, "0.00")}″/s vers {F(tSlope, "0")} s du cycle, soit {F(slopeMax / SiderealArcsecPerSec * 100, "0.0")} % de la vitesse sidérale. C'est la variation de vitesse que l'anticipation doit compenser à cet endroit.");
            if (slopeMax / SiderealArcsecPerSec > 0.15) ReportLine("Pente élevée : prévoyez une durée maximale d'impulsion RA suffisante, sinon les corrections seront écrêtées.", "warn");

            // --- Réglages actuels ---
            Heading("Réglages actuels");
            ReportLine(GuiderSettings.PecApply
                ? $"Application de la courbe : activée, force {GuiderSettings.PecStrengthPercent} %. La phase est retrouvée automatiquement pendant les {F(0.75 * P / 60, "0.#")} premières minutes de chaque guidage."
                : "Application de la courbe : désactivée (case « Appliquer la courbe » de l'onglet Guidage).", GuiderSettings.PecApply ? "ok" : "warn");
            ReportLine(GuiderSettings.PecRecord
                ? "Un nouvel enregistrement est demandé au prochain guidage : il remplacera cette courbe si une erreur périodique nette est trouvée."
                : "Aucun nouvel enregistrement demandé.");
            ReportLine("Un guidage d'au moins " + F(PecMath.MinRecordSec / 60, "0") + " minutes est nécessaire pour mettre la courbe à jour ; 5 tours de vis ou plus donnent une courbe plus fiable.");
        }
    }
}
