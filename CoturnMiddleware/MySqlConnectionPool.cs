using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using MySql.Data.MySqlClient;

namespace CoturnMiddleware
{
    public class MySqlConnectionPool
    {
        private readonly string _connectionString; // 数据库连接字符串
        private readonly ConcurrentBag<MySqlConnection> _connections; // 连接池

        public MySqlConnectionPool(string connectionString)
        {
            _connectionString = connectionString;
            _connections = new ConcurrentBag<MySqlConnection>();
        }

        public async Task<MySqlConnection> GetConnectionAsync()
        {
            if (_connections.TryTake(out var connection))
            {
                if (connection.State == System.Data.ConnectionState.Open)
                {
                    return connection; // 返回已打开的连接
                }
                else
                {
                    await connection.OpenAsync(); // 打开连接
                    return connection;
                }
            }
            else
            {
                var newConnection = new MySqlConnection(_connectionString);
                await newConnection.OpenAsync(); // 创建并打开新连接
                return newConnection;
            }
        }

        public void ReturnConnection(MySqlConnection connection)
        {
            if (connection != null && connection.State == System.Data.ConnectionState.Open)
            {
                _connections.Add(connection); // 将连接返回到连接池
            }
        }
    }
}