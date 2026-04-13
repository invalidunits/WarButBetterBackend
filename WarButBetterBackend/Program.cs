
namespace WarButBetterBackend
{
    public static class Program
    {
        public static void Main(String[] args)
        {
            var builder = WebApplication.CreateBuilder(args);
            builder.Services.AddControllers();
            builder.Services.AddHostedService<MatchmakingController.MatchCleanupService>();

            var app = builder.Build();
            app.UseWebSockets();
            app.MapControllers();
            app.UseHttpsRedirection();

            app.Run();
        }
    }
}