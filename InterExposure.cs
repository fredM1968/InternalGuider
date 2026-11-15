using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;

namespace NINA.Plugin.InternalGuider {
    /// <summary>Indique si le guidage interne tourne : la correction entre les poses se met alors en retrait.</summary>
    public static class GuideActivity {
        public static volatile bool Guiding;
    }

    /// <summary>Décalage d'un champ d'étoiles par rapport à un autre (translation seule).</summary>
    public class FieldShift {
        public double Dx, Dy;
        public int Matched;
        public double Rms;

        /// <summary>Apparie deux listes d'étoiles (x, y) par vote de translation. Renvoie null si le champ n'est pas reconnu.</summary>
        public static FieldShift Match(IList<StarInfo> reference, IList<StarInfo> current, double maxShiftPx, int minMatched) {
            if (reference == null || current == null || reference.Count < minMatched || current.Count < minMatched) return null;
            const double bin = 2.0;
            var votes = new Dictionary<long, int>();
            foreach (var r in reference) {
                foreach (var c in current) {
                    double dx = c.X - r.X, dy = c.Y - r.Y;
                    if (Math.Abs(dx) > maxShiftPx || Math.Abs(dy) > maxShiftPx) continue;
                    long kx = (long)Math.Floor(dx / bin), ky = (long)Math.Floor(dy / bin);
                    long key = (kx + 100000) * 1000003L + (ky + 100000);
                    votes.TryGetValue(key, out int n);
                    votes[key] = n + 1;
                }
            }
            if (votes.Count == 0) return null;
            // Meilleur sommet, cases voisines comprises
            long bestKey = 0; int bestScore = 0;
            foreach (var kv in votes) {
                long kx = kv.Key / 1000003L - 100000, ky = kv.Key % 1000003L - 100000;
                int score = 0;
                for (int ix = -1; ix <= 1; ix++) for (int iy = -1; iy <= 1; iy++) {
                    long k2 = (kx + ix + 100000) * 1000003L + (ky + iy + 100000);
                    if (votes.TryGetValue(k2, out int v)) score += v;
                }
                if (score > bestScore) { bestScore = score; bestKey = kv.Key; }
            }
            if (bestScore < minMatched) return null;
            double gx = (bestKey / 1000003L - 100000 + 0.5) * bin, gy = (bestKey % 1000003L - 100000 + 0.5) * bin;

            // Affinage : paires à moins de 3 px après translation, puis médiane
            for (int iter = 0; iter < 3; iter++) {
                var dxs = new List<double>(); var dys = new List<double>();
                var used = new HashSet<StarInfo>();
                foreach (var r in reference) {
                    StarInfo best = null; double bd = 3.0;
                    foreach (var c in current) {
                        if (used.Contains(c)) continue;
                        double d = Math.Sqrt((c.X - r.X - gx) * (c.X - r.X - gx) + (c.Y - r.Y - gy) * (c.Y - r.Y - gy));
                        if (d < bd) { bd = d; best = c; }
                    }
                    if (best != null) { used.Add(best); dxs.Add(best.X - r.X); dys.Add(best.Y - r.Y); }
                }
                if (dxs.Count < minMatched) return null;
                gx = Median(dxs); gy = Median(dys);
                if (iter == 2) {
                    double s = 0;
                    for (int i = 0; i < dxs.Count; i++) s += (dxs[i] - gx) * (dxs[i] - gx) + (dys[i] - gy) * (dys[i] - gy);
                    return new FieldShift { Dx = gx, Dy = gy, Matched = dxs.Count, Rms = Math.Sqrt(s / dxs.Count) };
                }
            }
            return null;
        }

        private static double Median(List<double> v) {
            var s = v.OrderBy(x => x).ToList();
            int n = s.Count;
            return n % 2 == 1 ? s[n / 2] : 0.5 * (s[n / 2 - 1] + s[n / 2]);
        }
    }

