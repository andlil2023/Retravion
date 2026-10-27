using MudBlazor;

namespace Retravion.Web.Components.Layout;

internal static class PrototypeTheme
{
    // A provisional palette drawn from the login photograph; not an established pattern yet.
    internal static MudTheme Value { get; } = new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = "#126779",
            Secondary = "#244967",
            Background = "#f5f4ef",
            Surface = "#ffffff",
            TextPrimary = "#20384a",
            TextSecondary = "#526373",
            DrawerBackground = "#142e43",
            DrawerText = "#e5edf3",
            AppbarBackground = "#ffffff",
            AppbarText = "#20384a"
        },
        PaletteDark = new PaletteDark
        {
            Primary = "#6bd4d4",
            Secondary = "#9dbfd9",
            Background = "#101e2a",
            Surface = "#192c3b",
            TextPrimary = "#e5edf3",
            TextSecondary = "#b3c3cf",
            DrawerBackground = "#0d2131",
            DrawerText = "#e5edf3",
            AppbarBackground = "#192c3b",
            AppbarText = "#e5edf3"
        },
        Typography = new Typography
        {
            Default = new DefaultTypography
            {
                FontFamily = ["Segoe UI", "Arial", "sans-serif"]
            }
        },
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "8px"
        }
    };
}
