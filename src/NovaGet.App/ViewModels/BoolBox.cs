namespace NovaGet.App.ViewModels;

/// <summary>Boxed booleans for XAML CommandParameters of <c>RelayCommand&lt;bool&gt;</c> (a "True" string wouldn't convert).</summary>
public static class BoolBox
{
    public static readonly object True = true;
    public static readonly object False = false;
}
