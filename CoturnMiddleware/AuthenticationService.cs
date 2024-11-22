using System;
using System.Threading.Tasks;
using MySql.Data.MySqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Caching.Memory;
using System.Net.Sockets;

namespace CoturnMiddleware
{
    public interface IAuthenticationService
    {
        bool IsAuthenticationRequest(string request); // 检查是否为身份验证请求

        Task<string> HandleAuthentication(string request, TcpClient client); // 处理身份验证请求
    }

    public class AuthenticationService : IAuthenticationService
    {
        private readonly IConfiguration _configuration; // 配置
        private readonly ILogger<AuthenticationService> _logger; // 日志记录器
        private readonly IMemoryCache _cache; // 内存缓存
        private readonly MySqlConnectionPool _connectionPool; // 数据库连接池

        public AuthenticationService(IConfiguration configuration, ILogger<AuthenticationService> logger, IMemoryCache cache)
        {
            _configuration = configuration;
            _logger = logger;
            _cache = cache;
            var connectionString = _configuration.GetConnectionString("DefaultConnection");
            if (string.IsNullOrEmpty(connectionString))
            {
                throw new ArgumentNullException(nameof(connectionString), "Connection string cannot be null or empty.");
            }
            _connectionPool = new MySqlConnectionPool(connectionString);

            //_connectionPool = new MySqlConnectionPool(_configuration.GetConnectionString("DefaultConnection")); // 初始化连接池
        }

        public bool IsAuthenticationRequest(string request)
        {
            string[] parts = request.Split(':'); // 分割请求字符串
            if (parts.Length != 2)
            {
                return false; // 如果请求不包含两个部分，则不是身份验证请求
            }

            string username = parts[0];
            string password = parts[1];

            if (username.Length < 4 || username.Length > 16 || password.Length < 4 || password.Length > 16)
            {
                return false; // 如果用户名或密码长度不符合要求，则不是身份验证请求
            }

            return true; // 请求格式正确，返回 true
        }

        public async Task<string> HandleAuthentication(string request, TcpClient client)
        {
            string[] parts = request.Split(':'); // 分割请求字符串
            if (parts.Length != 2)
            {
                return "{\"result\": \"error\", \"message\": \"Invalid request format\"}"; // 请求格式无效
            }

            string username = parts[0];
            string password = parts[1];
            var clientId = client.Client.RemoteEndPoint.ToString(); // 获取客户端 ID
            var clientIpPort = client.Client.RemoteEndPoint.ToString(); // 使用 `RemoteEndPoint` 获取客户端 IP 和端口

            // 检查缓存中是否有该用户的密码
            if (_cache.TryGetValue(username, out string? cachedPassword))
            {
                if (cachedPassword == password)
                {
                    return "{\"result\": \"success\", \"message\": \"Authentication successful\"}"; // 缓存命中，身份验证成功
                }
                else
                {
                    return "{\"result\": \"failure\", \"message\": \"Authentication failed\"}"; // 缓存命中，但密码不匹配，身份验证失败
                }
            }

            try
            {
                using (var conn = await _connectionPool.GetConnectionAsync()) // 从连接池获取数据库连接
                {
                    string query = "SELECT hmackey FROM turnusers_lt WHERE name=@username"; // 查询用户的 HMAC 密钥
                    MySqlCommand cmd = new MySqlCommand(query, conn);
                    cmd.Parameters.AddWithValue("@username", username); // 添加查询参数
                    object? result = await cmd.ExecuteScalarAsync(); // 执行查询并获取结果
                    if (result != null && result.ToString() == password)
                    {
                        _cache.Set(username, password, TimeSpan.FromMinutes(10)); // 缓存结果
                        return "{\"result\": \"success\", \"message\": \"Authentication successful\"}"; // 身份验证成功
                    }
                    else
                    {
                        return "{\"result\": \"failure\", \"message\": \"Authentication failed\"}"; // 身份验证失败
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during authentication"); // 记录错误日志
                return "{\"result\": \"error\", \"message\": \"Authentication error\"}"; // 身份验证过程中发生错误
            }
        }
    }
}