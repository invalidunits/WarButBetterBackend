
using Microsoft.AspNetCore.StaticFiles;

namespace WarButBetterBackend
{
    public static class Program
    {
        public static void Main(String[] args)
        {
            var builder = WebApplication.CreateBuilder(args);
            builder.WebHost.UseUrls("http://localhost:8080");
            builder.Services.AddRazorPages();
            builder.Services.AddControllers();
            builder.Services.AddHostedService<MatchmakingController.MatchCleanupService>();

            var app = builder.Build();
            var contentTypeProvider = new FileExtensionContentTypeProvider();
            contentTypeProvider.Mappings[".pck"] = "application/octet-stream";

            app.UseWebSockets();
            app.UseStaticFiles(new StaticFileOptions
            {
                ContentTypeProvider = contentTypeProvider,
            });
            app.MapControllers();
            app.MapRazorPages();
            app.Run();
        }
    }
}