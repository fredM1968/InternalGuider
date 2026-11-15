using NINA.Equipment.Interfaces.Mediator;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
using NINA.Profile.Interfaces;
using System.ComponentModel.Composition;

namespace NINA.Plugin.InternalGuider {
    /// <summary>Point d'entrée du plugin : NINA le découvre via MEF.</summary>
    [Export(typeof(IPluginManifest))]
    public class InternalGuiderPlugin : PluginBase {
        private readonly InterExposureService interExposure;

        [ImportingConstructor]
        public InternalGuiderPlugin(ITelescopeMediator telescopeMediator, ICameraMediator cameraMediator, IProfileService profileService,
                                    NINA.WPF.Base.Interfaces.Mediator.IImageSaveMediator imageSaveMediator) {
            // La correction entre les poses écoute les images de NINA ; elle ne fait rien tant qu'elle n'est pas activée dans les paramètres avancés.
            interExposure = new InterExposureService(telescopeMediator, cameraMediator, profileService, imageSaveMediator);
        }

        public override System.Threading.Tasks.Task Teardown() {
            interExposure?.Dispose();
            return base.Teardown();
        }
    }
}
