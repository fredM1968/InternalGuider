using System.Collections.Generic;
using System.ComponentModel.Composition;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Equipment.SDK.CameraSDKs.SBIGSDK;
using NINA.Image.Interfaces;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.ViewModel.Equipment.Camera;

namespace NINA.Plugin.InternalGuider {
    /// <summary>
    /// Ajoute l'entrée "Internal Guider" dans la liste des guideurs.
    /// Seuls les 4 services réellement disponibles pour les plugins sont importés.
    /// ISbigSdk et IEquipmentProviders&lt;ICamera&gt; ne sont pas exportés par NINA :
    /// SBIG est simplement ignoré (CameraChooserVM l'entoure d'un try/catch) et on
    /// fournit une liste de fournisseurs vide.
    /// </summary>
    [Export(typeof(IEquipmentProvider))]
    public class InternalGuiderProvider : IEquipmentProvider<IGuider> {
        private readonly InternalGuider guider;

        [ImportingConstructor]
        public InternalGuiderProvider(
            [Import(AllowDefault = true)] IProfileService profileService,
            [Import(AllowDefault = true)] ITelescopeMediator telescopeMediator,
            [Import(AllowDefault = true)] IExposureDataFactory exposureDataFactory,
            [Import(AllowDefault = true)] IImageDataFactory imageDataFactory,
            [Import(AllowDefault = true)] ICameraMediator cameraMediator) {

            Logger.Info($"InternalGuider: IProfileService={(profileService != null)}, " +
                        $"ITelescopeMediator={(telescopeMediator != null)}, " +
                        $"IExposureDataFactory={(exposureDataFactory != null)}, " +
                        $"IImageDataFactory={(imageDataFactory != null)}");

            CameraChooserVM chooser = null;
            if (profileService != null && telescopeMediator != null &&
                exposureDataFactory != null && imageDataFactory != null) {
                chooser = new CameraChooserVM(profileService, telescopeMediator, (ISbigSdk)null,
                    exposureDataFactory, imageDataFactory, new NoCameraProviders());
            }
            guider = new InternalGuider(chooser, telescopeMediator, profileService, cameraMediator);
        }

        public string Name => "Internal Guider";

        public IList<IGuider> GetEquipment() => new List<IGuider> { guider };
    }
}
