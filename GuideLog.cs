using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using NINA.Core.Utility;

namespace NINA.Plugin.InternalGuider {
    /// <summary>
    /// Journal d'une session de guidage, en CSV : %LOCALAPPDATA%\NINA\InternalGuider\Logs\guidelog_AAAAMMJJ_HHMMSS.csv.
    /// Les lignes commençant par # donnent le contexte (matériel, calibration, réglages). Ensuite, une ligne par image :
    /// type G = mesure de guidage, type E = événement (texte libre, dernière colonne).
    /// Chaque ligne est écrite et vidée immédiatement : le journal reste lisible même si NINA plante.
    /// </summary>
    public sealed class GuideLog : IDisposable {
        private const int MaxFiles = 100;
        private const string Columns = "heure,t_s,type,err_ra_px,err_dec_px,impulsion_ra_ms,impulsion_dec_ms,etoile_x,etoile_y,snr,hfd_px,flux,verrou_x,verrou_y,n_etoiles,texte";

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private readonly StreamWriter writer;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly object sync = new object();
        private bool disposed;

        public string Path { get; }

        public static string Directory => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NINA", "InternalGuider", "Logs");

        private GuideLog(string path, string header) {
            Path = path;
            writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(true)) {
                AutoFlush = true
            };
            foreach (var line in header.Split('\n')) writer.WriteLine("# " + line.TrimEnd('\r'));
            writer.WriteLine(Columns);
        }

        /// <summary>Crée le journal d'une session, ou renvoie null s'il est désactivé ou en cas d'erreur (le guidage n'en dépend jamais).</summary>
        public static GuideLog TryCreate(string header) {
            if (!GuiderSettings.GuideLogEnabled) return null;
            try {
                System.IO.Directory.CreateDirectory(Directory);
                Prune();
                string name = $"guidelog_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
                return new GuideLog(System.IO.Path.Combine(Directory, name), header);
            } catch (Exception ex) {
                Logger.Warning("InternalGuider: journal de guidage indisponible : " + ex.Message);
                return null;
            }
        }

        private static void Prune() {
            var old = new DirectoryInfo(Directory).GetFiles("guidelog_*.csv").OrderByDescending(f => f.CreationTime).Skip(MaxFiles - 1);
            foreach (var f in old) {
                try { f.Delete(); } catch (Exception) { }
            }
        }

        private static string F(double v) => v.ToString("0.###", Inv);

        private void Write(string line) {
            lock (sync) {
                if (disposed) return;
                try { writer.WriteLine(line); } catch (Exception) { /* disque plein, fichier verrouillé… : on ignore */ }
            }
        }

        private string Prefix(string type) =>
            $"{DateTime.Now:HH:mm:ss.fff},{clock.Elapsed.TotalSeconds.ToString("0.000", Inv)},{type}";

        public void Sample(GuideSample s) {
            Write(Prefix("G") + $",{F(s.ErrRaPx)},{F(s.ErrDecPx)},{s.PulseRaMs},{s.PulseDecMs}," +
                  $"{F(s.StarX)},{F(s.StarY)},{F(s.Snr)},{F(s.Hfd)},{F(s.Flux)},{F(s.LockX)},{F(s.LockY)},{s.StarsUsed},");
        }

        public void Event(string text) {
            Write(Prefix("E") + ",,,,,,,,,,,,," + text.Replace(',', ';').Replace('\r', ' ').Replace('\n', ' '));
        }

        public void Dispose() {
            lock (sync) {
                if (disposed) return;
                disposed = true;
                try { writer.Dispose(); } catch (Exception) { }
            }
        }

        /// <summary>Ajoute une ligne datée au journal des calibrations (calibrations.log, dans le même dossier).</summary>
        public static void AppendCalibration(string text) {
            if (!GuiderSettings.GuideLogEnabled) return;
            try {
                System.IO.Directory.CreateDirectory(Directory);
                string path = System.IO.Path.Combine(Directory, "calibrations.log");
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {text.Replace('\n', ' ')}{Environment.NewLine}", new UTF8Encoding(true));
            } catch (Exception ex) {
                Logger.Warning("InternalGuider: journal des calibrations indisponible : " + ex.Message);
            }
        }
    }
}
