using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using _1RM.Model;
using _1RM.Model.Protocol.Base;
using _1RM.Service.DataSource.DAO;
using _1RM.Service.DataSource.Model;
using _1RM.Utils.Tracing;
using _1RM.View;
using Newtonsoft.Json;
using Shawn.Utils;
using Shawn.Utils.Wpf.FileSystem;

namespace _1RM.Service.DataSource
{
    public class DataSourceService : NotifyPropertyChangedBase
    {
        public const int CHECK_UPDATE_PERIOD = 5;
        public const string LOCAL_DATA_SOURCE_NAME = "Local";

        public DataSourceService()
        {
        }

        public SqliteSource? LocalDataSource { get; private set; } = null;

        public readonly ConcurrentDictionary<string, DataSourceBase> AdditionalSources = new ConcurrentDictionary<string, DataSourceBase>();
        public List<DataSourceBase> AllSources
        {
            get
            {
                var ret = new List<DataSourceBase>(AdditionalSources.Count + 1);
                if (LocalDataSource != null)
                    ret.Add(LocalDataSource);
                ret.AddRange(AdditionalSources.Values);
                return ret;
            }
        }

        public List<ProtocolBaseViewModel> GetServers(bool force)
        {
            lock (this)
            {
                var ret = new List<ProtocolBaseViewModel>(100);
                if (LocalDataSource != null)
                    ret.AddRange(LocalDataSource.GetServers(force));
                foreach (var dataSource in AdditionalSources)
                {
                    try
                    {
                        var pbs = dataSource.Value.GetServers(force);
                        foreach (var pb in pbs)
                        {
                            pb.DataSourceNameForLauncher = AdditionalSources.Any() ? pb.DataSourceName : "";
                        }
                        ret.AddRange(pbs);
                    }
                    catch (Exception e)
                    {
                        SimpleLogHelper.DebugWarning(e);
                    }
                }
                return ret;
            }
        }

        public List<Credential> GetCredentials(bool force)
        {
            lock (this)
            {
                var ret = new List<Credential>(100);
                if (LocalDataSource != null)
                    ret.AddRange(LocalDataSource.GetCredentials(force));
                foreach (var dataSource in AdditionalSources)
                {
                    try
                    {
                        var cs = dataSource.Value.GetCredentials(force);
                        ret.AddRange(cs);
                    }
                    catch (Exception e)
                    {
                        SimpleLogHelper.DebugWarning(e);
                    }
                }
                return ret;
            }
        }

        public List<(DataSourceBase, Credential)> GetSourceCredentials(bool force)
        {
            lock (this)
            {
                var ret = new List<(DataSourceBase, Credential)>(100);
                if (LocalDataSource != null)
                {
                    ret.AddRange(LocalDataSource.GetCredentials().Select(credential => (LocalDataSource, credential)).Select(dummy => ((DataSourceBase, Credential))dummy));
                }
                foreach (var dataSource in AdditionalSources)
                {
                    try
                    {
                        var cs = dataSource.Value.GetCredentials(force);
                        ret.AddRange(cs.Select(credential => (dataSource.Value, credential)).Select(dummy => ((DataSourceBase, Credential))dummy));
                    }
                    catch (Exception e)
                    {
                        SimpleLogHelper.DebugWarning(e);
                    }
                }
                return ret;
            }
        }

        public DataSourceBase? GetDataSource(string sourceId = LOCAL_DATA_SOURCE_NAME)
        {
            if (string.IsNullOrEmpty(sourceId) || sourceId == LOCAL_DATA_SOURCE_NAME)
                return LocalDataSource;
            if (AdditionalSources.ContainsKey(sourceId))
                return AdditionalSources[sourceId];
            return null;
        }