    /// <summary>
    /// Correction entre les poses. Après chaque pose de lumière, le champ d'étoiles de l'image est comparé à celui de la première pose
    /// (la référence). Le plugin en déduit le décalage et la vitesse de dérive, puis, juste après la fin d'une pose (pendant le
    /// téléchargement, pas pendant la pose), envoie des impulsions pour recentrer le champ au milieu de la pose suivante.
    /// Il ne corrige pas ce qui se passe PENDANT une pose (traînées) : il recale d'une pose à l'autre et mesure la dérive.
    /// L'API de NINA pour les images est appelée par réflexion : si un nom change, le journal le dit et la fonction se désactive.
    /// </summary>
    public class InterExposureService {
        private const double GainFactor = 0.8;
        private const int MinMatched = 8;
        private const double MaxShiftPx = 400;
        private const int HistoryPoints = 5;

        private readonly ITelescopeMediator telescope;
        private readonly ICameraMediator camera;
        private readonly IProfileService profile;
        private readonly NinaMount mount;
        private readonly SemaphoreSlim busy = new SemaphoreSlim(1, 1);
        private readonly object sync = new object();
        private readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        private StreamWriter logWriter;
        private bool hooked;
        private volatile bool stop;

        // Référence et historique (secondes de l'horloge locale)
        private List<StarInfo> refStars;
        private double refScaleArcsec;
        private readonly List<double> hT = new List<double>(), hRa = new List<double>(), hDec = new List<double>();
        private double cRa, cDec;                 // corrections cumulées envoyées (arcsec ; RA : impulsion Est positive, Dec : Sud positive)
        private readonly List<(double t, double ra, double dec)> cHist = new List<(double, double, double)>();
        private double lastFrameEndT = -1e9, lastExposureSec = 60, lastEdgeT = -1e9;
        private int lastDecDir;
        private int frames;

        public InterExposureService(ITelescopeMediator telescope, ICameraMediator camera, IProfileService profile, object imageSaveMediator) {
            this.telescope = telescope;
            this.camera = camera;
            this.profile = profile;
            mount = telescope != null ? new NinaMount(telescope) : null;
            try {
                if (imageSaveMediator != null) hooked = Hook(imageSaveMediator);
                if (hooked) Task.Run(PollLoop);
            } catch (Exception ex) {
                Logger.Error(ex);
                Log("erreur à l'initialisation : " + ex.Message);
            }
        }

        public void Dispose() { stop = true; }

        // --------------------------------------------------------------------------------------------------------------------
        // Écoute des images (par réflexion)

        private bool Hook(object mediator) {
            foreach (var name in new[] { "BeforeFinalizeImageSaved", "BeforeImageSaved" }) {
                var ev = mediator.GetType().GetEvent(name);
                if (ev == null) continue;
                try {
                    var handlerType = ev.EventHandlerType;
                    var invoke = handlerType.GetMethod("Invoke");
                    var ps = invoke.GetParameters().Select(p => Expression.Parameter(p.ParameterType, p.Name)).ToArray();
                    var method = typeof(InterExposureService).GetMethod(nameof(OnImage), BindingFlags.NonPublic | BindingFlags.Instance);
                    Expression call = Expression.Call(Expression.Constant(this), method,
                        Expression.Convert(ps[0], typeof(object)), Expression.Convert(ps[1], typeof(object)));
                    Expression body;
                    if (invoke.ReturnType == typeof(void)) body = Expression.Block(typeof(void), call, Expression.Empty());
                    else if (invoke.ReturnType == typeof(Task)) body = call;
                    else body = Expression.Block(invoke.ReturnType, call, Expression.Default(invoke.ReturnType));
                    var del = Expression.Lambda(handlerType, body, ps).Compile();
                    ev.AddEventHandler(mediator, del);
                    Log($"écoute des images de NINA via « {name} »");
                    return true;
                } catch (Exception ex) {
                    Log($"impossible d'écouter « {name} » : {ex.Message}");
                }
            }
            Log("aucun évènement d'image trouvé dans NINA : la correction entre les poses est indisponible");
            return false;
        }

        internal static object Prop(object o, string path) {
            foreach (var part in path.Split('.')) {
                if (o == null) return null;
                var p = o.GetType().GetProperty(part, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (p == null) return null;
                o = p.GetValue(o);
            }
            return o;
        }

        private static double Num(object o, double fallback = 0) {
            try { return o == null ? fallback : Convert.ToDouble(o, CultureInfo.InvariantCulture); } catch (Exception) { return fallback; }
        }

