using System;
using System.Collections.Generic;
using System.Data.SQLite;
using _1RM.Model.Protocol.Base;
using MySql.Data.MySqlClient;
using Npgsql;

namespace _1RM.Service.DataSource.DAO.Dapper
{
    /// <summary>
    /// Thread-safe, pool-friendly DapperDatabase implementation that closes/disposes connections
    /// after each logical operation and automatically clears broken pooled sockets on transient errors.
    /// </summary>
    public sealed class DapperDatabaseFree : DapperDatabase
    {
        public DapperDatabaseFree(string databaseName, DatabaseType databaseType) : base(databaseName, databaseType)
        {
        }

        public override void CloseConnection()
        {
            lock (this)
            {
                try { _dbConnection?.Close(); } catch { /* ignore */ }
                if (DatabaseType == DatabaseType.Sqlite && _dbConnection is SQLiteConnection sqliteConn)
                {
                    try { SQLiteConnection.ClearPool(sqliteConn); } catch { /* ignore */ }
                }
                try { _dbConnection?.Dispose(); } catch { /* ignore */ }
                _dbConnection = null;
            }
        }

        private void ClearStalePoolAndClose()
        {
            lock (this)
            {
                try
                {
                    if (_dbConnection is MySqlConnection mysqlConn)
                        MySqlConnection.ClearPool(mysqlConn);
                    else if (_dbConnection is NpgsqlConnection npgsqlConn)
                        NpgsqlConnection.ClearPool(npgsqlConn);
                }
                catch
                {
                    // ignore pool cleanup failures
                }
                CloseConnection();
            }
        }

        private T ExecuteReadOrIdempotentWithRetry<T>(Func<T> op, bool closeInEnd = true) where T : Result
        {
            lock (this)
            {
                var open = OpenConnection();
                if (!open.IsSuccess && DatabaseType is DatabaseType.MySql or DatabaseType.PostgreSQL)
                {
                    ClearStalePoolAndClose();
                    open = OpenConnection();
                }

                var ret = op();
                if (!ret.IsSuccess && DatabaseType is DatabaseType.MySql or DatabaseType.PostgreSQL)
                {
                    ClearStalePoolAndClose();
                    if (OpenConnection().IsSuccess)
                    {
                        ret = op();
                    }
                }

                if (closeInEnd)
                    CloseConnection();
                return ret;
            }
        }

        private T ExecuteMutationSafe<T>(Func<T> op) where T : Result
        {
            lock (this)
            {
                var open = OpenConnection();
                if (!open.IsSuccess && DatabaseType is DatabaseType.MySql or DatabaseType.PostgreSQL)
                {
                    ClearStalePoolAndClose();
                    OpenConnection();
                }

                var ret = op();
                if (!ret.IsSuccess && !IsConnected() && DatabaseType is DatabaseType.MySql or DatabaseType.PostgreSQL)
                {
                    ClearStalePoolAndClose();
                    if (OpenConnection().IsSuccess)
                    {
                        ret = op();
                    }
                }

                CloseConnection();
                return ret;
            }
        }

        public override Result InitTables() => ExecuteReadOrIdempotentWithRetry(() => base.InitTables());

        public override ResultSelects<ProtocolBase> GetServers() => ExecuteReadOrIdempotentWithRetry(() => base.GetServers());

        public override Result AddServer(ref ProtocolBase protocolBase)
        {
            lock (this)
            {
                var tmp = protocolBase;
                var ret = ExecuteMutationSafe(() => base.AddServer(ref tmp));
                protocolBase = tmp;
                return ret;
            }
        }

        public override Result AddServer(IEnumerable<ProtocolBase> protocolBases) => ExecuteMutationSafe(() => base.AddServer(protocolBases));

        public override Result UpdateServer(ProtocolBase server) => ExecuteReadOrIdempotentWithRetry(() => base.UpdateServer(server));

        public override Result UpdateServer(IEnumerable<ProtocolBase> servers) => ExecuteReadOrIdempotentWithRetry(() => base.UpdateServer(servers));

        public override Result DeleteServer(IEnumerable<string> ids) => ExecuteMutationSafe(() => base.DeleteServer(ids));

        public override ResultString GetConfig(string key, bool closeInEnd = true) => ExecuteReadOrIdempotentWithRetry(() => base.GetConfig(key), closeInEnd);

        public override Result SetConfig(string key, string? value, bool closeInEnd = true) => ExecuteReadOrIdempotentWithRetry(() => base.SetConfig(key, value), closeInEnd);

        public override Result SetTableUpdateTimestamp(string dataType, long time = -1, bool closeInEnd = true) => ExecuteReadOrIdempotentWithRetry(() => base.SetTableUpdateTimestamp(dataType, time), closeInEnd);

        public override ResultLong GetTableUpdateTimestamp(string tableName, bool closeInEnd = true) => ExecuteReadOrIdempotentWithRetry(() => base.GetTableUpdateTimestamp(tableName), closeInEnd);

        public override ResultSelects<Credential> GetCredentials(bool closeInEnd = true) => ExecuteReadOrIdempotentWithRetry(() => base.GetCredentials(), closeInEnd);

        public override Result AddCredential(ref Credential credential)
        {
            lock (this)
            {
                var tmp = credential;
                var ret = ExecuteMutationSafe(() => base.AddCredential(ref tmp));
                credential = tmp;
                return ret;
            }
        }

        public override Result UpdateCredential(Credential credential, List<ProtocolBaseWithAddressPortUserPwd>? relatedProtocols = null) =>
            ExecuteMutationSafe(() => base.UpdateCredential(credential, relatedProtocols));

        public override Result DeleteCredential(IEnumerable<string> names, List<ProtocolBaseWithAddressPortUserPwd>? relatedProtocols = null) =>
            ExecuteMutationSafe(() => base.DeleteCredential(names, relatedProtocols));
    }
}
