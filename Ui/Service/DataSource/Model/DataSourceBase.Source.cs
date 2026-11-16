using System;
using System.Collections.Generic;
using System.Linq;
using _1RM.Model.Protocol.Base;
using _1RM.Service.DataSource.DAO;
using _1RM.Service.DataSource.DAO.Dapper;
using _1RM.View;
using Newtonsoft.Json;
using Shawn.Utils;
using Stylet;

namespace _1RM.Service.DataSource.Model
{
    public abstract partial class DataSourceBase : NotifyPropertyChangedBase
    {
        private bool _isWritable = true;

        [JsonIgnore]
        public bool IsWritable
        {
            get => _isWritable;
            protected set => SetAndNotifyIfChanged(ref _isWritable, value);
        }

        [JsonIgnore]
        public List<ProtocolBaseViewModel> CachedProtocols { get; protected set; } = new List<ProtocolBaseViewModel>();
        [JsonIgnore]
        public List<Credential> CachedCredentials { get; protected set; } = new List<Credential>();

        private EnumDatabaseStatus? _lastReadDataSourceStatus = null;

        [JsonIgnore]
        private readonly Dictionary<string, long> _lastReadTimestamp = new Dictionary<string, long>();

        public bool NeedRead(string tableName)
        {
            lock (this)
            {
                if (!_lastReadTimestamp.ContainsKey(tableName) || _lastReadTimestamp[tableName] <= 0)
                {
                    SimpleLogHelper.Debug($"Check NeedRead of [{DataSourceName}] = RAM 0 < DB any = True");
                    return true;
                }

                if (_lastReadDataSourceStatus != null && _lastReadDataSourceStatus != Status)
                {
                    _lastReadDataSourceStatus = Status;
                    return true;
                }

                if (Status != EnumDatabaseStatus.OK)
                {
                    return true;
                }

                var dataBase = GetDataBase();
                var ret = dataBase.GetTableUpdateTimestamp(tableName);
                if (!ret.IsSuccess)
                {
                    SimpleLogHelper.Debug($"Check NeedRead of [{DataSourceName}] = RAM {_lastReadTimestamp[tableName]} < DB {ret.Result} = {_lastReadTimestamp[tableName] < ret.Result}");
                    SetStatus(false);
                }
                else
                {
                    return _lastReadTimestamp[tableName] < ret.Result;
                }
                return true;
            }
        }

        public void ClearReadTimestamp()
        {
            lock (this)
            {
                _lastReadTimestamp.Clear();
            }
        }

        private void SetReadTimestampTo0(string tableName)
        {
            SetReadTimestamp(tableName, 0);
        }

        private void SetReadTimestamp(string tableName, long timestamp = -1)
        {
            lock (this)
            {
                if (timestamp < 0)
                {
                    timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                }
                if (_lastReadTimestamp.ContainsKey(tableName))
                {
                    _lastReadTimestamp[tableName] = timestamp;
                }
                else
                {
                    _lastReadTimestamp.Add(tableName, timestamp);
                }
            }
        }

        public IEnumerable<ProtocolBaseViewModel> GetServers(bool force = false)
        {
            if (Status != EnumDatabaseStatus.OK || (force == false && !NeedRead(TableServer.TABLE_NAME)))
            {
                return CachedProtocols;
            }

            List<ProtocolBase> protocols;
            lock (this)
            {
                var result = Database_GetServers();
                if (!result.IsSuccess)
                {
                    return CachedProtocols;
                }
                SetReadTimestamp(TableServer.TABLE_NAME);
                SetStatus(true);
                protocols = result.Items;
            }

            // Create view models OUTSIDE lock(this): dispatching under the lock deadlocks the UI thread.
            var viewModels = new List<ProtocolBaseViewModel>(protocols.Count);
            try
            {
                Execute.OnUIThreadSync(() =>
                {
                    foreach (var protocol in protocols)
                    {
                        try
                        {
                            viewModels.Add(new ProtocolBaseViewModel(protocol));
                        }
                        catch (Exception e)
                        {
                            SimpleLogHelper.DebugInfo(e);
                        }
                    }
                });
            }
            catch (Exception e)
            {
                SimpleLogHelper.Warning(e);
            }
            lock (this)
            {
                CachedProtocols = viewModels;
            }
            return viewModels;
        }

        public abstract IDatabase GetDataBase();

        private void SetStatus(bool isConnected)
        {
            if (isConnected)
            {
                Status = EnumDatabaseStatus.OK;
            }
            else
            {
                Status = Status != EnumDatabaseStatus.NotConnectedYet ? EnumDatabaseStatus.LostConnection : EnumDatabaseStatus.AccessDenied;
            }
        }

