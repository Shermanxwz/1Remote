using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using _1RM.Model.Protocol;
using _1RM.Model.Protocol.Base;
using _1RM.Service.DataSource.DAO;
using _1RM.Service.DataSource.DAO.Dapper;
using _1RM.Service.DataSource.Model;
using _1RM.Utils;
using _1RM.View;
using MySql.Data.MySqlClient;
using Npgsql;
using Stylet;
using Xunit;

namespace Tests.Service
{
    public class MysqlAndLauncherFixTests
    {
        [Fact]
        public void MysqlAndPgsqlConnectionStrings_IncludeResilientTimeoutsPoolingAndKeepalive()
        {
            var mysqlStr = DbExtensions.GetMysqlConnectionString("127.0.0.1", 3306, "1remote", "root", "secret", 2);
            var mcsb = new MySqlConnectionStringBuilder(mysqlStr);
            Assert.True(mcsb.ConnectionTimeout >= 10);
            Assert.True(mcsb.DefaultCommandTimeout >= 20);
            Assert.Equal("utf8mb4", mcsb.CharacterSet);
            Assert.True(mcsb.Pooling);
            Assert.True(mcsb.ConnectionReset);
            Assert.Equal(180u, mcsb.ConnectionLifeTime);
            Assert.Equal(30u, mcsb.Keepalive);

            var pgStr = DbExtensions.GetPgsqlConnectionString("127.0.0.1", 5432, "1remote", "postgres", "secret", 2);
            var pcsb = new NpgsqlConnectionStringBuilder(pgStr);
            Assert.True(pcsb.Timeout >= 10);
            Assert.True(pcsb.CommandTimeout >= 20);
            Assert.Equal(30, pcsb.KeepAlive);
            Assert.True(pcsb.Pooling);
        }

        [Fact]
        public void MysqlAndPgsqlSources_UseThreadSafePoolFriendlyDapperDatabaseFree()
        {
            var mysql = new MysqlSource { DataSourceName = "mysql-sync", DatabaseName = "1remote" };
            var pgsql = new PgsqlSource { DataSourceName = "pgsql-sync", DatabaseName = "1remote" };

            Assert.IsType<DapperDatabaseFree>(mysql.GetDataBase());
            Assert.IsType<DapperDatabaseFree>(pgsql.GetDataBase());
        }

        private sealed class ContendedMockDatabase : IDatabase
        {
            private int _inFlight = 0;
            private bool _connected = false;
            private readonly string _encTestValue;

            public ContendedMockDatabase(string encTestValue) : base("1remote", DatabaseType.MySql)
            {
                _encTestValue = encTestValue;
            }

            private void EnterCritical()
            {
                if (Interlocked.Increment(ref _inFlight) > 1)
                {
                    Interlocked.Decrement(ref _inFlight);
                    throw new InvalidOperationException("Fatal error encountered during command execution.");
                }
                Thread.Sleep(5);
                Interlocked.Decrement(ref _inFlight);
            }

            public override void CloseConnection()
            {
                EnterCritical();
                _connected = false;
            }

            public override Result OpenNewConnection(string newConnectionString)
            {
                EnterCritical();
                _connected = true;
                return Result.Success();
            }

            public override bool IsConnected() => _connected;

            public override Result InitTables()
            {
                EnterCritical();
                return _connected ? Result.Success() : Result.Fail("Fatal error encountered during command execution.");
            }

            public override Result TableExists(string tableName) => Result.Success();

            public override ResultSelects<ProtocolBase> GetServers()
            {
                EnterCritical();
                return ResultSelects<ProtocolBase>.Success(new List<ProtocolBase>
                {
                    new SSH { Id = "srv-1", DisplayName = "Prod-SSH", Address = "10.0.0.1", Port = "22", UserName = "root" }
                });
            }

