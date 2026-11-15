using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.ViewModel;

namespace NINA.Plugin.InternalGuider {
    /// <summary>
    /// Fournisseurs de caméras "vides" : NINA n'exporte pas son IEquipmentProviders&lt;ICamera&gt;
    /// aux plugins. Sans lui, CameraChooserVM liste quand même les caméras natives et ASCOM/Alpaca
    /// (seules les caméras ajoutées par d'AUTRES plugins manqueront).
    ///
    /// La signature ci-dessous est celle que je suppose : si le compilateur proteste,
    /// clique sur IEquipmentProviders, Ctrl + ; > "Implémenter l'interface", et fais renvoyer
    /// une liste vide à GetProviders().
    /// </summary>
    public class NoCameraProviders : IEquipmentProviders<ICamera> {
        public bool Initialized { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

        public void AddProvider(IEquipmentProvider deviceProvider)
        {
            throw new NotImplementedException();
        }

        public Type GetInterfaceType()
        {
            throw new NotImplementedException();
        }

        public Task<IList<IEquipmentProvider<ICamera>>> GetProviders() =>
            Task.FromResult<IList<IEquipmentProvider<ICamera>>>(new List<IEquipmentProvider<ICamera>>());
    }
}
