using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NINA.Core.Utility;

namespace NINA.Plugin.InternalGuider {
    /// <summary>Un dark maître : médiane pixel par pixel de plusieurs poses, pour une caméra, une durée de pose et un gain.</summary>
    public class DarkFrame {
        public int Width;
        public int Height;
        public double Exposure;
        /// <summary>Gain utilisé, ou -1 si la caméra n'a pas de gain réglable.</summary>
        public int Gain;
        public int Count;
        public DateTime Date;
        /// <summary>Médiane des pixels du dark : niveau rajouté après soustraction pour conserver le niveau moyen de l'image.</summary>
        public int Median;
        public ushort[] Data;
    }

    /// <summary>
    /// Bibliothèque de darks, dans %LOCALAPPDATA%\NINA\InternalGuider\Darks\&lt;caméra&gt;\dark_&lt;pose&gt;s_g&lt;gain&gt;.dk.
    /// Un dark ne sert que pour la même caméra, la même durée de pose et le même gain (et la même taille d'image).
    /// </summary>
    public static class DarkLibrary {
        private const int Magic = 0x4B444749;   // "IGDK"
        private const int FormatVersion = 1;
        private static int version;

        /// <summary>Change à chaque création ou suppression : permet d'invalider les copies en mémoire.</summary>
        public static int Version => Volatile.Read(ref version);

        private static string Root => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NINA", "InternalGuider", "Darks");

        private static string Sanitize(string text) {
            if (string.IsNullOrWhiteSpace(text)) return "camera";
            foreach (char c in Path.GetInvalidFileNameChars()) text = text.Replace(c, '_');
            return text.Trim();
        }

        private static string CameraDir(string cameraKey) => Path.Combine(Root, Sanitize(cameraKey));

        private static string FilePath(string cameraKey, double exposure, int gain) =>
            Path.Combine(CameraDir(cameraKey), string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "dark_{0:0.###}s_g{1}.dk", exposure, gain));

        public static void Save(string cameraKey, DarkFrame frame) {
            Directory.CreateDirectory(CameraDir(cameraKey));
            string path = FilePath(cameraKey, frame.Exposure, frame.Gain);
            using (var w = new BinaryWriter(File.Create(path))) {
                w.Write(Magic);
                w.Write(FormatVersion);
                w.Write(frame.Width);
                w.Write(frame.Height);
                w.Write(frame.Exposure);
                w.Write(frame.Gain);
                w.Write(frame.Count);
                w.Write(frame.Date.Ticks);
                w.Write(frame.Median);
                var bytes = new byte[frame.Data.Length * 2];
                Buffer.BlockCopy(frame.Data, 0, bytes, 0, bytes.Length);
                w.Write(bytes);
            }
            Interlocked.Increment(ref version);
        }

        private static DarkFrame Read(string path, bool withData) {
            try {
                using (var r = new BinaryReader(File.OpenRead(path))) {
                    if (r.ReadInt32() != Magic || r.ReadInt32() != FormatVersion) return null;
                    var f = new DarkFrame {
                        Width = r.ReadInt32(),
                        Height = r.ReadInt32(),
                        Exposure = r.ReadDouble(),
                        Gain = r.ReadInt32(),
                        Count = r.ReadInt32(),
                        Date = new DateTime(r.ReadInt64()),
                        Median = r.ReadInt32()
                    };
                    if (withData) {
                        var bytes = r.ReadBytes(f.Width * f.Height * 2);
                        if (bytes.Length != f.Width * f.Height * 2) return null;
                        f.Data = new ushort[f.Width * f.Height];
                        Buffer.BlockCopy(bytes, 0, f.Data, 0, bytes.Length);
                    }
                    return f;
                }
            } catch (Exception ex) {
                Logger.Warning("InternalGuider: dark illisible " + path + " : " + ex.Message);
                return null;
            }
        }

        /// <summary>Dark correspondant (même pose, même gain, même taille), ou null.</summary>
        public static DarkFrame TryLoad(string cameraKey, double exposure, int gain, int width, int height) {
            string path = FilePath(cameraKey, exposure, gain);
            if (!File.Exists(path)) return null;
            var f = Read(path, true);
            return f != null && f.Width == width && f.Height == height ? f : null;
        }

