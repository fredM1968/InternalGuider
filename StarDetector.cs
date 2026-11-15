using System;
using System.Collections.Generic;

namespace NINA.Plugin.InternalGuider {
    /// <summary>Une étoile détectée. Coordonnées en pixels de l'image analysée (centre d'un pixel = valeur entière).</summary>
    public class StarInfo {
        public double X { get; set; }
        public double Y { get; set; }
        /// <summary>Valeur maximale au-dessus du fond local.</summary>
        public double Peak { get; set; }
        /// <summary>Somme des valeurs au-dessus du fond local, dans l'ouverture.</summary>
        public double Flux { get; set; }
        /// <summary>Diamètre à mi-flux, en pixels.</summary>
        public double Hfd { get; set; }
        /// <summary>Rapport signal/bruit (bruit de fond seul, voir StarDetector.Measure).</summary>
        public double Snr { get; set; }
        public bool Saturated { get; set; }
    }

    public class DetectionResult {
        /// <summary>Image analysée (moyenne 2x2 glissante, taille (largeur-1) x (hauteur-1)).</summary>
        public float[] Gray;
        public int Width;
        public int Height;
        public double Background;
        public double Noise;
        public List<StarInfo> Stars = new List<StarInfo>();
        public StarInfo Selected;
        /// <summary>Étoiles utilisées pour le guidage (multi-étoiles), pour l'affichage ; null hors guidage.</summary>
        public List<StarInfo> Tracked;
    }

    /// <summary>
    /// Détection d'étoiles pour le guidage. Aucune dépendance à NINA : testable sur des images quelconques.
    ///
    /// Principe :
    /// 1. Moyenne 2x2 GLISSANTE de l'image brute : elle supprime la trame de Bayer d'une caméra couleur
    ///    (ASI224MC) sans réduire la résolution, donc sans sous-échantillonner l'étoile.
    /// 2. Fond = médiane, bruit = MAD x 1,4826, seuil = fond + 6 x bruit.
    /// 3. Candidats = maxima locaux 5x5 au-dessus du seuil, du plus brillant au moins brillant.
    /// 4. Mesure : fond local (médiane d'une couronne), centre de gravité pondéré dans une ouverture
    ///    circulaire, recentrée jusqu'à convergence, puis HFD et rapport signal/bruit.
    /// </summary>
    public static class StarDetector {
        public const int ApertureRadius = 5;
        public const double SigmaThreshold = 6.0;
        public const int MaxStars = 30;
        private const int MaxCandidates = 200;

        private struct Candidate {
            public int X;
            public int Y;
            public float V;
        }

