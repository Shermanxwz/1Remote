using _1RM.Model.Protocol.Base;
using _1RM.Service.DataSource.DAO.Dapper;
using _1RM.Service.DataSource.Model;
using _1RM.Utils;
using MySql.Data.MySqlClient;
using Npgsql;
using Shawn.Utils;
using System;
using System.Collections.Generic;
// ReSharper disable InconsistentNaming

namespace _1RM.Service.DataSource.DAO
{
    public enum DatabaseType
    {
        MySql,
        SqlServer,
        PostgreSQL,
        Oracle,
        Sqlite,
    }

    public class Result
    {
        public bool IsSuccess = false;
        public bool NeedReloadUI = false;
        public string ErrorInfo = string.Empty;
        private static readonly Result _SUCCESS = new Result() { IsSuccess = true };
        public static Result Success() => _SUCCESS;

        public static string GetErrorInfo(string message, string databaseName, string reason)
        {
            return $@"{message.TrimEnd()} `{databaseName}`\n{IoC.Translate("because:")}\n{reason}";
        }
        public static Result Fail(string message, DataSourceBase? database, string reason)
        {
            return Fail(message, database?.DataSourceName ?? "", reason);
        }
        public static Result Fail(string message, string databaseName, string reason)
        {
            return new Result() { IsSuccess = false, ErrorInfo = GetErrorInfo(message, databaseName, reason) };
        }
        public static Result Fail(string message)
        {
            return new Result() { IsSuccess = false, ErrorInfo = message };
        }
    }

    public class ResultSelects<T> : Result
    {
        public readonly List<T> Items;
        public ResultSelects(List<T> items) { Items = items; }
        public static ResultSelects<T> Success(List<T> protocolBases) => new ResultSelects<T>(protocolBases) { IsSuccess = true };
        public new static ResultSelects<T> Fail(string message, string databaseName, string reason) => new ResultSelects<T>(null!) { IsSuccess = false, ErrorInfo = GetErrorInfo(message, databaseName, reason) };
        public new static ResultSelects<T> Fail(string message) => new ResultSelects<T>(null!) { IsSuccess = false, ErrorInfo = message };
    }

    public class ResultLong : Result
    {
        public readonly long Result;
        public ResultLong(long result) { Result = result; }
        public static ResultLong Success(long result) => new ResultLong(result) { IsSuccess = true };
        public new static ResultLong Fail(string message, string databaseName, string reason) => new ResultLong(0) { IsSuccess = false, ErrorInfo = GetErrorInfo(message, databaseName, reason) };
        public new static ResultLong Fail(string message) => new ResultLong(0) { IsSuccess = false, ErrorInfo = message };
    }

    public class ResultString : Result
    {
        public readonly string? Result;
        public ResultString(string? result) { Result = result; }
        public static ResultString Success(string? result) => new ResultString(result) { IsSuccess = true };
        public new static ResultString Fail(string message, string databaseName, string reason) => new ResultString(string.Empty) { IsSuccess = false, ErrorInfo = GetErrorInfo(message, databaseName, reason) };
        public new static ResultString Fail(string message) => new ResultString(string.Empty) { IsSuccess = false, ErrorInfo = message };
    }

    public abstract class IDatabase
    {
        public string DatabaseName;
        protected readonly DatabaseType DatabaseType;
        protected string _connectionString = "";

        protected IDatabase(string databaseName, DatabaseType databaseType)
        {
            DatabaseName = databaseName;
            DatabaseType = databaseType;
        }

        public abstract void CloseConnection();
        public abstract Result OpenNewConnection(string newConnectionString);
        public abstract bool IsConnected();
        public abstract Result InitTables();
        public abstract Result TableExists(string tableName);
        public abstract ResultSelects<ProtocolBase> GetServers();
        public abstract Result AddServer(ref ProtocolBase protocolBase);
        public abstract Result AddServer(IEnumerable<ProtocolBase> protocolBases);
        public abstract Result UpdateServer(ProtocolBase server);
        public abstract Result UpdateServer(IEnumerable<ProtocolBase> servers);
        public abstract Result DeleteServer(IEnumerable<string> ids);
        public abstract ResultSelects<Credential> GetCredentials(bool closeInEnd = true);
        public abstract Result AddCredential(ref Credential credential);
        public abstract Result UpdateCredential(Credential credential, List<ProtocolBaseWithAddressPortUserPwd>? relatedProtocols = null);
        public abstract Result DeleteCredential(IEnumerable<string> names, List<ProtocolBaseWithAddressPortUserPwd>? relatedProtocols = null);
        public abstract ResultString GetConfig(string key, bool closeInEnd = true);
        public abstract Result SetConfig(string key, string? value, bool closeInEnd = true);
        public abstract Result SetTableUpdateTimestamp(string tableName, long time = -1, bool closeInEnd = true);
        public abstract ResultLong GetTableUpdateTimestamp(string tableName, bool closeInEnd = true);

        protected void SetEncryptionTest()
        {
            var get = GetConfig("EncryptionTest");
            if (string.IsNullOrEmpty(get.Result))
            {
                SetConfig("EncryptionTest", UnSafeStringEncipher.SimpleEncrypt("EncryptionTest"));
            }
        }

        public bool CheckEncryptionTest()
        {
            var et = GetConfig("EncryptionTest");
            return UnSafeStringEncipher.SimpleDecrypt(et.Result ?? "") == "EncryptionTest";
        }
    }

    public static class DbExtensions
    {
        public static string GetSqliteConnectionString(string dbPath)
        {
            var dataSource = dbPath;
            if (dbPath.StartsWith(@"\\"))
            {
                dataSource = "\\\\" + dataSource.Trim('\\');
            }
            return $"Data Source={dataSource}; Pooling=true;Min Pool Size=1";
        }

        public static string GetMysqlConnectionString(string host, int port, string dbName, string user, string password, int connectTimeOutSeconds)
        {
            var timeout = (uint)Math.Max(connectTimeOutSeconds, 10);
            var csb = new MySqlConnectionStringBuilder
            {
                Server = host,
                Port = (uint)port,
                Database = dbName,
                UserID = user,
                Password = password,
                CharacterSet = "utf8mb4",
                ConnectionTimeout = timeout,
                DefaultCommandTimeout = Math.Max(timeout * 2, 20),
                Pooling = true,
                ConnectionReset = true,
                ConnectionLifeTime = 180,
                Keepalive = 30,
            };
            return csb.ConnectionString;
        }

        public static string GetPgsqlConnectionString(string host, int port, string dbName, string user, string password, int connectTimeOutSeconds)
        {
            var timeout = Math.Max(connectTimeOutSeconds, 10);
            var csb = new NpgsqlConnectionStringBuilder
            {
                Host = host,
                Port = port,
                Database = dbName,
                Username = user,
                Password = password,
                Timeout = timeout,
                CommandTimeout = Math.Max(timeout * 2, 20),
                KeepAlive = 30,
                Pooling = true,
            };
            return csb.ConnectionString;
        }

        public static bool CheckWritable(this IDatabase iDatabase)
        {
            try
            {
                return iDatabase.SetConfig("permission_check", null).IsSuccess
                    && iDatabase.SetConfig("permission_check", "true").IsSuccess
                    && iDatabase.SetConfig("permission_check", "true").IsSuccess;
            }
            catch (Exception e)
            {
                SimpleLogHelper.Info(e);
                return false;
            }
        }
    }
}
