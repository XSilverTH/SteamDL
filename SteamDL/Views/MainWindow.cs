using Gtk;
using XSTH.Blueprint.Helpers;

namespace SteamDL.Views;

public partial class MainWindow : WindowBase<Adw.ApplicationWindow>
{
    private void OnClickMeButton_Clicked(object? sender, EventArgs e)
    {
        Console.WriteLine("Button was clicked!");
        (sender as Button)!.Label = "Clicked!";
    }
}