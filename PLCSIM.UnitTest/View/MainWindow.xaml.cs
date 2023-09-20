using PLCSIM.UnitTest.ViewModel;
using System.Windows;

namespace PLCSIM.UnitTest.View
{
    /// <summary>
    /// Interaktionslogik für MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private MainWindowViewModel viewModel;
        public MainWindow()
        {
            InitializeComponent();
            viewModel = new MainWindowViewModel();
            this.DataContext = viewModel;
        }

        //private void ClearLogsButton_Click(object sender, RoutedEventArgs e)
        //{
        //    viewModel.ClearLogsCommand.Execute(null);
        //    LoggingTextBox.LoggingTextBox.Text = "";
        //}
    }

}