        public static DetectionResult Analyze(ushort[] raw, int width, int height, int bitDepth = 16) {
            if (raw == null || width < 40 || height < 40 || raw.Length < width * height) {
                throw new ArgumentException("Image invalide pour la détection d'étoiles.");
            }

            var result = new DetectionResult();

            // 1. Moyenne 2x2 glissante
            int gw = width - 1;
            int gh = height - 1;
            var gray = new float[gw * gh];
            for (int y = 0; y < gh; y++) {
                int r0 = y * width;
                int r1 = r0 + width;
                int o = y * gw;
                for (int x = 0; x < gw; x++) {
                    gray[o + x] = (raw[r0 + x] + raw[r0 + x + 1] + raw[r1 + x] + raw[r1 + x + 1]) * 0.25f;
                }
            }
            result.Gray = gray;
            result.Width = gw;
            result.Height = gh;

            // 2. Fond et bruit (échantillon espacé d'un pas impair)
            int step = Math.Max(1, gray.Length / 40000);
            if (step % 2 == 0) step++;
            int count = (gray.Length + step - 1) / step;
            var sample = new float[count];
            int n = 0;
            for (int i = 0; i < gray.Length; i += step) sample[n++] = gray[i];
            Array.Sort(sample, 0, n);
            double median = sample[n / 2];
            var dev = new float[n];
            for (int i = 0; i < n; i++) dev[i] = (float)Math.Abs(sample[i] - median);
            Array.Sort(dev);
            double noise = Math.Max(1.4826 * dev[n / 2], 1.0);
            result.Background = median;
            result.Noise = noise;

            // Niveau de saturation : si la caméra est de moins de 16 bits mais que les données dépassent
            // sa plage, c'est qu'elles sont mises à l'échelle 16 bits.
            int depth = (bitDepth <= 0 || bitDepth > 16) ? 16 : bitDepth;
            double satLevel = (1 << depth) - 1;
            if (depth < 16) {
                int rawMax = 0;
                for (int i = 0; i < raw.Length; i++) if (raw[i] > rawMax) rawMax = raw[i];
                if (rawMax > satLevel) satLevel = 65535;
            }

            // 3. Candidats
            int margin = ApertureRadius + 5;
            double threshold = median + SigmaThreshold * noise;
            var cands = new List<Candidate>();
            for (int y = margin; y < gh - margin; y++) {
                int row = y * gw;
                for (int x = margin; x < gw - margin; x++) {
                    float v = gray[row + x];
                    if (v <= threshold) continue;
                    if (IsLocalMax(gray, gw, x, y, v)) cands.Add(new Candidate { X = x, Y = y, V = v });
                }
            }
            cands.Sort((a, b) => b.V.CompareTo(a.V));
            if (cands.Count > MaxCandidates) cands.RemoveRange(MaxCandidates, cands.Count - MaxCandidates);

            // 4. Mesure, du plus brillant au moins brillant
            double exclusion = (ApertureRadius + 1) * (ApertureRadius + 1);
            foreach (var c in cands) {
                bool near = false;
                foreach (var a in result.Stars) {
                    double dx = a.X - c.X;
                    double dy = a.Y - c.Y;
                    if (dx * dx + dy * dy < exclusion) { near = true; break; }
                }
                if (near) continue;

                var star = Measure(gray, gw, raw, width, height, c.X, c.Y, noise, satLevel);
                if (star == null) continue;
                result.Stars.Add(star);
                if (result.Stars.Count >= MaxStars) break;
            }

            result.Stars.Sort((a, b) => b.Flux.CompareTo(a.Flux));
            result.Selected = SelectGuideStar(result.Stars);
            return result;
        }

        /// <summary>
        /// Choisit l'étoile de guidage : non saturée, assez lumineuse, ni trop fine ni trop étalée,
        /// et isolée (pas de voisine comparable à moins de 15 pixels). Parmi elles, celle au meilleur rapport signal/bruit.
        /// </summary>
        public static StarInfo SelectGuideStar(IList<StarInfo> stars, double minSnr = 10, double minHfd = 0.8,
                                               double maxHfd = 10, double isolation = 15) {
            StarInfo best = null;
            foreach (var s in stars) {
                if (s.Saturated || s.Snr < minSnr || s.Hfd < minHfd || s.Hfd > maxHfd) continue;

                bool isolated = true;
                foreach (var o in stars) {
                    if (ReferenceEquals(o, s)) continue;
                    double dx = o.X - s.X;
                    double dy = o.Y - s.Y;
                    if (dx * dx + dy * dy < isolation * isolation && o.Flux > 0.1 * s.Flux) { isolated = false; break; }
                }
                if (!isolated) continue;

                if (best == null || s.Snr > best.Snr) best = s;
            }
            return best;
        }

