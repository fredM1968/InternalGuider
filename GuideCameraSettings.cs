using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using NINA.Core.Utility;

namespace NINA.Plugin.InternalGuider {
    /// <summary>Gain mémorisé pour chaque caméra de guidage (fichier %LOCALAPPDATA%\NINA\InternalGuider\camera.json).</summary>
    public static class GuideCameraSettings {
        private class Data {
            public Dictionary<string, int> Gains { get; set; } = new Dictionary<string, int>();
            public Dictionary<string, double> Exposures { get; set; } = new Dictionary<string, double>();
        }

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NINA", "InternalGuider", "camera.json");

        private static Data Load() {
            try {
                if (File.Exists(FilePath)) {
                    var data = JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath));
                    if (data?.Gains != null) { if (data.Exposures == null) data.Exposures = new Dictionary<string, double>(); return data; }
                }
            } catch (Exception ex) {
                Logger.Error(ex);
            }
            return new Data();
        }

        /// <summary>Gain mémorisé pour cette caméra, ou null s'il n'y en a pas.</summary>
        public static int? GetGain(string cameraId) {
            if (string.IsNullOrEmpty(cameraId)) return null;
            return Load().Gains.TryGetValue(cameraId, out var gain) ? gain : (int?)null;
        }

        /// <summary>Durée de pose mémorisée pour cette caméra, ou null s'il n'y en a pas.</summary>
        public static double? GetExposure(string cameraId) {
            if (string.IsNullOrEmpty(cameraId)) return null;
            return Load().Exposures.TryGetValue(cameraId, out var e) && e > 0 ? e : (double?)null;
        }

        public static void SetExposure(string cameraId, double seconds) {
            if (string.IsNullOrEmpty(cameraId) || seconds <= 0) return;
            try {
                var data = Load();
                data.Exposures[cameraId] = seconds;
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
            } catch (Exception ex) {
                Logger.Error(ex);
            }
        }

        public static void SetGain(string cameraId, int gain) {
            if (string.IsNullOrEmpty(cameraId)) return;
            try {
                var data = Load();
                data.Gains[cameraId] = gain;
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
            } catch (Exception ex) {
                Logger.Error(ex);
            }
        }

        /// <summary>Gains et poses mémorisés, en JSON (pour la sauvegarde de la configuration).</summary>
        public static string ToJson() => JsonSerializer.Serialize(Load());

        /// <summary>Fusionne des gains et poses lus dans une sauvegarde de configuration.</summary>
        public static void FromJson(string json) {
            try {
                var incoming = JsonSerializer.Deserialize<Data>(json);
                if (incoming == null) return;
                var data = Load();
                if (incoming.Gains != null) foreach (var kv in incoming.Gains) data.Gains[kv.Key] = kv.Value;
                if (incoming.Exposures != null) foreach (var kv in incoming.Exposures) if (kv.Value > 0) data.Exposures[kv.Key] = kv.Value;
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
            } catch (Exception ex) {
                Logger.Error(ex);
            }
        }
    }
}