        /// <summary>Image prise par NINA, réduite (pixels, largeur, hauteur, profondeur, facteur de réduction, pose en s) : sert à l'aperçu en caméra unique.</summary>
        public static event Action<ushort[], int, int, int, int, double> NinaImage;

        private Task OnImage(object sender, object args) {
            try {
                bool wantInter = GuiderSettings.InterExpEnabled && !GuideActivity.Guiding;
                var preview = NinaImage;
                if (!wantInter && preview == null) return Task.CompletedTask;
                LogOnce("recu", "première image reçue de NINA (évènement d'enregistrement)");
                var image = Prop(args, "Image");
                if (image == null) { LogOnce("image", "l'évènement d'image ne contient pas « Image »"); return Task.CompletedTask; }
                // Caméra couleur : NINA fournit une image débayérisée qui contient l'image brute (mosaïque) dans RawImageData
                var rawImage = Prop(image, "RawImageData");
                if (rawImage != null) image = rawImage;
                string type = Prop(image, "MetaData.Image.ImageType")?.ToString() ?? "";
                if (wantInter && type.Length > 0 && type.IndexOf("LIGHT", StringComparison.OrdinalIgnoreCase) < 0 && type.IndexOf("SNAPSHOT", StringComparison.OrdinalIgnoreCase) < 0) wantInter = false;
                if (!wantInter && preview == null) return Task.CompletedTask;
                var flat = Prop(image, "Data.FlatArray") as ushort[];
                int w = (int)Num(Prop(image, "Properties.Width")), h = (int)Num(Prop(image, "Properties.Height"));
                int depth = (int)Num(Prop(image, "Properties.BitDepth"), 16);
                if (flat == null || w < 200 || h < 200 || flat.Length < w * h) { LogOnce("data", $"pixels de l'image introuvables : image={image.GetType().FullName}, Data={Prop(image, "Data")?.GetType().FullName ?? "null"}, FlatArray={(flat == null ? "null" : flat.Length.ToString())}, Properties={Prop(image, "Properties")?.GetType().FullName ?? "null"}, largeur={w}, hauteur={h}; propriétés de l'image : {string.Join(", ", image.GetType().GetProperties().Select(q => q.Name))}"); return Task.CompletedTask; }
                double exposure = Num(Prop(image, "MetaData.Image.ExposureTime"), lastExposureSec);
                double binX = Math.Max(1, Num(Prop(image, "MetaData.Camera.BinX"), 1));
                double pixelUm = Num(Prop(image, "MetaData.Camera.PixelSize"), 0);
                double focal = Num(Prop(image, "MetaData.Telescope.FocalLength"), 0);
                if (pixelUm <= 0) pixelUm = Num(Prop(profile?.ActiveProfile, "CameraSettings.PixelSize"));
                if (focal <= 0) focal = Num(Prop(profile?.ActiveProfile, "TelescopeSettings.FocalLength"));
                double scale = GuiderSettings.InterExpScaleArcsec > 0 ? GuiderSettings.InterExpScaleArcsec
                             : (pixelUm > 0 && focal > 0 ? pixelUm * binX / focal * 206.265 : 0);

                // Réduction par moyenne de blocs pour garder une analyse rapide
                int f = Math.Max(1, (int)Math.Ceiling(w / 2200.0));
                int bw = w / f, bh = h / f;
                var small = new ushort[bw * bh];
                if (f == 1) Array.Copy(flat, small, small.Length);
                else {
                    int area = f * f;
                    for (int y = 0; y < bh; y++) for (int x = 0; x < bw; x++) {
                        int sum = 0;
                        for (int j = 0; j < f; j++) { int row = (y * f + j) * w + x * f; for (int i = 0; i < f; i++) sum += flat[row + i]; }
                        small[y * bw + x] = (ushort)(sum / area);
                    }
                }
                double endT = clock.Elapsed.TotalSeconds;
                double edge = lastEdgeT;
                if (endT - edge < 180 && edge > 0) endT = edge;      // fin de pose connue par la surveillance de la caméra
                try { preview?.Invoke(small, bw, bh, depth, f, exposure); } catch (Exception) { }
                if (wantInter) Task.Run(() => Process(small, bw, bh, depth, f, exposure, scale, endT));
            } catch (Exception ex) {
                Log("erreur sur une image : " + ex.Message);
            }
            return Task.CompletedTask;
        }

