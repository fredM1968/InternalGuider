using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NINA.Astrometry;
using NINA.Core.Enum;
using NINA.Equipment.Interfaces.Mediator;

namespace NINA.Plugin.InternalGuider {
    public enum GuideDir {
        North,
        South,
        East,
        West
    }

    /// <summary>Ce dont le guidage a besoin de la monture : envoyer une impulsion de correction.</summary>
    public interface IGuideMount {
        /// <summary>Vrai si la monture est utilisable (connectée dans NINA, ou simulée).</summary>
        bool IsAvailable { get; }
        double? DeclinationDegrees { get; }
        string PierSide { get; }
        /// <summary>Temps (heures solaires) avant le passage au méridien : positif = pas encore passé, négatif = déjà passé ; null si inconnu.</summary>
        double? HoursToMeridian { get; }

        /// <summary>Envoie une impulsion et ne rend la main qu'une fois sa durée écoulée.</summary>
        Task PulseAsync(GuideDir direction, int milliseconds, CancellationToken token);

        /// <summary>Pointe l'équateur céleste (Dec 0°), 1 h après le méridien (angle horaire +1 h), pour calibrer dans la meilleure zone du ciel.
        /// Rend la main à la fin du pointage ; lève une exception avec un message clair si la monture ne peut pas pointer.</summary>
        Task SlewToCalibrationFieldAsync(Action<string> progress, CancellationToken token);
    }

    /// <summary>Monture réelle, pilotée par le médiateur Monture de NINA (la monture se connecte dans l'onglet Monture).</summary>
    public class NinaMount : IGuideMount {
        private readonly ITelescopeMediator mediator;

        public NinaMount(ITelescopeMediator mediator) {
            this.mediator = mediator;
        }

        public bool IsAvailable {
            get {
                try { return mediator?.GetInfo()?.Connected == true; } catch (Exception) { return false; }
            }
        }

        public double? HoursToMeridian {
            get {
                try {
                    var info = mediator?.GetInfo();
                    if (info == null || !info.Connected) return null;
                    double h = info.RightAscension - info.SiderealTime;   // heures sidérales avant le méridien
                    while (h > 12) h -= 24;
                    while (h <= -12) h += 24;
                    return h * 0.99727;                                   // en heures solaires
                } catch (Exception) { return null; }
            }
        }

        public double? DeclinationDegrees {
            get {
                try { return mediator?.GetInfo()?.Declination; } catch (Exception) { return null; }
            }
        }

        public string PierSide {
            get {
                try { return mediator?.GetInfo()?.SideOfPier.ToString(); } catch (Exception) { return null; }
            }
        }

        /// <summary>Angle horaire visé pour la calibration (heures) : 1 h après le méridien.</summary>
        public const double CalibrationHourAngle = 1.0;

