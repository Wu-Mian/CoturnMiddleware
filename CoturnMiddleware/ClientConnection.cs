using System.Net.Sockets; // 使用 System.Net.Sockets 库

namespace CoturnMiddleware
{
    public class ClientConnection
    {
        public string Id { get; set; }  // 客户端 ID
        public TcpClient TcpClient { get; set; }  // TCP 客户端

        // 构造函数，初始化客户端连接
        public ClientConnection(string id, TcpClient tcpClient)
        {
            Id = id;  // 初始化客户端 ID
            TcpClient = tcpClient;  // 初始化 TCP 客户端
        }
    }
}