        private readonly HashSet<string> logged = new HashSet<string>();
        private void LogOnce(string key, string text) { lock (sync) { if (!logged.Add(key)) return; } Log(text); }

        // --------------------------------------------------------------------------------------------------------------------
        // Mesure

        private async Task Process(ushort[] small, int bw, int bh, int depth, int factor, double exposure, double scaleMain, double endT) {
            if (!await busy.WaitAsync(0).ConfigureAwait(false)) { Log("mesure précédente encore en cours : image ignorée"); return; }
            try {
                var det = StarDetector.Analyze(small, bw, bh, depth);
                var stars = det.Stars.Where(s => !s.Saturated).OrderByDescending(s => s.Flux).Take(60)
                    .Select(s => new StarInfo { X = s.X * factor, Y = s.Y * factor, Flux = s.Flux, Hfd = s.Hfd * factor, Snr = s.Snr }).ToList();
                double mid = endT - exposure / 2.0;
                lastExposureSec = exposure;
                lastFrameEndT = endT;
                lock (sync) {
                    if (refStars == null || scaleMain <= 0 && refScaleArcsec <= 0) {
                        SetReference(stars, scaleMain, "première pose", mid);
                        return;
                    }
                }
                var shift = FieldShift.Match(refStars, stars, MaxShiftPx, MinMatched);
                if (shift == null) {
                    Log($"champ non reconnu ({stars.Count} étoiles) : nouvelle référence");
                    lock (sync) SetReference(stars, scaleMain, "champ non reconnu", mid);
                    return;
                }
                var cal = CalibrationFor();
                if (cal == null) { LogOnce("cal", "pas de calibration utilisable : lancez une calibration du guidage (même caméra et même télescope)"); return; }
                if (cal.PixelScale <= 0) { LogOnce("scale", "la calibration n'a pas d'échelle (″/px)"); return; }
                if (scaleMain <= 0) { LogOnce("scalemain", "échelle de l'image inconnue (taille de pixel, focale) : réglez-la dans Paramètres avancés"); return; }

                // Décalage → axes de la monture (px de guidage), puis arcsec
                double rot = GuiderSettings.InterExpRotationDeg * Math.PI / 180.0;
                double k = scaleMain / cal.PixelScale;
                double gx = (shift.Dx * Math.Cos(rot) - shift.Dy * Math.Sin(rot)) * k;
                double gy = (shift.Dx * Math.Sin(rot) + shift.Dy * Math.Cos(rot)) * k;
                double ux1 = Math.Cos(cal.RaAngleDeg * Math.PI / 180), uy1 = Math.Sin(cal.RaAngleDeg * Math.PI / 180);
                double ux2 = Math.Cos(cal.DecAngleDeg * Math.PI / 180), uy2 = Math.Sin(cal.DecAngleDeg * Math.PI / 180);
                double det2 = ux1 * uy2 - uy1 * ux2;
                if (Math.Abs(det2) < 0.2) { LogOnce("det", "axes de la calibration trop peu perpendiculaires"); return; }
                double a = (gx * uy2 - gy * ux2) / det2 * cal.PixelScale;      // arcsec, positif = sens d'une impulsion Ouest
                double b = (ux1 * gy - uy1 * gx) / det2 * cal.PixelScale;      // arcsec, positif = Nord

                lock (sync) {
                    // Rupture (dither, retournement, recentrage) : le résidu par rapport à la prévision est trop grand
                    double c1Ra = CumAt(mid).ra, c1Dec = CumAt(mid).dec;
                    double uRa = a + c1Ra, uDec = b + c1Dec;
                    double predRa = Predict(hT, hRa, mid, uRa), predDec = Predict(hT, hDec, mid, uDec);
                    double jump = Math.Sqrt((uRa - predRa) * (uRa - predRa) + (uDec - predDec) * (uDec - predDec));
                    if (hT.Count >= 1 && jump > GuiderSettings.InterExpJumpArcsec) {
                        Log($"saut de {jump:0.0}″ par rapport à la prévision (dither, retournement ou recentrage) : nouvelle référence");
                        SetReference(stars, scaleMain, "saut", mid);
                        return;
                    }
                    hT.Add(mid); hRa.Add(uRa); hDec.Add(uDec);
                    while (hT.Count > HistoryPoints) { hT.RemoveAt(0); hRa.RemoveAt(0); hDec.RemoveAt(0); }
                    frames++;
                    double vRa = Slope(hT, hRa) * 60, vDec = Slope(hT, hDec) * 60;
                    Log($"pose {frames} : décalage RA {a:+0.0;-0.0}″ Dec {b:+0.0;-0.0}″ ({shift.Matched} étoiles, rms {shift.Rms:0.00} px) · position sans correction RA {uRa:+0.0;-0.0}″ Dec {uDec:+0.0;-0.0}″ · dérive RA {vRa:+0.00;-0.00}″/min Dec {vDec:+0.00;-0.00}″/min");
                }
            } catch (Exception ex) {
                Log("erreur de mesure : " + ex.Message);
            } finally {
                busy.Release();
            }
        }