        public Tuple<bool, string> Database_OpenConnection(int connectTimeOutSeconds = 5)
        {
            lock (this)
            {
                try
                {
                    var dataBase = GetDataBase();
                    var connectionString = GetConnectionString(connectTimeOutSeconds);
                    if (connectionString != _lastConnectionString)
                    {
                        dataBase.CloseConnection();
                        _lastConnectionString = connectionString;
                    }

                    var result = dataBase.OpenNewConnection(connectionString);
                    SetStatus(dataBase.IsConnected() == true);
                    if (!result.IsSuccess)
                    {
                        SimpleLogHelper.DebugError(result.ErrorInfo);
                        return new Tuple<bool, string>(false, result.ErrorInfo);
                    }
                }
                catch (Exception e)
                {
                    SetStatus(false);
                    return new Tuple<bool, string>(false, e.Message);
                }

                return new Tuple<bool, string>(true, "");
            }
        }

        public virtual void Database_CloseConnection()
        {
            lock (this)
            {
                var dataBase = GetDataBase();
                if (dataBase.IsConnected())
                    dataBase.CloseConnection();
            }
        }

        private string _lastConnectionString = "";
        public virtual DatabaseStatus Database_SelfCheck(int connectTimeOutSeconds = 5)
        {
            lock (this)
            {
                var tro = Database_OpenConnection(connectTimeOutSeconds);
                if (tro.Item1 == false)
                {
                    SetStatus(false);
                    return DatabaseStatus.New(Status, tro.Item2);
                }

                var dataBase = GetDataBase();
                {
                    var initTablesResult = dataBase.InitTables();
                    if (initTablesResult.IsSuccess == false)
                    {
                        SetStatus(false);
                        SimpleLogHelper.DebugWarning(initTablesResult.ErrorInfo);
                        return DatabaseStatus.New(Status, initTablesResult.ErrorInfo);
                    }
                }

                IsWritable = dataBase.CheckWritable();

                if (!dataBase.GetConfig("EncryptionTest").IsSuccess)
                {
                    SetStatus(false);
                    return DatabaseStatus.New(Status, "read database test failed");
                }

                if (dataBase.CheckEncryptionTest() == false)
                {
                    Status = EnumDatabaseStatus.EncryptKeyError;
                    dataBase.CloseConnection();
                    return DatabaseStatus.New(Status);
                }

                SetStatus(true);
                dataBase.CloseConnection();
                return DatabaseStatus.New(Status);
            }
        }

        public Result Database_InsertServer(ProtocolBase server)
        {
            lock (this)
            {
                if (_isWritable)
                {
                    var tmp = (ProtocolBase)server.Clone();
                    tmp.SetNotifyPropertyChangedEnabled(false);
                    tmp.EncryptToDatabaseLevel();
                    var result = GetDataBase().AddServer(ref tmp);
                    if (result.IsSuccess)
                    {
                        SetReadTimestampTo0(TableServer.TABLE_NAME);
                        SetReadTimestampTo0(TableCredential.TABLE_NAME);
                        SetStatus(true);
                        server.Id = tmp.Id;
                    }
                    return result;
                }
                return Result.Success();
            }
        }

        public Result Database_InsertServer(List<ProtocolBase> servers)
        {
            lock (this)
            {
                if (_isWritable)
                {
                    var cloneList = new List<ProtocolBase>();
                    foreach (var server in servers)
                    {
                        var tmp = (ProtocolBase)server.Clone();
                        tmp.SetNotifyPropertyChangedEnabled(false);
                        tmp.EncryptToDatabaseLevel();
                        cloneList.Add(tmp);
                    }
                    var ret = GetDataBase().AddServer(cloneList);
                    if (ret.IsSuccess)
                    {
                        SetReadTimestampTo0(TableServer.TABLE_NAME);
                        SetReadTimestampTo0(TableCredential.TABLE_NAME);
                        SetStatus(true);
                    }
                    return ret;
                }
                return Result.Success();
            }
        }

        public Result Database_UpdateServer(ProtocolBase org)
        {
            return Database_UpdateServer([org]);
        }

        public Result Database_UpdateServer(IEnumerable<ProtocolBase> servers)
        {
            lock (this)
            {
                if (_isWritable)
                {
                    var serverArray = servers as ProtocolBase[] ?? servers.ToArray();
                    var cloneList = new List<ProtocolBase>(serverArray.Length);
                    foreach (var server in serverArray)
                    {
                        var tmp = (ProtocolBase)server.Clone();
                        tmp.SetNotifyPropertyChangedEnabled(false);
                        tmp.EncryptToDatabaseLevel();
                        cloneList.Add(tmp);
                    }
                    var ret = GetDataBase().UpdateServer(cloneList);
                    if (!ret.IsSuccess)
                    {
                        return ret;
                    }

                    foreach (var protocolServer in serverArray)
                    {
                        var old = CachedProtocols.FirstOrDefault(x => x.Id == protocolServer.Id);
                        if (old != null)
                        {
                            if (old.Server != protocolServer)
                            {
                                old.Server = protocolServer;
                            }
                        }
                        else
                        {
                            SetReadTimestampTo0(TableServer.TABLE_NAME);
                            break;
                        }
                    }
                    SetReadTimestamp(TableServer.TABLE_NAME);
                    SetStatus(true);
                    return ret;
                }
                return Result.Success();
            }
        }

