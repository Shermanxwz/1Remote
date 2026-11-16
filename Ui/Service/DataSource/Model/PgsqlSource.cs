using System;
using _1RM.Service.DataSource.DAO;
using _1RM.Service.DataSource.DAO.Dapper;
using _1RM.Utils;
using Newtonsoft.Json;

namespace _1RM.Service.DataSource.Model
{
    public sealed class PgsqlSource : DataSourceBase
    {
        private string _host = "127.0.0.1";
        public string Host
        {
            get => _host;
            set => SetAndNotifyIfChanged(ref _host, value);
        }

        private int _port = 5432;
        public int Port
        {
            get => _port;
            set => SetAndNotifyIfChanged(ref _port, value);
        }

        private string _databaseName = "1Remote";
        public string DatabaseName
        {
            get => _databaseName;
            set => SetAndNotifyIfChanged(ref _databaseName, value);
        }

        private string _userName = "";
        public string UserName
        {
            get => _userName;
            set => SetAndNotifyIfChanged(ref _userName, value);
        }

        [JsonProperty(nameof(Password))]
        private string EncryptPassword { get; set; } = "";

        [JsonIgnore]
        public string Password
        {
            get
            {
                if (string.IsNullOrEmpty(EncryptPassword))
                    return "";
                var t = UnSafeStringEncipher.SimpleDecrypt(EncryptPassword);
                if (string.IsNullOrEmpty(t))
                    return EncryptPassword;
                return t ?? "";
            }
            set
            {
                EncryptPassword = string.IsNullOrEmpty(value) ? "" : UnSafeStringEncipher.SimpleEncrypt(value);
                RaisePropertyChanged();
            }
        }

        public PgsqlSource() : base()
        {
        }

        public override string GetConnectionString(int connectTimeOutSeconds = 10)
        {
            return DbExtensions.GetPgsqlConnectionString(_host, _port, _databaseName, _userName, Password, Math.Max(connectTimeOutSeconds, 10));
        }

        [JsonIgnore]
        public override DatabaseType DatabaseType => DatabaseType.PostgreSQL;

        [JsonIgnore]
        public override string Description => $"Host={Host};Port={Port};Database={DatabaseName};Username={UserName};...";

        private IDatabase? _database = null;
        public override IDatabase GetDataBase()
        {
            lock (this)
            {
                _database ??= new DapperDatabaseFree(DatabaseName, DatabaseType.PostgreSQL);
                _database.DatabaseName = DatabaseName;
                return _database;
            }
        }

        public override DatabaseStatus Database_SelfCheck(int connectTimeOutSeconds = 10)
        {
            lock (this)
            {
                return base.Database_SelfCheck(Math.Max(connectTimeOutSeconds, 10));
            }
        }

        public override void Database_CloseConnection()
        {
            lock (this)
            {
                base.Database_CloseConnection();
            }
        }

        public static bool TestConnection(PgsqlSource config)
        {
            return TestConnection(config.Host, config.Port, config.DatabaseName, config.UserName, config.Password);
        }

        public static bool TestConnection(string host, int port, string dbName, string userName, string password)
        {
            var str = DbExtensions.GetPgsqlConnectionString(host, port, dbName, userName, password, 10);
            var db = new DapperDatabaseFree(dbName, DatabaseType.PostgreSQL);
            var ret = db.OpenNewConnection(str);
            db.CloseConnection();
            return ret.IsSuccess;
        }
    }
}
