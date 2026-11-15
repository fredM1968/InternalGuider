using System;

namespace NINA.Plugin.InternalGuider {

    /// <summary>
    /// Dernière image de guidage (après soustraction du dark) et échelle, mises à disposition des autres plugins
    /// (Planitario, outil « Champ du guidage »). Accès par réflexion : aucun plugin ne dépend de l'autre pour compiler.
    /// </summary>
    public static class GuideFrameExport {

        private static readonly object gate = new object();
        private static ushort[] raw;
        private static int width, height, bitDepth;
        private static double exposure, pixelScale;
        private static DateTime utc;

        /// <summary>Appelé par l'aperçu à chaque image réelle (pas en simulation).</summary>
        internal static void Publish(ushort[] data, int w, int h, int depth, double exposureSeconds, double scale) {
            if (data == null || w <= 0 || h <= 0) return;
            lock (gate) {
                raw = data;
                width = w;
                height = h;
                bitDepth = depth > 0 ? depth : 16;
                exposure = exposureSeconds;
                pixelScale = scale;
                utc = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// Dernière image : { ushort[] pixels, int largeur, int hauteur, int profondeur (bits), double échelle (″/px),
        /// double pose (s), DateTime date UTC }. Exception avec un message clair s'il n'y en a pas d'utilisable.
        /// </summary>
        public static object[] GetLastFrame(double maxAgeSeconds) {
            lock (gate) {
                if (raw == null) throw new InvalidOperationException("Internal Guider n'a pas encore pris d'image : lance la boucle de poses ou le guidage");
                if ((DateTime.UtcNow - utc).TotalSeconds > maxAgeSeconds)
                    throw new InvalidOperationException($"la dernière image d'Internal Guider date de {(DateTime.UtcNow - utc).TotalSeconds:0} s : lance la boucle de poses ou le guidage");
                if (pixelScale <= 0) throw new InvalidOperationException("échelle de la caméra de guidage inconnue : renseigne la focale dans la configuration d'Internal Guider");
                return new object[] { (ushort[])raw.Clone(), width, height, bitDepth, pixelScale, exposure, utc };
            }
        }
    }
}
