using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;

namespace NINA.Plugin.InternalGuider {
    /// <summary>
    /// Diagnostic « caméra unique » : écrit dans un journal les poses du plugin (début, fin, erreurs) et les poses de NINA sur la caméra principale
    /// (début et fin, vus par l'état de la caméra), pour voir comment le pilote réagit quand deux demandes de pose se croisent.
    /// Inactif tant que l'option n'est pas cochée dans les paramètres avancés.
    /// </summary>
    public static class CameraDiagnostic {
        private static readonly object sync = new object();
        private static StreamWriter writer;
        private static int started;
        private static readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();

        public static bool Enabled => GuiderSettings.CameraDiag;

        public static void Log(string text) {
            if (!Enabled) return;
            try {
                lock (sync) {
                    if (writer == null) {
                        Directory.CreateDirectory(GuideLog.Directory);
                        string path = Path.Combine(GuideLog.Directory, $"cameradiag_{DateTime.Now:yyyyMMdd_HHmmss}.log");
                        writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(true)) { AutoFlush = true };
                        writer.WriteLine("# Diagnostic caméra unique : lignes « plugin » = poses de l'Internal Guider ; lignes « NINA » = état de la caméra principale de NINA.");
                    }
                    writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  {text}");
                }
            } catch (Exception) { }
        }

        /// <summary>Surveille l'état de pose de la caméra principale de NINA (une seule surveillance par session).</summary>
        public static void StartMonitor(ICameraMediator camera) {
            if (camera == null || Interlocked.Exchange(ref started, 1) == 1) return;
            Task.Run(async () => {
                bool? prevExposing = null, prevConnected = null;
                while (true) {
                    try {
                        await Task.Delay(100).ConfigureAwait(false);
                        if (!Enabled) { prevExposing = null; prevConnected = null; continue; }
                        var info = camera.GetInfo();
                        bool? exposing = InterExposureService.Prop(info, "IsExposing") as bool?;
                        bool? connected = InterExposureService.Prop(info, "Connected") as bool?;
                        if (connected != prevConnected) { Log($"NINA : caméra principale {(connected == true ? "connectée" : "non connectée")}"); prevConnected = connected; }
                        if (exposing != null && exposing != prevExposing) {
                            Log(exposing == true ? "NINA : pose en cours" : "NINA : pose terminée");
                            prevExposing = exposing;
                        }
                    } catch (Exception) {
                        await Task.Delay(2000).ConfigureAwait(false);
                    }
                }
            });
        }
    }
}
