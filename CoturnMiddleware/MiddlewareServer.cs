using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Generic; // 用于存储客户端连接
using System.Collections.Concurrent;  // 引入用于并发集合的命名空间

using System.Collections.Generic;  // 引入用于存储客户端连接的泛型集合命名空间

namespace CoturnMiddleware
{
    public class MiddlewareServer : BackgroundService
    {
        private readonly ILogger<MiddlewareServer> _logger; // 日志记录器
        private readonly IAuthenticationService _authService; // 身份验证服务
        private readonly IConfiguration _configuration; // 配置
        private readonly TcpListener _listener; // TCP 监听器
        private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(3000); // 控制并发连接数

        //private readonly Dictionary<string, ClientConnection> _clients = new Dictionary<string, ClientConnection>(); // 存储客户端连接
        private readonly ConcurrentDictionary<string, List<ClientConnection>> _clients = new ConcurrentDictionary<string, List<ClientConnection>>();  // 并发字典，用于存储客户端连接

        public MiddlewareServer(ILogger<MiddlewareServer> logger, IAuthenticationService authService, IConfiguration configuration)
        {
            _logger = logger;
            _authService = authService;
            _configuration = configuration;
            int port = _configuration.GetValue<int>("Middleware:Port"); // 获取监听端口
            _listener = new TcpListener(System.Net.IPAddress.Any, port); // 创建 TCP 监听器
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _listener.Start();  // 启动 TCP 监听器
            _logger.LogInformation("中间件服务器已启动，等待连接...");  // 记录服务器启动信息

            // 持续等待新的客户端连接，直到收到停止请求
            while (!stoppingToken.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync();  // 接受客户端连接
                await _semaphore.WaitAsync(stoppingToken);  // 控制并发连接数
                _ = Task.Run(() => HandleClient(client, stoppingToken), stoppingToken).ContinueWith(t => _semaphore.Release());  // 异步处理客户端连接，并在完成后释放信号量
            }
        }

        private async Task HandleClient(TcpClient client, CancellationToken stoppingToken)
        {
            try
            {
                using (client)
                {
                    NetworkStream stream = client.GetStream(); // 获取网络流
                    byte[] buffer = new byte[1024]; // 创建缓冲区
                    //int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length); // 异步读取数据
                    //string request = Encoding.UTF8.GetString(buffer, 0, bytesRead); // 将字节数组转换为字符串
                    //string response;

                    int bytesRead;  // 读取的字节数

                    // 持续读取客户端数据，直到连接关闭或出现错误
                    while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, stoppingToken)) != 0)
                    {
                        string request = Encoding.UTF8.GetString(buffer, 0, bytesRead);  // 将字节数组转换为字符串
                        string response;
                        if (_authService.IsAuthenticationRequest(request)) // 检查是否为身份验证请求
                        {
                            response = await _authService.HandleAuthentication(request, client); // 处理身份验证请求
                        }
                        else if (request.StartsWith("SDP:") || request.StartsWith("ICE:")) // 检查是否为信令消息
                        {
                            response = await HandleSignalingMessage(request, client); // 处理信令消息
                        }
                        else
                        {
                            response = await ForwardToCoturn(buffer, bytesRead); // 转发请求到 Coturn 服务器
                        }

                        byte[] responseBytes = Encoding.UTF8.GetBytes(response); // 将响应字符串转换为字节数组
                        await stream.WriteAsync(responseBytes, 0, responseBytes.Length); // 异步发送响应
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling client request"); // 记录错误日志
            }
        }

