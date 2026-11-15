using System;
using System.Collections.Generic;

namespace NINA.Plugin.InternalGuider {
    /// <summary>
    /// Ciel synthétique pour tester le plugin en intérieur : étoiles gaussiennes (FWHM d'environ 1,2 pixel),
    /// trame de Bayer RGGB, léger dégradé de fond, bruit gaussien, et un petit mouvement d'ensemble
    /// (dérive lente + turbulence) qui permet de voir bouger la croix de repérage.
    /// </summary>
    public static class SyntheticSky {
        private const double Sigma = 0.5;       // écart-type de l'étoile, en pixels du capteur
        private const double Background = 800;
        private const double Noise = 25;
        private static readonly double[] Bayer = { 0.85, 1.0, 1.0, 0.7 }; // R, G, G, B

        public static ushort[] Generate(int w, int h, int frame) => Generate(w, h, frame, 0, 0, true);

        /// <summary>offsetX/offsetY : décalage du ciel en pixels (monture simulée) ; drift : ajoute la dérive lente.</summary>
        public static ushort[] Generate(int w, int h, int frame, double offsetX, double offsetY, bool drift) =>
            Generate(w, h, frame, offsetX, offsetY, drift, 1.0);

        /// <summary>transmission : part du flux des étoiles qui passe (1 = ciel clair, 0,3 = voile, 0,02 = nuage épais).</summary>
        public static ushort[] Generate(int w, int h, int frame, double offsetX, double offsetY, bool drift, double transmission) {
            var raw = new ushort[w * h];

            // Fond, dégradé et bruit (Box-Muller, deux valeurs par tirage)
            var noiseRng = new Random(frame * 7919 + 13);
            for (int y = 0; y < h; y++) {
                int row = y * w;
                for (int x = 0; x < w; x += 2) {
                    double u1 = 1.0 - noiseRng.NextDouble();
                    double u2 = noiseRng.NextDouble();
                    double mag = Noise * Math.Sqrt(-2.0 * Math.Log(u1));
                    double g0 = mag * Math.Cos(2.0 * Math.PI * u2);
                    double g1 = mag * Math.Sin(2.0 * Math.PI * u2);
                    double grad0 = Background + 80.0 * x / w;
                    raw[row + x] = Clamp(grad0 + g0);
                    if (x + 1 < w) raw[row + x + 1] = Clamp(Background + 80.0 * (x + 1) / w + g1);
                }
            }

            // Mouvement d'ensemble : dérive lente + turbulence
            var motionRng = new Random(frame * 104729 + 7);
            double shiftX = offsetX + (drift ? 0.8 * Math.Sin(frame * 0.05) : 0) + 0.12 * Gauss(motionRng);
            double shiftY = offsetY + (drift ? 0.6 * Math.Cos(frame * 0.04) : 0) + 0.12 * Gauss(motionRng);

            // Étoiles (positions fixes pour une taille d'image donnée)
            var starRng = new Random(42);
            int count = 40;
            for (int i = 0; i < count; i++) {
                double sx = 30 + starRng.NextDouble() * (w - 60) + shiftX;
                double sy = 30 + starRng.NextDouble() * (h - 60) + shiftY;
                double flux = Math.Min(300000.0, 500.0 * Math.Pow(Math.Max(starRng.NextDouble(), 0.02), -1.3));
                AddStar(raw, w, h, sx, sy, flux * transmission);
            }
            return raw;
        }

        private static void AddStar(ushort[] raw, int w, int h, double sx, double sy, double flux) {
            int x0 = Math.Max(0, (int)sx - 6);
            int x1 = Math.Min(w - 1, (int)sx + 7);
            int y0 = Math.Max(0, (int)sy - 6);
            int y1 = Math.Min(h - 1, (int)sy + 7);
            double norm = flux / (2.0 * Math.PI * Sigma * Sigma);

            for (int y = y0; y <= y1; y++) {
                for (int x = x0; x <= x1; x++) {
                    // Intégration sur la surface du pixel : 3 x 3 sous-échantillons
                    double acc = 0;
                    for (int oy = -1; oy <= 1; oy++) {
                        for (int ox = -1; ox <= 1; ox++) {
                            double dx = x + ox / 3.0 - sx;
                            double dy = y + oy / 3.0 - sy;
                            acc += Math.Exp(-(dx * dx + dy * dy) / (2.0 * Sigma * Sigma));
                        }
                    }
                    double value = norm * acc / 9.0 * Bayer[(y & 1) * 2 + (x & 1)];
                    int idx = y * w + x;
                    raw[idx] = Clamp(raw[idx] + value);
                }
            }
        }

        private static ushort Clamp(double v) {
            if (v < 0) return 0;
            if (v > 65535) return 65535;
            return (ushort)Math.Round(v);
        }

        private static double Gauss(Random r) {
            double u1 = 1.0 - r.NextDouble();
            double u2 = r.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }
    }
}
