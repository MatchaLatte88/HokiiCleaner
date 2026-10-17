using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HokiiCleaner.App.ViewModels;

namespace HokiiCleaner.App.Views;

public partial class SonarView : UserControl
{
    public SonarView()
    {
        InitializeComponent();
    }

    /// <summary>Doppelklick auf eine der größten Dateien zeigt sie im Explorer.</summary>
    private void FileRow_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2
            && ((FrameworkElement)sender).DataContext is FileRowViewModel file
            && DataContext is MainViewModel viewModel)
        {
            viewModel.Reveal(file);
        }
    }
}
