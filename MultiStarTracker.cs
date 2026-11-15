using System;
using System.Collections.Generic;
using System.Linq;

namespace NINA.Plugin.InternalGuider {
    /// <summary>Mesure combinée d'une image : déplacement moyen des étoiles suivies par rapport à leur position de référence.</summary>
    public class StarMeasurement {
        /// <summary>Déplacement (pixels) par rapport aux positions de référence, moyenne pondérée des étoiles retenues.</summary>
        public double Dx;
        public double Dy;
        /// <summary>Étoile représentative pour l'affichage et le journal : l'étoile principale si elle est mesurée, sinon la plus lumineuse retenue.</summary>
        public StarInfo Reference;
        /// <summary>Étoiles retenues pour cette mesure (dans cette image).</summary>
        public List<StarInfo> Used = new List<StarInfo>();
        /// <summary>Flux actuel / flux normal (médiane sur les étoiles retrouvées) ; 1 = normal, proche de 0 = nuage épais.</summary>
        public double FluxRatio = 1;
        /// <summary>Événements à journaliser (étoile abandonnée, rejetée, ajoutée…).</summary>
        public List<string> Events = new List<string>();
    }

    /// <summary>
    /// Suit une ou plusieurs étoiles et combine leurs déplacements, comme le guidage multi-étoiles de PHD2 : la moyenne
    /// (pondérée par le rapport signal/bruit) de plusieurs étoiles réduit le bruit de seeing d'un facteur voisin de racine de N.
    /// Chaque étoile garde sa propre position de référence ; le décalage du dither est géré par le moteur de guidage.
    /// Avec une seule étoile, le comportement est celui du guidage classique.
    /// </summary>
    public class MultiStarTracker {
        private const double PrimarySearchPx = 20;
        private const double SecondarySearchPx = 10;
        private const int MaxMissingSecondary = 10;
        private const int MaxMissingPrimary = 10;
        private const double EdgePx = 12;
        private const double EligibleEdgePx = 30;
        private const double MinSeparationPx = 15;
        private const int ReplenishEvery = 20;
        private const double ReacquireMaxPx = 60;

        private class Tracked {
            public StarInfo Pos;
            public double LockX;
            public double LockY;
            public int Missing;
            public bool Seen;
            /// <summary>Flux normal de l'étoile (moyenne glissante hors nuage), pour détecter une chute de flux.</summary>
            public double BaseFlux;
        }

        private readonly List<Tracked> stars = new List<Tracked>();
        private readonly int maxStars;
        private readonly bool backup;       // guidage sur une étoile : une seconde étoile est suivie en réserve
        private readonly int target;        // nombre d'étoiles suivies (en réserve comprises)
        private int frame;
        private double lastDx;
        private double lastDy;

        public MultiStarTracker(int maxStars, bool backupStar = false) {
            this.maxStars = Math.Max(1, maxStars);
            backup = backupStar && this.maxStars == 1;
            target = backup ? 2 : this.maxStars;
        }

        public int MaxStars => maxStars;

        /// <summary>Vrai si une étoile de secours est suivie en plus de l'étoile de guidage (guidage mono-étoile).</summary>
        public bool HasBackup => backup && stars.Count > 1;

        /// <summary>Position de référence de l'étoile principale (coordonnées de l'image analysée).</summary>
        public double PrimaryLockX => stars.Count > 0 ? stars[0].LockX : 0;
        public double PrimaryLockY => stars.Count > 0 ? stars[0].LockY : 0;

        public int Count => stars.Count;

        /// <summary>Vrai si la mesure vient de l'étoile de secours (l'étoile de guidage est momentanément absente).</summary>
        public bool UsesBackup(StarMeasurement m) => backup && stars.Count > 1 && !ReferenceEquals(m.Reference, stars[0].Pos);

        /// <summary>Étoiles actuellement suivies (positions dans la dernière image).</summary>
        public List<StarInfo> CurrentStars => stars.Select(t => t.Pos).ToList();

        private static bool Eligible(StarInfo s, IList<StarInfo> all, int w, int h) {
            if (s.Saturated || s.Snr < 10 || s.Hfd < 0.8 || s.Hfd > 10) return false;
            if (s.X < EligibleEdgePx || s.Y < EligibleEdgePx || s.X > w - EligibleEdgePx || s.Y > h - EligibleEdgePx) return false;
            foreach (var o in all) {
                if (ReferenceEquals(o, s)) continue;
                double dx = o.X - s.X, dy = o.Y - s.Y;
                if (dx * dx + dy * dy < MinSeparationPx * MinSeparationPx && o.Flux > 0.1 * s.Flux) return false;
            }
            return true;
        }