        private void SetReference(List<StarInfo> stars, double scale, string why, double mid) {
            refStars = stars; refScaleArcsec = scale;
            hT.Clear(); hRa.Clear(); hDec.Clear();
            cRa = cDec = 0; cHist.Clear();
            hT.Add(mid); hRa.Add(0); hDec.Add(0);
            Log($"référence : {stars.Count} étoiles ({why}), échelle {scale:0.00}″/px");
            if (stars.Count < MinMatched) Log("attention : trop peu d'étoiles pour recaler (il en faut au moins " + MinMatched + ")");
        }

        private (double ra, double dec) CumAt(double t) {
            double ra = 0, dec = 0;
            foreach (var h in cHist) { if (h.t <= t) { ra = h.ra; dec = h.dec; } }
            return (ra, dec);
        }

        private static double Slope(List<double> t, List<double> y) {
            int n = t.Count;
            if (n < 3) return 0;
            double mt = t.Average(), my = y.Average(), sxx = 0, sxy = 0;
            for (int i = 0; i < n; i++) { sxx += (t[i] - mt) * (t[i] - mt); sxy += (t[i] - mt) * (y[i] - my); }
            return sxx > 0 ? sxy / sxx : 0;
        }

        private static double Predict(List<double> t, List<double> y, double at, double fallback) {
            int n = t.Count;
            if (n == 0) return fallback;
            if (n < 3) return y[n - 1];
            double mt = t.Average(), my = y.Average();
            return my + Slope(t, y) * (at - mt);
        }

        private CalibrationData CalibrationFor() {
            try { return CalibrationData.LoadFor(mount?.PierSide); } catch (Exception) { return null; }
        }

        // --------------------------------------------------------------------------------------------------------------------
        // Surveillance de la caméra et impulsions

        private bool? Exposing() {
            try {
                var info = camera?.GetInfo();
                if (info == null) return null;
                var v = Prop(info, "IsExposing");
                return v is bool bb ? bb : (bool?)null;
            } catch (Exception) { return null; }
        }

        private async Task PollLoop() {
            bool? prev = null;
            while (!stop) {
                try {
                    await Task.Delay(150).ConfigureAwait(false);
                    var now = Exposing();
                    if (now == null) continue;
                    if (prev == true && now == false) {
                        lastEdgeT = clock.Elapsed.TotalSeconds;
                        if (GuiderSettings.InterExpEnabled && !GuideActivity.Guiding) await CorrectAsync().ConfigureAwait(false);
                    }
                    prev = now;
                } catch (Exception ex) {
                    Log("erreur de surveillance : " + ex.Message);
                    await Task.Delay(2000).ConfigureAwait(false);
                }
            }
        }

