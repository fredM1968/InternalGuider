using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace NINA.Plugin.InternalGuider {
    /// <summary>Ligne du tableau de bilan de calibration.</summary>
    public class ReportRow {
        public string Label { get; set; }
        public string Value { get; set; }
    }

    /// <summary>Bilan de calibration à la manière de PHD2 : graphique des déplacements de l'étoile et tableau des mesures.</summary>
    public static class CalibrationReport {
        private const double SiderealArcsecPerSec = 15.041;

        public static IList<ReportRow> Rows(CalibrationData d) {
            var rows = new List<ReportRow>();
            if (d == null) return rows;
            Action<string, string> add = (l, v) => rows.Add(new ReportRow { Label = l, Value = v });
            add("Date", d.Date.ToString("yyyy-MM-dd HH:mm:ss") + (d.Simulated ? " (simulation)" : "") + (d.Derived ? " (déduite après retournement)" : ""));
            if (d.RaSteps > 0 || d.DecSteps > 0) add("Pas RA / Dec", $"{d.RaSteps} / {d.DecSteps}" + (d.StepMs > 0 ? $" (pas de {d.StepMs} ms)" : ""));
            add("Angle caméra (RA)", $"{d.RaAngleDeg:0.0}°");
            add("Erreur d'orthogonalité", $"{Math.Abs(Math.Abs(d.OrthogonalityDeg) - 90):0.0}°");
            add("Vitesse RA", Rate(d, d.RaRatePxPerMs));
            add("Vitesse Dec", Rate(d, d.DecRatePxPerMs));
            add("Retour au départ", $"RA {d.RaReturnResidualPx:0.0} px · Dec {d.DecReturnResidualPx:0.0} px");
            if (d.RaReturnRatePxPerMs > 0 || d.DecReturnRatePxPerMs > 0) {
                add("Temps perdu RA", $"{d.RaOutLostMs:0} ms (aller) · {d.RaBackLostMs:0} ms (retour)");
                add("Temps perdu Dec", $"{d.DecOutLostMs:0} ms (aller) · {d.DecBackLostMs:0} ms (retour)");
            }
            if (d.PixelScale > 0) add("Échelle", $"{d.PixelScale:0.00} ″/px");
            if (d.FocalLengthMm > 0) add("Focale", $"{d.FocalLengthMm:0.#} mm");
            if (d.DeclinationDeg.HasValue) add("Déclinaison", $"{d.DeclinationDeg.Value:0.0}°");
            if (!string.IsNullOrEmpty(d.PierSide)) add("Côté du pier", d.PierSide);
            if (!string.IsNullOrEmpty(d.Warnings)) add(d.Derived ? "Note" : "Attention", d.Warnings);
            return rows;
        }

        private static string Rate(CalibrationData d, double pxPerMs) {
            string s = $"{pxPerMs * 1000:0.000} px/s";
            if (d.PixelScale > 0) {
                double a = pxPerMs * 1000 * d.PixelScale;
                s += $" · {a:0.00}″/s · {a / SiderealArcsecPerSec:0.00}× sidéral";
            }
            return s;
        }

        /// <summary>
        /// Déplacements de l'étoile dans le repère de l'image (x vers la droite, y vers le bas, comme l'aperçu) :
        /// RA en vert, Dec en rouge ; points pleins à l'aller, anneaux au retour. Null si la calibration ne contient pas de points.
        /// </summary>
        public static ImageSource Plot(CalibrationData d) {
            if (d == null) return null;
            var all = new List<CalPoint>();
            if (d.RaPoints != null) all.AddRange(d.RaPoints);
            if (d.DecPoints != null) all.AddRange(d.DecPoints);
            if (all.Count < 2) return null;

            const double W = 260, H = 260, M = 14;
            double minX = Math.Min(0, all.Min(p => p.X)), maxX = Math.Max(0, all.Max(p => p.X));
            double minY = Math.Min(0, all.Min(p => p.Y)), maxY = Math.Max(0, all.Max(p => p.Y));
            double span = Math.Max(8, Math.Max(maxX - minX, maxY - minY));
            double k = (W - 2 * M) / span;
            double offX = M + ((W - 2 * M) - (maxX - minX) * k) / 2 - minX * k;
            double offY = M + ((H - 2 * M) - (maxY - minY) * k) / 2 - minY * k;
            Func<double, double> sx = x => offX + x * k;
            Func<double, double> sy = y => offY + y * k;

            var g = new DrawingGroup();
            g.Children.Add(new GeometryDrawing(Brushes.Black, null, new RectangleGeometry(new Rect(0, 0, W, H))));
            var axis = new Pen(new SolidColorBrush(Color.FromRgb(90, 100, 104)), 0.8) { DashStyle = new DashStyle(new double[] { 1, 3 }, 0) };
            axis.Freeze();
            g.Children.Add(new GeometryDrawing(null, axis, new LineGeometry(new Point(0, sy(0)), new Point(W, sy(0)))));
            g.Children.Add(new GeometryDrawing(null, axis, new LineGeometry(new Point(sx(0), 0), new Point(sx(0), H))));

            var green = new SolidColorBrush(Color.FromRgb(96, 230, 96)); green.Freeze();
            var red = new SolidColorBrush(Color.FromRgb(240, 50, 50)); red.Freeze();
            Action<IList<CalPoint>, Brush> draw = (pts, brush) => {
                if (pts == null || pts.Count == 0) return;
                var pen = new Pen(brush, 1.2); pen.Freeze();
                var outPts = pts.Where(p => !p.Back).ToList();
                for (int i = 1; i < outPts.Count; i++)
                    g.Children.Add(new GeometryDrawing(null, pen, new LineGeometry(new Point(sx(outPts[i - 1].X), sy(outPts[i - 1].Y)), new Point(sx(outPts[i].X), sy(outPts[i].Y)))));
                foreach (var p in pts) {
                    var c = new Point(sx(p.X), sy(p.Y));
                    if (p.Back) g.Children.Add(new GeometryDrawing(null, pen, new EllipseGeometry(c, 2.6, 2.6)));
                    else g.Children.Add(new GeometryDrawing(brush, null, new EllipseGeometry(c, 2.4, 2.4)));
                }
            };
            draw(d.RaPoints, green);
            draw(d.DecPoints, red);
            g.Freeze();
            var img = new DrawingImage(g);
            img.Freeze();
            return img;
        }
    }
}
