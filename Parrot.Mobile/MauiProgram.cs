using Microsoft.Extensions.Logging;
using Parrot.SharedComponents.Services;

namespace Parrot.Mobile
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                });

            builder.Services.AddMauiBlazorWebView();

            // The Aurora shell: rail state, the typed face of aurora-interop.js, and the single toast slot.
            builder.Services.AddScoped<AppShellState>();
            builder.Services.AddScoped<AuroraInterop>();
            builder.Services.AddScoped<ToastService>();

#if DEBUG
    		builder.Services.AddBlazorWebViewDeveloperTools();
    		builder.Logging.AddDebug();
#endif

            return builder.Build();
        }
    }
}
