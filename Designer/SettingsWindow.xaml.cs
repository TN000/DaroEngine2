// Designer/SettingsWindow.xaml.cs
using System.Windows;
using DaroDesigner.Models;

namespace DaroDesigner
{
    public partial class SettingsWindow : Window
    {
        public SettingsWindow()
        {
            InitializeComponent();
            LoadSettings();
        }

        private void LoadSettings()
        {
            CmbEdgeSmoothing.SelectedIndex = (int)AppSettingsModel.EdgeSmoothing;
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            AppSettingsModel.EdgeSmoothing = (EdgeSmoothingLevel)CmbEdgeSmoothing.SelectedIndex;
            AppSettingsModel.Save();
            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