        /// <summary>Maximum local strict sur 5x5 (à égalité, le premier dans l'ordre de balayage l'emporte).</summary>
        private static bool IsLocalMax(float[] g, int gw, int x, int y, float v) {
            for (int dy = -2; dy <= 2; dy++) {
                int row = (y + dy) * gw + x;
                for (int dx = -2; dx <= 2; dx++) {
                    if (dx == 0 && dy == 0) continue;
                    float nb = g[row + dx];
                    if (nb > v) return false;
                    if (nb == v && (dy < 0 || (dy == 0 && dx < 0))) return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Mesure une étoile autour du pixel (px, py). Rapport signal/bruit = flux signé / (bruit x racine du nombre de pixels
        /// de l'ouverture) : il ne tient compte que du bruit de fond, donc il surestime un peu pour une étoile très brillante.
        /// </summary>
        private static StarInfo Measure(float[] g, int gw, ushort[] raw, int w, int h, int px, int py,
                                        double noise, double satLevel) {
            int R = ApertureRadius;

            // Fond local : médiane d'une couronne (R+2 à R+4)
            int rin = R + 2;
            int rout = R + 4;
            var ring = new List<float>(160);
            for (int dy = -rout; dy <= rout; dy++) {
                for (int dx = -rout; dx <= rout; dx++) {
                    int d2 = dx * dx + dy * dy;
                    if (d2 < rin * rin || d2 > rout * rout) continue;
                    ring.Add(g[(py + dy) * gw + px + dx]);
                }
            }
            ring.Sort();
            double bg = ring[ring.Count / 2];

            // Centre de gravité, ouverture recentrée jusqu'à convergence
            double xc = px;
            double yc = py;
            double flux = 0;          // somme des valeurs positives : sert au centre de gravité
            double signedFlux = 0;    // somme signée : sert au rapport signal/bruit (le bruit seul donne zéro en moyenne)
            double peak = 0;
            int npix = 0;
            for (int iter = 0; iter < 5; iter++) {
                flux = 0;
                signedFlux = 0;
                peak = 0;
                npix = 0;
                double sx = 0;
                double sy = 0;
                int x0 = (int)Math.Floor(xc - R);
                int x1 = (int)Math.Ceiling(xc + R);
                int y0 = (int)Math.Floor(yc - R);
                int y1 = (int)Math.Ceiling(yc + R);
                for (int y = y0; y <= y1; y++) {
                    for (int x = x0; x <= x1; x++) {
                        double dx = x - xc;
                        double dy = y - yc;
                        if (dx * dx + dy * dy > R * R) continue;
                        npix++;
                        double wgt = g[y * gw + x] - bg;
                        if (wgt > peak) peak = wgt;
                        signedFlux += wgt;
                        if (wgt <= 0) continue;
                        flux += wgt;
                        sx += wgt * x;
                        sy += wgt * y;
                    }
                }
                if (flux <= 0) return null;

                double nx = sx / flux;
                double ny = sy / flux;
                bool converged = Math.Abs(nx - xc) < 0.005 && Math.Abs(ny - yc) < 0.005;
                xc = nx;
                yc = ny;
                if (Math.Abs(xc - px) > 3 || Math.Abs(yc - py) > 3) return null; // dérive vers une autre structure
                if (converged) break;
            }

            // HFD
            double hNum = 0;
            double hDen = 0;
            {
                int x0 = (int)Math.Floor(xc - R);
                int x1 = (int)Math.Ceiling(xc + R);
                int y0 = (int)Math.Floor(yc - R);
                int y1 = (int)Math.Ceiling(yc + R);
                for (int y = y0; y <= y1; y++) {
                    for (int x = x0; x <= x1; x++) {
                        double dx = x - xc;
                        double dy = y - yc;
                        double d2 = dx * dx + dy * dy;
                        if (d2 > R * R) continue;
                        double wgt = g[y * gw + x] - bg;
                        if (wgt <= 0) continue;
                        hNum += wgt * Math.Sqrt(d2);
                        hDen += wgt;
                    }
                }
            }
            if (hDen <= 0) return null;
            double hfd = 2.0 * hNum / hDen;

            // Le seuil de détection est global : en présence d'un dégradé de fond, on revérifie ici par rapport au fond local.
            if (peak < SigmaThreshold * noise) return null;
            double snr = signedFlux / (noise * Math.Sqrt(npix));
            if (snr < 5) return null;

            // Saturation : valeur brute maximale autour de l'étoile (le pixel gris (x, y) couvre les pixels bruts x..x+1, y..y+1)
            int cx = (int)Math.Round(xc);
            int cy = (int)Math.Round(yc);
            int rx0 = Math.Max(0, cx - R);
            int rx1 = Math.Min(w - 1, cx + R + 1);
            int ry0 = Math.Max(0, cy - R);
            int ry1 = Math.Min(h - 1, cy + R + 1);
            int max = 0;
            for (int y = ry0; y <= ry1; y++) {
                int row = y * w;
                for (int x = rx0; x <= rx1; x++) if (raw[row + x] > max) max = raw[row + x];
            }

            return new StarInfo {
                X = xc,
                Y = yc,
                Peak = peak,
                Flux = signedFlux,
                Hfd = hfd,
                Snr = snr,
                Saturated = max >= 0.97 * satLevel
            };
        }
    }
}