        private async Task<string> HandleSignalingMessage(string message, TcpClient client)
        {
            try
            {
                var parts = message.Split(':');  // 分割信令消息
                if (parts.Length < 3) return "信令消息格式无效";  // 检查消息格式是否有效
                var messageType = parts[0];  // 获取消息类型
                var signalingData = parts[1];  // 获取信令数据
                var username = parts[2].Split('_')[0];  // 提取用户名部分
                var targetClientMark = parts.Length > 3 ? parts[3] : null;  // 提取目标客户端标记（如果有）

                var clientId = client.Client.RemoteEndPoint.ToString();  // 获取客户端 ID
                var clientConnection = new ClientConnection(clientId, client);  // 创建客户端连接对象

                // 添加客户端到连接列表
                _clients.AddOrUpdate(username, new List<ClientConnection> { clientConnection }, (key, list) =>
                {
                    if (!list.Exists(c => c.Id == clientId))  // 如果列表中不存在该客户端，则添加
                        list.Add(clientConnection);
                    return list;
                });

                if (string.IsNullOrEmpty(targetClientMark))  // 没有指定目标客户端标记
                {
                    // 将消息转发给同一用户名下的所有其他客户端
                    foreach (var conn in _clients[username])
                    {
                        if (conn.Id != clientId)
                        {
                            await SendToClient(conn.TcpClient, $"{messageType}:{signalingData}");
                        }
                    }
                    return $"{messageType}:{signalingData} 已转发给用户名 {username} 下的所有客户端";
                }
                else  // 有指定目标客户端标记
                {
                    // 找到目标客户端
                    var targetClient = _clients[username].Find(c => c.Id == targetClientMark);

                    if (targetClient == null)
                    {
                        return "未找到目标客户端";
                    }

                    // 处理 SDP 或 ICE 消息，并转发给目标客户端
                    if (messageType == "SDP")
                    {
                        await SendToClient(targetClient.TcpClient, $"SDP:{signalingData}");
                        return $"SDP:{signalingData} 已发送给 {targetClientMark}";
                    }
                    else if (messageType == "ICE")
                    {
                        await SendToClient(targetClient.TcpClient, "ICE候选者已接收");
                        return $"ICE候选者已发送给 {targetClientMark}";
                    }

                    return "未知的信令消息类型";
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "处理信令消息时发生错误");  // 记录错误日志
                return "处理信令消息时发生错误";
            }
        }

        // 向客户端发送消息的异步方法
        private async Task SendToClient(TcpClient client, string message)
        {
            try
            {
                NetworkStream stream = client.GetStream();  // 获取网络流
                byte[] responseBytes = Encoding.UTF8.GetBytes(message);  // 将消息字符串转换为字节数组
                await stream.WriteAsync(responseBytes, 0, responseBytes.Length);  // 异步发送消息
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "向客户端发送消息时发生错误");  // 记录错误日志
            }
        }

        private async Task<string> ForwardToCoturn(byte[] buffer, int bytesRead)
        {
            string? coturnServer = _configuration["Coturn:Server"]; // 获取 Coturn 服务器地址
            if (string.IsNullOrEmpty(coturnServer))
            {
                throw new ArgumentNullException(nameof(coturnServer), "Coturn server address cannot be null or empty.");
            }
            int coturnPort = _configuration.GetValue<int>("Coturn:Port"); // 获取 Coturn 服务器端口

            try
            {
                using (TcpClient coturnClient = new TcpClient())
                {
                    await coturnClient.ConnectAsync(coturnServer, coturnPort); // 连接到 Coturn 服务器
                    NetworkStream coturnStream = coturnClient.GetStream(); // 获取 Coturn 服务器的网络流
                    await coturnStream.WriteAsync(buffer, 0, bytesRead); // 异步发送请求
                    byte[] responseBuffer = new byte[1024]; // 创建响应缓冲区
                    int responseBytesRead = await coturnStream.ReadAsync(responseBuffer, 0, responseBuffer.Length); // 异步读取响应
                    Array.Resize(ref responseBuffer, responseBytesRead); // 调整响应缓冲区大小
                    return Encoding.UTF8.GetString(responseBuffer); // 将响应字节数组转换为字符串
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error forwarding request to Coturn"); // 记录错误日志
                return "Error forwarding request to Coturn"; // 返回错误消息
            }
        }
    }
}