        /// <summary>Gains pour lesquels un dark existe (même pose et même taille), triés.</summary>
        public static List<int> GainsFor(string cameraKey, double exposure, int width, int height) {
            var result = new List<int>();
            try {
                string dir = CameraDir(cameraKey);
                if (!Directory.Exists(dir)) return result;
                foreach (var p in Directory.GetFiles(dir, "*.dk")) {
                    var f = Read(p, false);
                    if (f != null && Math.Abs(f.Exposure - exposure) < 1e-6 && f.Width == width && f.Height == height && f.Gain >= 0) result.Add(f.Gain);
                }
                result.Sort();
            } catch (Exception ex) {
                Logger.Warning("InternalGuider: liste des darks illisible : " + ex.Message);
            }
            return result;
        }

        /// <summary>Vrai s'il existe au moins un dark enregistré (pour une caméra quelconque).</summary>
        public static bool HasAny() {
            try {
                return Directory.Exists(Root) && Directory.GetFiles(Root, "*.dk", SearchOption.AllDirectories).Length > 0;
            } catch (Exception) {
                return false;
            }
        }

        /// <summary>Description des darks d'une caméra, un par ligne (vide s'il n'y en a pas).</summary>
        public static string Describe(string cameraKey) {
            string dir = CameraDir(cameraKey);
            if (!Directory.Exists(dir)) return "";
            var list = Directory.GetFiles(dir, "*.dk").Select(p => Read(p, false)).Where(f => f != null)
                                .OrderBy(f => f.Exposure).ThenBy(f => f.Gain).ToList();
            return string.Join(" · ", list.Select(f =>
                string.Format(System.Globalization.CultureInfo.CurrentCulture, "{0:0.###} s{1} ({2}×)",
                    f.Exposure, f.Gain >= 0 ? " gain " + f.Gain : "", f.Count)));
        }

        /// <summary>Supprime toutes les bibliothèques de darks (toutes les caméras). Renvoie le nombre de darks supprimés.</summary>
        public static int DeleteAll() {
            int n = 0;
            try {
                if (Directory.Exists(Root)) {
                    n = Directory.GetFiles(Root, "*.dk", SearchOption.AllDirectories).Length;
                    Directory.Delete(Root, true);
                }
            } catch (Exception ex) {
                Logger.Error(ex);
            }
            Interlocked.Increment(ref version);
            return n;
        }

        /// <summary>Médiane pixel par pixel d'une série de poses.</summary>
        public static DarkFrame Combine(IList<ushort[]> frames, int width, int height, double exposure, int gain) {
            int n = frames.Count;
            int len = width * height;
            var data = new ushort[len];
            Parallel.For(0, len, () => new ushort[n], (i, state, buf) => {
                for (int k = 0; k < n; k++) buf[k] = frames[k][i];
                Array.Sort(buf);
                data[i] = (n % 2 == 1) ? buf[n / 2] : (ushort)((buf[n / 2 - 1] + buf[n / 2]) / 2);
                return buf;
            }, _ => { });

            var sample = new ushort[Math.Min(len, 20001)];
            int step = Math.Max(1, len / sample.Length);
            for (int i = 0, j = 0; j < sample.Length && i < len; i += step, j++) sample[j] = data[i];
            Array.Sort(sample);

            return new DarkFrame {
                Width = width, Height = height, Exposure = exposure, Gain = gain, Count = n,
                Date = DateTime.Now, Median = sample[sample.Length / 2], Data = data
            };
        }

        /// <summary>Soustrait le dark (en place) et rajoute son niveau médian, pour que le fond de l'image garde le même niveau.</summary>
        public static void Apply(ushort[] raw, DarkFrame dark) {
            var d = dark.Data;
            int len = Math.Min(raw.Length, d.Length);
            int pedestal = dark.Median;
            for (int i = 0; i < len; i++) {
                int v = raw[i] - d[i] + pedestal;
                raw[i] = (ushort)(v < 0 ? 0 : v > 65535 ? 65535 : v);
            }
        }
    }
}
