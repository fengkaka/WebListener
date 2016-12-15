using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.HttpSys;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SelfHostServer
{
    public class Startup
    {
        public void ConfigureServices(IServiceCollection services)
        {
            // Server options can be configured here instead of in Main.
            services.Configure<HttpSysOptions>(options =>
            {
                options.Authentication.Schemes = AuthenticationSchemes.None;
                options.Authentication.AllowAnonymous = true;
            });
        }

        public void Configure(IApplicationBuilder app, ILoggerFactory loggerfactory)
        {
            loggerfactory.AddConsole(LogLevel.Debug);

            app.Run(async context =>
            {
                if (context.WebSockets.IsWebSocketRequest)
                {
                    byte[] bytes = Encoding.ASCII.GetBytes("Hello World: " + DateTime.Now);
                    WebSocket webSocket = await context.WebSockets.AcceptWebSocketAsync();
                    await webSocket.SendAsync(new ArraySegment<byte>(bytes, 0, bytes.Length), WebSocketMessageType.Text, true, CancellationToken.None);
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Goodbye", CancellationToken.None);
                    webSocket.Dispose();
                }
                else
                {
                    context.Response.ContentType = "text/plain";
                    await context.Response.WriteAsync("Hello world from " + context.Request.Host + " at " + DateTime.Now);
                }
            });
        }

        public static void Main(string[] args)
        {
            var host = new WebHostBuilder()
                .UseStartup<Startup>()
                .UseHttpSys(options =>
                {
                    options.Authentication.Schemes = AuthenticationSchemes.None;
                    options.Authentication.AllowAnonymous = true;
                })
                .Build();

            host.Run();
        }
    }
}
