using System.Collections.Generic;

namespace NINA.Plugin.InternalGuider {
    /// <summary>
    /// Étoile de guidage choisie à la souris dans l'aperçu (clic gauche), ou retour à la sélection automatique (clic droit).
    /// Le choix vaut pour la session NINA ; il suit l'étoile image après image. Si l'étoile n'est plus trouvée au démarrage
    /// d'une calibration ou d'un guidage (autre champ, par exemple), on revient à la sélection automatique.
    /// </summary>
    public static class StarSelection {
        /// <summary>Distance maximale (pixels) entre la position mémorisée et l'étoile retrouvée.</summary>
        public const double MatchRadiusPx = 15;
        private const double MinEdgePx = 40;

        private static readonly object sync = new object();
        private static bool active;
        private static double x;
        private static double y;

        public static bool IsManual {
            get { lock (sync) return active; }
        }

        public static void Set(double sx, double sy) {
            lock (sync) { active = true; x = sx; y = sy; }
        }

        public static void Clear() {
            lock (sync) { active = false; }
        }

        private static StarInfo Nearest(IList<StarInfo> stars, double px, double py, double radius) {
            StarInfo best = null;
            double bestD = radius * radius;
            foreach (var s in stars) {
                double dx = s.X - px, dy = s.Y - py;
                double d = dx * dx + dy * dy;
                if (d <= bestD) { best = s; bestD = d; }
            }
            return best;
        }

        /// <summary>Raison pour laquelle l'étoile ne peut pas servir au guidage, ou null si elle convient.</summary>
        public static string Validate(StarInfo s, int width, int height) {
            if (s.Saturated) return "saturée";
            if (s.Snr < 8) return $"trop faible (SNR {s.Snr:0})";
            if (s.Hfd < 0.8 || s.Hfd > 10) return $"forme inadaptée (HFD {s.Hfd:0.0} px : pixel chaud ou étoile double ?)";
            if (s.X < MinEdgePx || s.Y < MinEdgePx || s.X > width - MinEdgePx || s.Y > height - MinEdgePx) return "trop près du bord de l'image";
            return null;
        }

        /// <summary>Aperçu : si un choix manuel existe, la croix verte se place sur l'étoile choisie.</summary>
        public static void ApplyToResult(DetectionResult r) {
            double px, py;
            lock (sync) {
                if (!active) return;
                px = x; py = y;
            }
            var s = Nearest(r.Stars, px, py, MatchRadiusPx);
            if (s != null) {
                r.Selected = s;
                Set(s.X, s.Y);   // suit la dérive lente de l'étoile
            }
        }

        /// <summary>Étoile de départ d'une calibration ou d'un guidage : le choix manuel s'il est retrouvé, sinon la sélection automatique.</summary>
        public static StarInfo Pick(IList<StarInfo> candidates) {
            double px, py;
            bool manual;
            lock (sync) { manual = active; px = x; py = y; }
            if (manual) {
                var s = Nearest(candidates, px, py, MatchRadiusPx);
                if (s != null && !s.Saturated) {
                    Set(s.X, s.Y);
                    return s;
                }
                Clear();   // étoile choisie introuvable (autre champ ?) : retour à l'automatique
            }
            return StarDetector.SelectGuideStar(candidates);
        }
    }
}