            public override Result AddServer(ref ProtocolBase protocolBase) => Result.Success();
            public override Result AddServer(IEnumerable<ProtocolBase> protocolBases) => Result.Success();
            public override Result UpdateServer(ProtocolBase server) => Result.Success();
            public override Result UpdateServer(IEnumerable<ProtocolBase> servers) => Result.Success();
            public override Result DeleteServer(IEnumerable<string> ids) => Result.Success();
            public override ResultSelects<Credential> GetCredentials(bool closeInEnd = true) => ResultSelects<Credential>.Success(new List<Credential>());
            public override Result AddCredential(ref Credential credential) => Result.Success();
            public override Result UpdateCredential(Credential credential, List<ProtocolBaseWithAddressPortUserPwd>? relatedProtocols = null) => Result.Success();
            public override Result DeleteCredential(IEnumerable<string> names, List<ProtocolBaseWithAddressPortUserPwd>? relatedProtocols = null) => Result.Success();

            public override ResultString GetConfig(string key, bool closeInEnd = true)
            {
                EnterCritical();
                if (!_connected)
                    return ResultString.Fail("Fatal error encountered during command execution.");
                return key == "EncryptionTest" ? ResultString.Success(_encTestValue) : ResultString.Success("true");
            }

            public override Result SetConfig(string key, string? value, bool closeInEnd = true)
            {
                EnterCritical();
                return _connected ? Result.Success() : Result.Fail("Fatal error encountered during command execution.");
            }

            public override Result SetTableUpdateTimestamp(string tableName, long time = -1, bool closeInEnd = true) => Result.Success();

            public override ResultLong GetTableUpdateTimestamp(string tableName, bool closeInEnd = true)
            {
                EnterCritical();
                return ResultLong.Success(DateTimeOffset.Now.ToUnixTimeMilliseconds());
            }
        }

        private sealed class ContendedMysqlTestSource : DataSourceBase
        {
            private readonly IDatabase _db;
            public ContendedMysqlTestSource(IDatabase db) { _db = db; DataSourceName = "1remote"; }
            public override IDatabase GetDataBase() => _db;
            public override string GetConnectionString(int connectTimeOutSeconds = 10) => "Server=127.0.0.1;Database=1remote;";
            public override DatabaseType DatabaseType => DatabaseType.MySql;
            public override string Description => "contended mysql source";
        }

        private sealed class InlineDispatcher : IDispatcher
        {
            public void Post(Action action) => action();
            public void Send(Action action) => action();
            public bool IsCurrent => true;
        }

        [Fact]
        public async Task ConcurrentSelfCheckAndSync_NeverTriggersFatalCommandExecutionOrLostConnection()
        {
            UnSafeStringEncipher.Init("unit-test-salt");
            var enc = UnSafeStringEncipher.SimpleEncrypt("EncryptionTest");
            var db = new ContendedMockDatabase(enc);
            var source = new ContendedMysqlTestSource(db);

            var origDispatcher = Execute.Dispatcher;
            Execute.Dispatcher = new InlineDispatcher();
            try
            {
                var tasks = Enumerable.Range(0, 12).Select(i => Task.Run(() =>
                {
                    var check = source.Database_SelfCheck(10);
                    Assert.Equal(EnumDatabaseStatus.OK, check.Status);
                    _ = source.NeedRead("Servers");
                    var servers = source.GetServers(force: i % 2 == 0).ToList();
                    Assert.Equal(EnumDatabaseStatus.OK, source.Status);
                })).ToArray();

                await Task.WhenAll(tasks);
                Assert.Equal(EnumDatabaseStatus.OK, source.Status);
            }
            finally
            {
                Execute.Dispatcher = origDispatcher;
            }
        }

        [Fact]
        public void LauncherWindowViewModel_OpeningGracePeriodPreventsFocusTheft()
        {
            LauncherWindowViewModel.ResetOpeningStateForTest();
            Assert.False(LauncherWindowViewModel.IsLauncherOpenOrOpening);

            LauncherWindowViewModel.MarkOpeningGracePeriod();
            Assert.True(LauncherWindowViewModel.IsLauncherOpenOrOpening);

            LauncherWindowViewModel.ResetOpeningStateForTest();
            Assert.False(LauncherWindowViewModel.IsLauncherOpenOrOpening);
        }
    }
}
