
namespace WarButBetterBackend
{
    public static class Program
    {
        public static void Main(String[] args)
        {
            var builder = WebApplication.CreateBuilder(args);
            // builder.WebHost.UseUrls("http://localhost:8080");
            builder.Services.AddControllers();
            builder.Services.AddHostedService<MatchmakingController.MatchCleanupService>();

            var app = builder.Build();
            app.UseWebSockets();
            app.MapControllers();
            // app.UseHttpsRedirection();

            app.Run();
        }
    }
}