// ============================================================================
// SakuraEDL - Multi Instance Window
// ============================================================================

using System;
using System.Windows;

namespace SakuraEDL.Views
{
    public partial class MultiInstanceForm : Window
    {
        public MultiInstanceForm()
        {
            InitializeComponent();
        }

        public string Message
        {
            get => MessageLabel.Text;
            set => MessageLabel.Text = value;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = true;
            this.Close();
        }
    }
}
