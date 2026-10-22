using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BikeSync.App.ViewModels;

namespace BikeSync.App.Views;

public partial class ActivitiesPage : UserControl
{
    public ActivitiesPage()
    {
        InitializeComponent();
    }

    private void OnActivityDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is ActivityLibraryViewModel viewModel &&
            ActivityGrid.SelectedItem is ActivityRowViewModel row)
        {
            viewModel.OpenDetailCommand.Execute(row);
        }
    }
}
