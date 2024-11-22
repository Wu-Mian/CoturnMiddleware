using System;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CoturnMiddleware
{
    internal class Program
    {
        public static async Task Main(string[] args)
        {
            // 创建并运行主机
            var host = CreateHostBuilder(args).Build();
            await host.RunAsync(); // 异步运行主机
        }

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .ConfigureAppConfiguration((context, config) =>
                {
                    // 设置配置文件路径并加载配置文件
                    config.SetBasePath(AppContext.BaseDirectory)
                          .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
                })
                .ConfigureServices((context, services) =>
                {
                    // 注册身份验证服务、中间件服务器和内存缓存
                    services.AddSingleton<IAuthenticationService, AuthenticationService>();
                    services.AddHostedService<MiddlewareServer>();
                    services.AddMemoryCache(); // 添加内存缓存服务
                })
                .ConfigureLogging(logging =>
                {
                    // 配置日志记录
                    logging.ClearProviders();
                    logging.AddConsole();
                });
    }
}