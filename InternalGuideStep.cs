using System;
using NINA.Core.Interfaces;

namespace NINA.Plugin.InternalGuider {
    /// <summary>
    /// Une correction de guidage, au format attendu par NINA (graphe de l'onglet Guidage, historique).
    /// Distances en pixels de la caméra de guidage ; durées en millisecondes (positives = impulsion Est / Sud).
    /// </summary>
    public class InternalGuideStep : IGuideStep {
        public InternalGuideStep(double frame, double time, double raDistance, double decDistance, double raDuration, double decDuration) {
            Frame = frame;
            Time = time;
            RADistanceRaw = raDistance;
            DECDistanceRaw = decDistance;
            RADuration = raDuration;
            DECDuration = decDuration;
        }

        // Membres de IGuideEvent (format des événements PHD2 ; sans objet ici)
        public string Event => "GuideStep";
        public string TimeStamp => DateTime.Now.ToString("o");
        public string Host => "InternalGuider";
        public int Inst => 1;

        public double Frame { get; }
        public double Time { get; }
        public double RADistanceRaw { get; set; }
        public double DECDistanceRaw { get; set; }
        public double RADuration { get; }
        public double DECDuration { get; }

        public IGuideStep Clone() => new InternalGuideStep(Frame, Time, RADistanceRaw, DECDistanceRaw, RADuration, DECDuration);
    }
}
