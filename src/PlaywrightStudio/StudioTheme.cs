using MudBlazor;

namespace PlaywrightStudio;

public static class StudioTheme
{
    public static readonly MudTheme Dark = new()
    {
        PaletteDark = new PaletteDark
        {
            // The logo's leaf green (#40a156), lifted to carry on a near-black background -
            // the mark itself stays the darker original, so the two read as one family.
            Primary = "#56be74",
            Secondary = "#8fd3a3",
            Tertiary = "#c084fc",
            Info = "#4fc3f7",
            Success = "#3ecf8e",
            Warning = "#ffc857",
            Error = "#ff6b81",
            Background = "#0f1115",
            Surface = "#171a20",
            DrawerBackground = "#12151a",
            DrawerText = "#c9ced6",
            AppbarBackground = "#12151a",
            AppbarText = "#e8eaed",
            TextPrimary = "#e8eaed",
            TextSecondary = "#9aa2ad",
            ActionDefault = "#9aa2ad",
            Divider = "#262b33",
            LinesDefault = "#262b33",
            TableLines = "#232830",
            OverlayDark = "rgba(8,10,14,0.75)"
        },
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "8px",
            DrawerMiniWidthLeft = "58px",
            DrawerWidthLeft = "230px"
        },
        Typography = new Typography
        {
            Default = new DefaultTypography
            {
                FontFamily = new[] { "Segoe UI", "system-ui", "-apple-system", "Roboto", "sans-serif" }
            }
        }
    };
}