        public async Task SlewToCalibrationFieldAsync(Action<string> progress, CancellationToken token) {
            var info = mediator?.GetInfo();
            if (info == null || !info.Connected) throw new InvalidOperationException("la monture n'est pas connectée dans NINA.");
            if (info.AtPark) throw new InvalidOperationException("la monture est parquée : déparquez-la avant de calibrer.");

            // RA (JNow) = heure sidérale locale - angle horaire ; Dec = 0
            double ra = info.SiderealTime - CalibrationHourAngle;
            while (ra < 0) ra += 24;
            while (ra >= 24) ra -= 24;
            progress?.Invoke("pointage de l'équateur céleste, 1 h après le méridien…");
            var target = new Coordinates(Angle.ByHours(ra), Angle.ByDegree(0), Epoch.JNOW);
            bool ok = await mediator.SlewToCoordinatesAsync(target, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (!ok) throw new InvalidOperationException("le pointage de la monture a échoué (voir le journal de NINA).");
            progress?.Invoke("pointage terminé, stabilisation de la monture…");
            await Task.Delay(4000, token).ConfigureAwait(false);
        }

        public async Task PulseAsync(GuideDir direction, int milliseconds, CancellationToken token) {
            GuideDirections d;
            switch (direction) {
                case GuideDir.North: d = GuideDirections.guideNorth; break;
                case GuideDir.South: d = GuideDirections.guideSouth; break;
                case GuideDir.East: d = GuideDirections.guideEast; break;
                default: d = GuideDirections.guideWest; break;
            }

            var watch = Stopwatch.StartNew();
            await Task.Run(() => mediator.PulseGuide(d, milliseconds), token).ConfigureAwait(false);

            // Certains pilotes rendent la main tout de suite : on attend la fin de l'impulsion.
            int remaining = milliseconds - (int)watch.ElapsedMilliseconds;
            if (remaining > 0) await Task.Delay(remaining, token).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Monture simulée pour tester la calibration en intérieur : elle déplace le ciel synthétique.
    /// Axes inclinés de 25°, Dec perpendiculaire, jeu de 400 ms à chaque inversion de la Dec.
    /// </summary>
    public class SimMount : IGuideMount {
        private const double RaRate = 0.0023;     // pixels par milliseconde
        private const double DecRate = 0.0021;
        private const double AngleRad = 25.0 * Math.PI / 180.0;
        private const double BacklashMs = 3000;      // jeu en Dec réaliste (une monture harmonique non équilibrée en a plusieurs secondes)
        private const double SouthFactor = 0.6;      // vitesse Sud / vitesse Nord : l'engrenage pousse moins bien vers le Sud
        private const double Speedup = 4;         // les impulsions simulées durent 4 fois moins longtemps

        private readonly object sync = new object();
        private double offsetX;
        private double offsetY;
        private GuideDir? lastDec;
        private double backlashLeft = BacklashMs;

        public bool IsAvailable => true;
        public double? DeclinationDegrees => 0;
        public string PierSide => "Simulation";
        public double? HoursToMeridian => null;

        public Task SlewToCalibrationFieldAsync(Action<string> progress, CancellationToken token) {
            progress?.Invoke("simulation : pointage ignoré.");
            return Task.CompletedTask;
        }

        // Dérive simulée (guidage et assistant) : dérive lente + erreur périodique (120 s, 1 px) en RA, dérive constante en Dec.
        private bool driftOn;
        private DateTime driftStart;
        private double bakedX;
        private double bakedY;

        public double OffsetX { get { lock (sync) return offsetX + bakedX + DriftNow().Item1; } }
        public double OffsetY { get { lock (sync) return offsetY + bakedY + DriftNow().Item2; } }

        public void SetDrift(bool on) {
            lock (sync) {
                if (on == driftOn) return;
                if (on) {
                    driftStart = DateTime.UtcNow;
                } else {
                    var d = DriftNow();
                    bakedX += d.Item1;
                    bakedY += d.Item2;
                }
                driftOn = on;
            }
        }

        private (double, double) DriftNow() {
            if (!driftOn) return (0, 0);
            double t = (DateTime.UtcNow - driftStart).TotalSeconds;
            double ra = 0.05 * t + 1.0 * Math.Sin(2 * Math.PI * t / 120.0);
            double dec = 0.10 * t;
            return (ra * Math.Cos(AngleRad) + dec * Math.Cos(AngleRad + Math.PI / 2),
                    ra * Math.Sin(AngleRad) + dec * Math.Sin(AngleRad + Math.PI / 2));
        }

        public async Task PulseAsync(GuideDir direction, int milliseconds, CancellationToken token) {
            await Task.Delay((int)(milliseconds / Speedup), token).ConfigureAwait(false);

            lock (sync) {
                double effective = milliseconds;
                double dx;
                double dy;
                if (direction == GuideDir.North || direction == GuideDir.South) {
                    if (lastDec != direction) backlashLeft = BacklashMs;
                    lastDec = direction;
                    double eaten = Math.Min(backlashLeft, effective);
                    backlashLeft -= eaten;
                    effective -= eaten;
                    if (direction == GuideDir.South) effective *= SouthFactor;
                    double sign = direction == GuideDir.North ? 1 : -1;
                    dx = sign * Math.Cos(AngleRad + Math.PI / 2) * DecRate * effective;
                    dy = sign * Math.Sin(AngleRad + Math.PI / 2) * DecRate * effective;
                } else {
                    double sign = direction == GuideDir.West ? 1 : -1;
                    dx = sign * Math.Cos(AngleRad) * RaRate * effective;
                    dy = sign * Math.Sin(AngleRad) * RaRate * effective;
                }
                offsetX += dx;
                offsetY += dy;
            }
        }
    }
}