        private async Task CorrectAsync() {
            double nowT = clock.Elapsed.TotalSeconds;
            double wantRa, wantDec, planRa, planDec;
            lock (sync) {
                if (refStars == null || hT.Count == 0) return;
                if (nowT - lastFrameEndT > Math.Max(600, 4 * lastExposureSec)) return;      // plus de pose de lumière récente
                // Position visée : le milieu de la pose suivante
                double tNext = nowT + lastExposureSec / 2.0 + 5;
                double uRa = Predict(hT, hRa, tNext, hRa[hRa.Count - 1]), uDec = Predict(hT, hDec, tNext, hDec[hDec.Count - 1]);
                wantRa = GainFactor * (uRa - cRa);
                wantDec = GainFactor * (uDec - cDec);
                planRa = wantRa; planDec = wantDec;
            }
            var cal = CalibrationFor();
            if (cal == null || cal.PixelScale <= 0 || mount == null || !mount.IsAvailable) return;
            double raRate = cal.RaRatePxPerMs * cal.PixelScale;                // arcsec par ms, impulsion Ouest
            double decRateN = cal.DecRatePxPerMs * cal.PixelScale;
            double decRateS = cal.DecReturnRatePxPerMs > 0 ? Math.Max(0.25 * decRateN, Math.Min(decRateN, cal.DecReturnRatePxPerMs * cal.PixelScale)) : decRateN;
            if (raRate <= 0 || decRateN <= 0) return;

            double min = 1.0;                                                // zone morte (arcsec)
            double raMsF = Math.Abs(planRa) < min ? 0 : planRa / raRate;      // positif = Est
            double decMsF = 0;
            if (Math.Abs(planDec) >= min) {
                decMsF = planDec > 0 ? planDec / decRateS : planDec / decRateN;   // positif = Sud
                int mode = GuiderSettings.DecMode;
                if (mode == 1 && decMsF > 0) decMsF = 0;                      // Nord seul
                if (mode == 2 && decMsF < 0) decMsF = 0;                      // Sud seul
            }
            double total = Math.Abs(raMsF) + Math.Abs(decMsF);
            double budget = GuiderSettings.InterExpBudgetMs;
            if (total > budget) { raMsF *= budget / total; decMsF *= budget / total; }
            int raMs = (int)Math.Round(raMsF), decMs = (int)Math.Round(decMsF);
            if (Math.Abs(raMs) < 20) raMs = 0;
            if (Math.Abs(decMs) < 20) decMs = 0;
            if (raMs == 0 && decMs == 0) return;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            try {
                if (raMs != 0) await mount.PulseAsync(raMs > 0 ? GuideDir.East : GuideDir.West, Math.Abs(raMs), CancellationToken.None).ConfigureAwait(false);
                if (decMs != 0) {
                    int dir = decMs > 0 ? 1 : -1;
                    int ms = Math.Abs(decMs);
                    int comp = GuiderSettings.DecBacklashMs;
                    if (comp > 0 && lastDecDir != 0 && dir != lastDecDir) ms += comp;
                    lastDecDir = dir;
                    await mount.PulseAsync(dir > 0 ? GuideDir.South : GuideDir.North, ms, CancellationToken.None).ConfigureAwait(false);
                }
            } catch (Exception ex) {
                Log("erreur d'impulsion : " + ex.Message);
                return;
            }
            lock (sync) {
                cRa += raMs * raRate;                                          // même convention que le décalage : Est positif réduit le décalage a
                cDec += decMs > 0 ? decMs * decRateS : decMs * decRateN;
                cHist.Add((clock.Elapsed.TotalSeconds, cRa, cDec));
                while (cHist.Count > 40) cHist.RemoveAt(0);
            }
            bool exposingNow = Exposing() == true;
            Log($"correction : RA {raMs:+0;-0;0} ms, Dec {decMs:+0;-0;0} ms (Est et Sud positifs) en {sw.ElapsedMilliseconds} ms" +
                (exposingNow ? " · ATTENTION : la pose suivante avait déjà commencé (allongez le délai entre les poses ou réduisez la durée maximale de correction)" : ""));
        }

        // --------------------------------------------------------------------------------------------------------------------
        // Journal

        private void Log(string text) {
            try {
                Logger.Info("InternalGuider (entre les poses) : " + text);
                lock (sync) {
                    if (logWriter == null) {
                        System.IO.Directory.CreateDirectory(GuideLog.Directory);
                        string path = System.IO.Path.Combine(GuideLog.Directory, $"interexposure_{DateTime.Now:yyyyMMdd_HHmmss}.log");
                        logWriter = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(true)) { AutoFlush = true };
                    }
                    logWriter.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  {text}");
                }
            } catch (Exception) { }
        }
    }
}