        public Result Database_DeleteServer(IEnumerable<string> ids)
        {
            lock (this)
            {
                if (_isWritable)
                {
                    var enumerable = ids.ToArray();
                    var ret = GetDataBase().DeleteServer(enumerable);
                    if (ret.IsSuccess)
                    {
                        CachedProtocols.RemoveAll(x => enumerable.Contains(x.Id));
                        SetStatus(true);
                    }
                    return ret;
                }
                return Result.Fail(IoC.Translate("We can not delete from database:") + " readonly");
            }
        }

        private ResultSelects<ProtocolBase> Database_GetServers()
        {
            var ret = GetDataBase().GetServers();
            if (ret.IsSuccess)
            {
                foreach (var protocolBase in ret.Items)
                {
                    protocolBase.DataSource = this;
                }
                SetStatus(true);
            }
            return ret;
        }

        public Result Database_InsertCredential(Credential credential)
        {
            lock (this)
            {
                if (_isWritable)
                {
                    var tmp = (Credential)credential.Clone();
                    tmp.SetNotifyPropertyChangedEnabled(false);
                    tmp.EncryptToDatabaseLevel();
                    var result = GetDataBase().AddCredential(ref tmp);
                    if (result.IsSuccess)
                    {
                        credential.DatabaseId = tmp.DatabaseId;
                        credential.DataSource = this;
                        SetStatus(true);
                    }
                    return result;
                }
                return Result.Success();
            }
        }

        public Result Database_UpdateCredential(Credential org, string credentialNameBeforeUpdate)
        {
            if (_isWritable)
            {
                var servers = GetServers(true);
                var relatedProtocols = servers.Select(x => x.Server)
                    .Where(x => (x as ProtocolBaseWithAddressPortUserPwd)?.InheritedCredentialName == credentialNameBeforeUpdate)
                    .Select(x => (ProtocolBaseWithAddressPortUserPwd)x).ToList();

                lock (this)
                {
                    var tmp = (Credential)org.Clone();
                    tmp.SetNotifyPropertyChangedEnabled(false);
                    tmp.EncryptToDatabaseLevel();
                    var ret = GetDataBase().UpdateCredential(tmp, relatedProtocols);
                    if (ret.IsSuccess)
                    {
                        org.DataSource = this;
                        SetStatus(true);
                        if (relatedProtocols.Count > 0)
                        {
                            ClearReadTimestamp();
                            ret.NeedReloadUI = true;
                        }
                    }
                    return ret;
                }
            }
            return Result.Success();
        }

        public Result Database_DeleteCredential(IEnumerable<string> names)
        {
            if (_isWritable)
            {
                var enumerable = names.ToArray();
                var servers = GetServers(true);
                var relatedProtocols = servers.Select(x => x.Server)
                    .Where(x => enumerable.Any(name => name == (x as ProtocolBaseWithAddressPortUserPwd)?.InheritedCredentialName))
                    .Select(x => (ProtocolBaseWithAddressPortUserPwd)x).ToList();
                lock (this)
                {
                    var ret = GetDataBase().DeleteCredential(enumerable, relatedProtocols);
                    if (ret.IsSuccess)
                    {
                        CachedCredentials.RemoveAll(x => enumerable.Contains(x.Name));
                        if (relatedProtocols.Count > 0)
                        {
                            ClearReadTimestamp();
                            ret.NeedReloadUI = true;
                        }
                        SetStatus(true);
                    }
                    return ret;
                }
            }
            return Result.Fail(IoC.Translate("We can not delete from database:") + " readonly");
        }

        public IEnumerable<Credential> GetCredentials(bool force = false)
        {
            if (Status != EnumDatabaseStatus.OK || (force == false && !NeedRead(TableCredential.TABLE_NAME)))
            {
                return CachedCredentials;
            }
            lock (this)
            {
                var result = GetDataBase().GetCredentials();
                if (result.IsSuccess)
                {
                    SetReadTimestamp(TableCredential.TABLE_NAME);
                    foreach (var credential in result.Items)
                    {
                        credential.DataSource = this;
                    }
                    CachedCredentials = result.Items;
                    SetStatus(true);
                }
                return CachedCredentials;
            }
        }
    }
}