        public DatabaseStatus InitLocalDataSource(SqliteSource sqliteConfig)
        {
            LocalDataSource?.Database_CloseConnection();
            sqliteConfig.DataSourceName = LOCAL_DATA_SOURCE_NAME;
            sqliteConfig.ClearReadTimestamp();
            if (!IoPermissionHelper.HasWritePermissionOnFile(sqliteConfig.Path))
            {
                LocalDataSource = null;
                return DatabaseStatus.New(EnumDatabaseStatus.AccessDenied, $"write permission denied for `{sqliteConfig.Path}`");
            }
            var ret = sqliteConfig.Database_SelfCheck();
            LocalDataSource = sqliteConfig;
            RaisePropertyChanged(nameof(LocalDataSource));
            return ret;
        }

        public DatabaseStatus AddOrUpdateDataSource(DataSourceBase newDataSource, int connectTimeOutSeconds = 10, bool doReload = true)
        {
            var globalData = connectTimeOutSeconds > 0 ? IoC.TryGet<GlobalData>() : null;
            globalData?.StopTick();
            try
            {
                newDataSource.ClearReadTimestamp();
                if (newDataSource is SqliteSource { DataSourceName: LOCAL_DATA_SOURCE_NAME } localConfig)
                {
                    return InitLocalDataSource(localConfig);
                }

                {
                    var olds = AdditionalSources.Where(x => x.Value == newDataSource).ToArray();
                    foreach (var pair in olds)
                    {
                        AdditionalSources.TryRemove(pair.Key, out var tmp);
                        tmp?.Database_CloseConnection();
                    }
                    {
                        AdditionalSources.TryRemove(newDataSource.DataSourceName, out var tmp);
                        tmp?.Database_CloseConnection();
                    }
                }

                newDataSource.Database_CloseConnection();
                var ret = DatabaseStatus.New(EnumDatabaseStatus.NotConnectedYet);
                if (connectTimeOutSeconds > 0)
                    ret = newDataSource.Database_SelfCheck(connectTimeOutSeconds);
                AdditionalSources.AddOrUpdate(newDataSource.DataSourceName, newDataSource, (name, source) => newDataSource);
                return ret;
            }
            catch (Exception e)
            {
                SimpleLogHelper.Warning(e);
                UnifyTracing.Error(e);
                return DatabaseStatus.New(EnumDatabaseStatus.OtherError, e.Message);
            }
            finally
            {
                if (doReload)
                    IoC.TryGet<GlobalData>()?.ReloadAll();
                globalData?.StartTick();
            }
        }

        public void RemoveDataSource(string name)
        {
            if (name == LOCAL_DATA_SOURCE_NAME)
                return;
            if (AdditionalSources.ContainsKey(name))
            {
                AdditionalSources[name].Database_CloseConnection();
                if (AdditionalSources.TryRemove(name, out _))
                {
                    IoC.Get<GlobalData>().ReloadAll(true);
                }
            }
        }

        public static List<DataSourceBase> AdditionalSourcesLoadFromProfile(string path)
        {
            var ads = new List<DataSourceBase>();
            if (File.Exists(path))
            {
                var tmp = JsonConvert.DeserializeObject<List<DataSourceBase>>(File.ReadAllText(AppPathHelper.Instance.ProfileAdditionalDataSourceJsonPath));
                if (tmp != null)
                    ads = tmp;
            }
            return ads;
        }

        public static void AdditionalSourcesSaveToProfile(string path, List<DataSourceBase> sources)
        {
            if (sources.Count == 0)
            {
                var fi = new FileInfo(path);
                if (fi.Exists)
                    fi.Delete();
            }
            else
            {
                var fi = new FileInfo(path);
                try
                {
                    if (fi?.Directory?.Exists == false)
                        fi.Directory.Create();
                }
                catch (Exception e)
                {
                    SimpleLogHelper.Error(e);
                    return;
                }
                if (IoPermissionHelper.HasWritePermissionOnFile(path))
                {
                    RetryHelper.Try(() =>
                    {
                        File.WriteAllText(path, JsonConvert.SerializeObject(sources, Formatting.Indented), Encoding.UTF8);
                    }, actionOnError: exception => UnifyTracing.Error(exception));
                }
            }
        }
    }
}
