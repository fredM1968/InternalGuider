using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NINA.Plugin.InternalGuider {
    /// <summary>Résultat du « profil de l'étoile » : zoom, courbe radiale, historique du HFD et texte de mesures.</summary>
    public class StarProfileData {
        public ImageSource Zoom;
        public ImageSource Curve;
        public ImageSource History;
        public string Text = "";
    }

    /// <summary>
    /// Équivalent du « Profil de l'étoile » de PHD2 : zoom sur l'étoile de guidage, profil radial avec gaussienne de même FWHM,
    /// historique du HFD (pour suivre la mise au point) et mesures (HFD, FWHM, pic, SNR, saturation).
    /// Aucune dépendance à NINA.
    /// </summary>
    public class StarProfileRenderer {
        private const int Half = 10;             // le zoom couvre (2 x Half + 1)² pixels
        private const double CurveRadius = 8;    // rayon affiché du profil radial (px)
        private const int HistorySize = 120;
        private readonly List<double> hfdHistory = new List<double>();
        private readonly List<double> peakHistory = new List<double>();

        public void Reset() { hfdHistory.Clear(); peakHistory.Clear(); }

        public StarProfileData Render(DetectionResult r, double pixelScale) {
            var d = new StarProfileData();
            var s = r?.Selected;
            if (r == null || s == null || r.Gray == null) {
                d.Text = "Aucune étoile de guidage";
                return d;
            }
            hfdHistory.Add(s.Hfd);
            peakHistory.Add(s.Peak);
            if (hfdHistory.Count > HistorySize) { hfdHistory.RemoveAt(0); peakHistory.RemoveAt(0); }

            int gw = r.Width, gh = r.Height;
            int cx = (int)Math.Round(s.X), cy = (int)Math.Round(s.Y);
            int size = 2 * Half + 1;
            var crop = new float[size * size];
            float mx = float.MinValue;
            for (int j = 0; j < size; j++) {
                for (int i = 0; i < size; i++) {
                    int x = cx - Half + i, y = cy - Half + j;
                    float v = (x >= 0 && y >= 0 && x < gw && y < gh) ? r.Gray[y * gw + x] : (float)r.Background;
                    crop[j * size + i] = v;
                    if (v > mx) mx = v;
                }
            }
            double bg = r.Background;
            double top = Math.Max(mx, bg + 5 * r.Noise);
            double span = Math.Max(1e-6, top - bg);

            // Zoom : linéaire entre le fond et le maximum local (contraste propre à l'étoile), racine pour garder les ailes visibles
            var bytes = new byte[size * size];
            for (int k = 0; k < bytes.Length; k++) {
                double v = (crop[k] - bg) / span;
                v = v < 0 ? 0 : (v > 1 ? 1 : v);
                bytes[k] = (byte)(255 * Math.Sqrt(v));
            }
            var bmp = BitmapSource.Create(size, size, 96, 96, PixelFormats.Gray8, null, bytes, size);
            bmp.Freeze();
            var zg = new DrawingGroup();
            zg.Children.Add(new ImageDrawing(bmp, new Rect(0, 0, size, size)));
            // Repère du centre mesuré (coordonnées du zoom : le pixel entier cx occupe [Half, Half + 1])
            double mxp = s.X - cx + Half + 0.5, myp = s.Y - cy + Half + 0.5;
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(200, 0, 255, 90)), 0.08);
            pen.Freeze();
            zg.Children.Add(new GeometryDrawing(null, pen, new LineGeometry(new Point(mxp - 1.2, myp), new Point(mxp - 0.5, myp))));
            zg.Children.Add(new GeometryDrawing(null, pen, new LineGeometry(new Point(mxp + 0.5, myp), new Point(mxp + 1.2, myp))));
            zg.Children.Add(new GeometryDrawing(null, pen, new LineGeometry(new Point(mxp, myp - 1.2), new Point(mxp, myp - 0.5))));
            zg.Children.Add(new GeometryDrawing(null, pen, new LineGeometry(new Point(mxp, myp + 0.5), new Point(mxp, myp + 1.2))));
            zg.Freeze();
            var zoom = new DrawingImage(zg);
            zoom.Freeze();
            d.Zoom = zoom;

            // Profil radial : chaque pixel du voisinage en (distance au centre, valeur - fond), gaussienne de même FWHM en cyan
            double peakVal = Math.Max(1e-6, s.Peak > 0 ? s.Peak : top - bg);
            double fwhm = 0.94 * s.Hfd;
            double sigma = Math.Max(0.3, fwhm / 2.3548);
            const double W = 260, H = 120, L = 6, B = 6;
            var cg = new DrawingGroup();
            cg.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(20, 26, 28)), null, new RectangleGeometry(new Rect(0, 0, W, H))));
            Func<double, double> px = rr => L + (W - 2 * L) * rr / CurveRadius;
            Func<double, double> py = vv => H - B - (H - 2 * B) * Math.Max(-0.1, Math.Min(1.1, vv / peakVal)) / 1.1;
            var grid = new Pen(new SolidColorBrush(Color.FromRgb(70, 82, 86)), 0.8); grid.Freeze();
            var halfPen = new Pen(new SolidColorBrush(Color.FromRgb(255, 200, 0)), 0.8) { DashStyle = DashStyles.Dash }; halfPen.Freeze();
            cg.Children.Add(new GeometryDrawing(null, grid, new LineGeometry(new Point(L, py(0)), new Point(W - L, py(0)))));
            cg.Children.Add(new GeometryDrawing(null, halfPen, new LineGeometry(new Point(L, py(peakVal / 2)), new Point(W - L, py(peakVal / 2)))));
            var gauss = new PathGeometry();
            var fig = new PathFigure { StartPoint = new Point(px(0), py(peakVal)) };
            for (int k = 1; k <= 60; k++) {
                double rr = CurveRadius * k / 60.0;
                fig.Segments.Add(new LineSegment(new Point(px(rr), py(peakVal * Math.Exp(-rr * rr / (2 * sigma * sigma)))), true));
            }
            gauss.Figures.Add(fig);
            var gpen = new Pen(new SolidColorBrush(Color.FromRgb(0, 200, 255)), 1.3); gpen.Freeze();
            cg.Children.Add(new GeometryDrawing(null, gpen, gauss));
            var dot = new SolidColorBrush(Color.FromRgb(255, 255, 255)); dot.Freeze();
            for (int j = 0; j < size; j++) {
                for (int i = 0; i < size; i++) {
                    double dx = cx - Half + i - s.X, dy = cy - Half + j - s.Y;
                    double rr = Math.Sqrt(dx * dx + dy * dy);
                    if (rr > CurveRadius) continue;
                    cg.Children.Add(new GeometryDrawing(dot, null, new EllipseGeometry(new Point(px(rr), py(crop[j * size + i] - bg)), 1.8, 1.8)));
                }
            }
            cg.Freeze();
            var curve = new DrawingImage(cg);
            curve.Freeze();
            d.Curve = curve;

            // Historique du HFD (ligne verte), échelle automatique
            const double HW = 260, HH = 50;
            var hg = new DrawingGroup();
            hg.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(20, 26, 28)), null, new RectangleGeometry(new Rect(0, 0, HW, HH))));
            if (hfdHistory.Count >= 2) {
                double lo = hfdHistory.Min(), hi = hfdHistory.Max();
                if (hi - lo < 0.3) { double m = (hi + lo) / 2; lo = m - 0.15; hi = m + 0.15; }
                var pf = new PathFigure { StartPoint = new Point(0, 0) };
                for (int k = 0; k < hfdHistory.Count; k++) {
                    var p = new Point(HW * k / (HistorySize - 1.0) , 4 + (HH - 8) * (1 - (hfdHistory[k] - lo) / (hi - lo)));
                    if (k == 0) pf.StartPoint = p; else pf.Segments.Add(new LineSegment(p, true));
                }
                var pg = new PathGeometry(); pg.Figures.Add(pf);
                var hpen = new Pen(new SolidColorBrush(Color.FromRgb(0, 255, 90)), 1.3); hpen.Freeze();
                hg.Children.Add(new GeometryDrawing(null, hpen, pg));
            }
            hg.Freeze();
            var hist = new DrawingImage(hg);
            hist.Freeze();
            d.History = hist;

            // Texte
            string asec(double v) => pixelScale > 0 ? $" ({v * pixelScale:0.0}″)" : "";
            string t = $"HFD : {s.Hfd:0.00} px{asec(s.Hfd)}\nFWHM : {fwhm:0.00} px{asec(fwhm)}\nPic : {s.Peak:0} · SNR : {s.Snr:0}";
            if (hfdHistory.Count >= 5) t += $"\nHFD récent : min {hfdHistory.Min():0.00} · max {hfdHistory.Max():0.00}";
            if (s.Saturated) t += "\nÉTOILE SATURÉE : baissez le gain ou la pose";
            d.Text = t;
            return d;
        }
    }
}