        /// <summary>Choisit l'étoile principale (comme avant) puis, en mode multi-étoiles, les meilleures étoiles suivantes.</summary>
        public StarInfo Init(DetectionResult first, StarInfo primary) {
            stars.Clear();
            stars.Add(new Tracked { Pos = primary, LockX = primary.X, LockY = primary.Y, BaseFlux = primary.Flux });
            if (target > 1) {
                foreach (var s in first.Stars.Where(s => !ReferenceEquals(s, primary) && Eligible(s, first.Stars, first.Width, first.Height))
                                               .OrderByDescending(s => s.Snr)) {
                    if (stars.Count >= target) break;
                    if (stars.Any(t => Distance(t.Pos, s) < MinSeparationPx)) continue;
                    stars.Add(new Tracked { Pos = s, LockX = s.X, LockY = s.Y, BaseFlux = s.Flux });
                }
            }
            return primary;
        }

        private static double Distance(StarInfo a, StarInfo b) {
            double dx = a.X - b.X, dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>Associe chaque étoile suivie à une détection de l'image et renvoie la mesure combinée (null si aucune étoile n'est retrouvée).</summary>
        public StarMeasurement Update(DetectionResult r) {
            frame++;
            var m = new StarMeasurement();

            // Association une à une, du plus proche au plus éloigné
            var pairs = new List<(int Star, int Det, double D)>();
            for (int i = 0; i < stars.Count; i++) {
                double radius = i == 0 ? PrimarySearchPx : SecondarySearchPx;
                // Étoile perdue (nuage) : la monture a pu dériver pendant ce temps, on élargit la recherche peu à peu
                // mais on exige un flux comparable à celui de la dernière détection pour ne pas prendre une autre étoile.
                bool widen = stars[i].Missing > 0;
                if (widen) radius = Math.Min(ReacquireMaxPx, radius + 4.0 * stars[i].Missing);
                for (int j = 0; j < r.Stars.Count; j++) {
                    double d = Distance(stars[i].Pos, r.Stars[j]);
                    if (d > radius) continue;
                    if (widen && d > (i == 0 ? PrimarySearchPx : SecondarySearchPx)) {
                        double ratio = r.Stars[j].Flux / Math.Max(1e-9, stars[i].BaseFlux);
                        if (ratio < 0.25 || ratio > 4) continue;
                    }
                    pairs.Add((i, j, d));
                }
            }
            var usedDet = new HashSet<int>();
            foreach (var t in stars) t.Seen = false;
            foreach (var p in pairs.OrderBy(p => p.D)) {
                if (stars[p.Star].Seen || usedDet.Contains(p.Det)) continue;
                stars[p.Star].Seen = true;
                usedDet.Add(p.Det);
                stars[p.Star].Pos = r.Stars[p.Det];
                stars[p.Star].Missing = 0;
            }
            foreach (var t in stars) if (!t.Seen) t.Missing++;

            // Étoiles à abandonner : trop longtemps absentes, ou trop près du bord.
            // Si aucune étoile n'est visible (nuage), on ne retire rien : elles reviendront toutes ensemble.
            if (stars.Any(q => q.Seen))
            for (int i = stars.Count - 1; i >= 0; i--) {
                var t = stars[i];
                bool nearEdge = t.Seen && (t.Pos.X < EdgePx || t.Pos.Y < EdgePx || t.Pos.X > r.Width - EdgePx || t.Pos.Y > r.Height - EdgePx);
                bool gone = t.Missing > (i == 0 ? MaxMissingPrimary : MaxMissingSecondary);
                if (stars.Count > 1 && (nearEdge || gone)) {
                    m.Events.Add(backup && i == 0
                        ? $"étoile de guidage abandonnée ({(nearEdge ? "près du bord" : "perdue")}) : l'étoile de secours prend le relais"
                        : $"étoile {(i == 0 ? "principale " : "")}abandonnée ({(nearEdge ? "près du bord" : "perdue")}), {stars.Count - 1} étoile(s) restante(s)");
                    stars.RemoveAt(i);
                }
            }

            var seen = stars.Where(t => t.Seen).ToList();
            if (seen.Count == 0) return null;
            m.FluxRatio = Median(seen.Select(t => t.Pos.Flux / Math.Max(1e-9, t.BaseFlux)));
            // Étoile de secours : la mesure vient de l'étoile de guidage seule ; la réserve ne sert que si elle est absente
            // (chaque étoile a sa propre référence prise au départ : le déplacement mesuré est le même, pas de saut au changement)
            if (backup && seen.Count > 1) seen = new List<Tracked> { seen[0] };

            // Déplacements individuels
            var disp = seen.Select(t => (X: t.Pos.X - t.LockX, Y: t.Pos.Y - t.LockY, W: Math.Max(10.0, Math.Min(300.0, t.Pos.Snr)), T: t)).ToList();
            var inliers = disp;

            if (disp.Count >= 3) {
                double mx = Median(disp.Select(v => v.X)), my = Median(disp.Select(v => v.Y));
                var dist = disp.Select(v => Math.Sqrt((v.X - mx) * (v.X - mx) + (v.Y - my) * (v.Y - my))).ToList();
                double thr = Math.Max(1.0, 3 * 1.4826 * Median(dist));
                inliers = disp.Where((v, i) => dist[i] <= thr).ToList();
                if (inliers.Count < disp.Count) m.Events.Add($"{disp.Count - inliers.Count} étoile(s) écartée(s) de la mesure (écart > {thr:0.0} px)");
            } else if (disp.Count == 2) {
                double sep = Math.Sqrt(Math.Pow(disp[0].X - disp[1].X, 2) + Math.Pow(disp[0].Y - disp[1].Y, 2));
                if (sep > 2.5) {
                    inliers = new List<(double X, double Y, double W, Tracked T)> { disp[0].W >= disp[1].W ? disp[0] : disp[1] };
                    m.Events.Add($"deux étoiles en désaccord de {sep:0.0} px : la plus brillante seule est utilisée");
                }
            }

            double sw = inliers.Sum(v => v.W);
            m.Dx = inliers.Sum(v => v.X * v.W) / sw;
            m.Dy = inliers.Sum(v => v.Y * v.W) / sw;
            lastDx = m.Dx;
            lastDy = m.Dy;
            m.Used = inliers.Select(v => v.T.Pos).ToList();
            var primaryInliers = inliers.FirstOrDefault(v => ReferenceEquals(v.T, stars[0]));
            m.Reference = primaryInliers.T != null ? primaryInliers.T.Pos : inliers.OrderByDescending(v => v.W).First().T.Pos;

            // Renouvellement : on complète la liste quand des étoiles ont disparu
            if (target > 1 && stars.Count < target && frame % ReplenishEvery == 0) {
                var cand = r.Stars.Where((s, j) => !usedDet.Contains(j) && Eligible(s, r.Stars, r.Width, r.Height) &&
                                                    stars.All(t => Distance(t.Pos, s) >= MinSeparationPx))
                                  .OrderByDescending(s => s.Snr).FirstOrDefault();
                if (cand != null) {
                    // Référence choisie pour que cette étoile affiche exactement le déplacement actuel : pas de saut de mesure
                    stars.Add(new Tracked { Pos = cand, LockX = cand.X - lastDx, LockY = cand.Y - lastDy, Seen = true, BaseFlux = cand.Flux });
                    m.Events.Add(backup ? $"étoile de secours choisie (SNR {cand.Snr:0})" : $"étoile ajoutée (SNR {cand.Snr:0}), {stars.Count} étoile(s) suivie(s)");
                }
            }
            return m;
        }

        /// <summary>Met à jour le flux normal des étoiles vues (à appeler seulement quand le ciel est clair).</summary>
        public void AdoptFlux() {
            foreach (var t in stars) {
                if (!t.Seen) continue;
                t.BaseFlux = 0.9 * t.BaseFlux + 0.1 * t.Pos.Flux;
            }
        }

        private static double Median(IEnumerable<double> values) {
            var a = values.OrderBy(v => v).ToArray();
            if (a.Length == 0) return 0;
            return a.Length % 2 == 1 ? a[a.Length / 2] : 0.5 * (a[a.Length / 2 - 1] + a[a.Length / 2]);
        }
    }
}
