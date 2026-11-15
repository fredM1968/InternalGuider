using System.Windows;
using NINA.WPF.Base.ViewModel.Equipment.Camera;

namespace NINA.Plugin.InternalGuider {
    public partial class ConfigWindow : Window {
        private readonly ConfigViewModel viewModel;

        public ConfigWindow(CameraChooserVM chooser, System.Func<System.Collections.Generic.IList<NINA.Equipment.Interfaces.IDevice>> extraDevices = null) {
            InitializeComponent();
            WindowTheme.Apply(this);
            viewModel = new ConfigViewModel(chooser, extraDevices);
            DataContext = viewModel;
            Loaded += async (s, e) => await viewModel.LoadAsync();
            Closing += (s, e) => viewModel.SaveSelection();
        }

        private void CameraSettings_Click(object sender, RoutedEventArgs e) => viewModel.OpenCameraSettings();

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
