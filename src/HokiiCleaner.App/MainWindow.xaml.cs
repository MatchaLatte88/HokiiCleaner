using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using HokiiCleaner.App.Infrastructure;
using HokiiCleaner.App.ViewModels;

namespace HokiiCleaner.App;

public partial class MainWindow : Window
{
    private static readonly Geometry MaximizeGeometry = Geometry.Parse("M0.5,0.5 H9.5 V9.5 H0.5 Z");
    private static readonly Geometry RestoreGeometry = Geometry.Parse("M2.5,2.5 V0.5 H9.5 V7.5 H7.5 M0.5,2.5 H7.5 V9.5 H0.5 Z");

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowEffects.UseRoundedCorners(this);
        StateChanged += (_, _) => UpdateWindowState();
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext;

    private void UpdateWindowState()
    {
        // Ein Fenster ohne Systemrahmen ragt maximiert über den Bildschirmrand; der Rand wird hier abgezogen.
        WindowRoot.Margin = WindowState == WindowState.Maximized
            ? new Thickness(
                SystemParameters.WindowResizeBorderThickness.Left + SystemParameters.WindowNonClientFrameThickness.Left,
                SystemParameters.WindowResizeBorderThickness.Top + SystemParameters.WindowNonClientFrameThickness.Top,
                SystemParameters.WindowResizeBorderThickness.Right + SystemParameters.WindowNonClientFrameThickness.Right,
                SystemParameters.WindowResizeBorderThickness.Bottom + SystemParameters.WindowNonClientFrameThickness.Bottom)
            : new Thickness(0);
        MaximizeIcon.Data = WindowState == WindowState.Maximized ? RestoreGeometry : MaximizeGeometry;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            SystemCommands.RestoreWindow(this);
        }
        else
        {
            SystemCommands.MaximizeWindow(this);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

    private void CategoryRow_MouseEnter(object sender, MouseEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is CategoryRowViewModel row)
        {
            ViewModel.HoveredCategory = row.Category;
        }
    }

    private void CategoryRow_MouseLeave(object sender, MouseEventArgs e) => ViewModel.HoveredCategory = null;

    private void FileRow_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && ((FrameworkElement)sender).DataContext is FileRowViewModel file)
        {
            ViewModel.Reveal(file);
        }
    }

    private void PlaceRow_MouseEnter(object sender, MouseEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is FolderRowViewModel row)
        {
            ViewModel.Places.HoveredRow = row;
        }
    }

    private void PlaceRow_MouseLeave(object sender, MouseEventArgs e) => ViewModel.Places.HoveredRow = null;

    private void PlaceRow_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is FolderRowViewModel row && ViewModel.Places.OpenCommand.CanExecute(row))
        {
            ViewModel.Places.OpenCommand.Execute(row);
        }
    }

    /// <summary>Enter öffnet den gewählten Ordner, Rücktaste geht eine Ebene hoch.</summary>
    private void PlaceList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        PlacesViewModel places = ViewModel.Places;
        if (e.Key == Key.Enter && places.SelectedRow is FolderRowViewModel row && places.OpenCommand.CanExecute(row))
        {
            places.OpenCommand.Execute(row);
            e.Handled = true;
        }
        else if (e.Key == Key.Back && places.UpCommand.CanExecute(null))
        {
            places.UpCommand.Execute(null);
            e.Handled = true;
        }
    }
}
