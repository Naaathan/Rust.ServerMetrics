using Facepunch;
using HarmonyLib;
using Network;
using Newtonsoft.Json;
using RustServerMetrics.Config;
using RustServerMetrics.HarmonyPatches.Utility;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using UnityEngine;

namespace RustServerMetrics;

public class MetricsLogger : SingletonComponent<MetricsLogger>
{
    private const string ConfigurationPath = "HarmonyMods_Data/ServerMetrics/Configuration.json";
    private readonly Dictionary<ulong, Action> _playerStatsActions = new();
    private readonly Dictionary<ulong, uint> _perfReportDelayCounter = new();
    private readonly ThreadLocal<StringBuilder> _stringBuilder = new ThreadLocal<StringBuilder>(() => new StringBuilder(128));

    private class NetworkUpdateData
    {
        public int Count;
        
        public long Bytes;

        public NetworkUpdateData(int count, long bytes)
        {
            Count = count;
            Bytes = bytes;
        }
    }

    private class PacketProfile : Pool.IPooled
    {
        public string EntityType { get; set; }

        public ulong NetId { get; set; }

        public long Timestamp { get; set; }

        public void EnterPool() => Reset();

        public void LeavePool() => Reset();

        public void Reset()
        {
            EntityType = null;
            NetId = 0ul;
            Timestamp = 0L;
        }
    }

    private readonly ConcurrentQueue<PacketProfile> _packetProfiles = new();

    private readonly Dictionary<Message.Type, NetworkUpdateData> _networkUpdates = Enum.GetValues(typeof(Message.Type))
                                                                                       .Cast<Message.Type>()
                                                                                       .Distinct()
                                                                                       .ToDictionary(x => x, 
                                                                                                     _ => new NetworkUpdateData(0, 0));
    
    private static readonly IReadOnlyDictionary<Message.Type, string> MessageTypeNames = Enum.GetValues(typeof(Message.Type))
                                                                                             .Cast<Message.Type>()
                                                                                             .Distinct()
                                                                                             .ToDictionary(x => x, 
                                                                                                           x => x.ToString());

    public readonly MetricsTimeStorage<MethodInfo> ServerInvokes = new("invoke_execution", LogMethodInfo);
    public readonly MetricsTimeStorage<string> ServerRpcCalls = new("rpc_calls", LogMethodName);
    public readonly MetricsTimeStorage<string> WorkQueueTimes = new("work_queue", LogMethodName);
    public readonly MetricsTimeStorage<string> ServerUpdate = new("server_update", LogMethodName);
    public readonly MetricsTimeStorage<string> TimeWarnings = new("timewarnings", LogMethodName);
    public readonly MetricsTimeStorage<string> ServerConsoleCommands = new("console_commands", (builder, command) =>
    {
        builder.Append(",command=\"");
        builder.Append(command);
    });

    public static bool IsReady;
    public bool Ready { get => IsReady; private set => IsReady = value; }
    internal ConfigData Configuration { get; private set; }

    private Uri _baseUri;
    private readonly int _performanceReportRequestId = UnityEngine.Random.Range(-2147483648, 2147483647);
    private ReportUploader _reportUploader;
    private bool _firstReportGenerated;
    private int _lastFrameID;
    private System.Diagnostics.Process _currentProcess;

    public Uri BaseUri
    {
        get
        {
            if (_baseUri != null)
            {
                return _baseUri;
            }

            _baseUri = new Uri(new Uri(Configuration.DatabaseUrl), 
                               $"/write?db={Configuration.DatabaseName}&precision=ms&u={Configuration.DatabaseUser}&p={Configuration.DatabasePassword}");
            return _baseUri;
        }
    }

    #region Initialization

    internal static void Initialize()
    {
        new GameObject().AddComponent<MetricsLogger>();
    }

    internal void OnServerStarted()
    {
        RustServerMetricsLoader.__serverStarted = true;
            
        Debug.Log($"[ServerMetrics]: Applying Startup Patches");
        var assembly = GetType().Assembly;

        var harmonyInstance = HarmonyLoader.loadedMods.FirstOrDefault(x => x.Assembly == assembly)?.Harmony.harmonyObject;
        if (harmonyInstance == null)
        {
            RustServerMetricsLoader.__harmonyInstance ??= new Harmony("RustServerMetrics" + "PATCH");
            harmonyInstance = RustServerMetricsLoader.__harmonyInstance;
        }

        var nestedTypes = assembly.GetTypes();
        foreach (var nestedType in nestedTypes)
        {
            if (nestedType.GetCustomAttribute<DelayedHarmonyPatchAttribute>(false) == null) continue;
                
            var patchProcessor = new PatchClassProcessor((Harmony)harmonyInstance, nestedType);
            Debug.Log(patchProcessor.Patch() == null ? $"[ServerMetrics]: Failed to apply patch: {nestedType.Name}" : $"[ServerMetrics]: Applied Startup Patch: {nestedType.Name}");
        }
    }

    public override void Awake()
    {
        base.Awake();
        _reportUploader = gameObject.AddComponent<ReportUploader>();
        RegisterCommands();

        LoadConfiguration();
        if (!ValidateConfiguration())
        {
            return;
        }
            
        if (!Configuration.Enabled)
        {
            Debug.LogWarning("[ServerMetrics]: Metrics gathering has been disabled in the configuration");
            return;
        }

        Pool.ResizeBuffer<PacketProfile>(10000);
        Pool.FillBuffer<PacketProfile>();

        StartLoggingMetrics();
        Ready = true;
    }

    public void StartLoggingMetrics()
    {
        InvokeRepeating(CheckBufferQueues, UnityEngine.Random.Range(0f, 1f), 1f);
        InvokeRepeating(LogPacketsProfiled, UnityEngine.Random.Range(0f, 1f), 1f);
        InvokeRepeating(LogNetworkUpdates, UnityEngine.Random.Range(0.25f, 0.75f), 0.5f);

        InvokeRepeating(ServerInvokes.SerializeToStringBuilder, UnityEngine.Random.Range(0f, 1f), 1f);
        InvokeRepeating(ServerRpcCalls.SerializeToStringBuilder, UnityEngine.Random.Range(0f, 1f), 1f);
        InvokeRepeating(ServerConsoleCommands.SerializeToStringBuilder, UnityEngine.Random.Range(0f, 1f), 1f);
        InvokeRepeating(WorkQueueTimes.SerializeToStringBuilder, UnityEngine.Random.Range(0f, 1f), 1f);
        InvokeRepeating(ServerUpdate.SerializeToStringBuilder, UnityEngine.Random.Range(0f, 1f), 1f);
        InvokeRepeating(TimeWarnings.SerializeToStringBuilder, UnityEngine.Random.Range(0f, 1f), 1f);
    }

    #endregion

    internal void CheckBufferQueues()
    {
        var queueLengths = (Net.sv.ReadQueueLength, Net.sv.WriteQueueLength);

        UploadPacket("network_buffers", queueLengths, (builder, lengths) =>
        {
            builder.Append(",read_queue_length=");
            builder.Append(lengths.ReadQueueLength);
            builder.Append("i write_queue_length=");
            builder.Append(lengths.WriteQueueLength);
            builder.Append("i");
        });
    }

    internal void OnPlayerInit(BasePlayer player)
    {
        if (!Ready) return;
        if (!Configuration.GatherPlayerMetrics) return;
        var action = new Action(() => GatherPlayerSecondStats(player));
        if (_playerStatsActions.TryGetValue(player.userID, out var existingAction))
            player.CancelInvoke(existingAction);
        _playerStatsActions[player.userID] = action;
        player.InvokeRepeating(action, UnityEngine.Random.Range(0.5f, 1.5f), 1f);
    }

    internal void OnPlayerDisconnected(BasePlayer player)
    {
        if (!Ready) return;
        if (!Configuration.GatherPlayerMetrics) return;
        if (_playerStatsActions.TryGetValue(player.userID, out var action))
            player.CancelInvoke(action);
        _playerStatsActions.Remove(player.userID);
        _perfReportDelayCounter.Remove(player.userID);
    }

    internal void OnNetWriteSend(NetWrite write, SendInfo sendInfo, int type)
    {
        if (!Ready)
        {
            return;
        }

        var data = _networkUpdates[(Message.Type)type];
        if (sendInfo.connection != null)
        {
            Interlocked.Add(ref data.Count, 1);
            Interlocked.Add(ref data.Bytes, write.Length);
        }
        else if (sendInfo.connections != null)
        {
            var count = sendInfo.connections.Count;
            Interlocked.Add(ref data.Count, count);
            Interlocked.Add(ref data.Bytes, write.Length * count);
        }
    }

    internal void OnPacketProfilerLogDetailed(NetworkableId entityId)
    {
        if (!Ready) return;
        var net = BaseNetworkable.serverEntities.Find(entityId);
        if (!net.IsValid()) return;
        string entityType = net.GetType()?.Name;
        if (string.IsNullOrEmpty(entityType)) return;

        var packetProfile = Pool.Get<PacketProfile>();
        packetProfile.EntityType = entityType;
        packetProfile.NetId = net.net.ID.Value;
        packetProfile.Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        _packetProfiles.Enqueue(packetProfile);
    }

    internal void OnOxidePluginMetrics(Dictionary<string, double> metrics)
    {
        if (!Ready) return;
        if (metrics.Count < 1) return;

        foreach (var metric in metrics)
        {
            UploadPacket("oxide_plugins", metric, (builder, report) =>
            {
                builder.Append(",plugin=\"");
                AppendPluginNameSanitized(builder, report.Key);
                builder.Append("\" hookTime=");
                builder.Append(report.Value);
            });
        }
    }

    internal bool OnClientPerformanceReport(ProtoBuf.PerformanceReport clientPerformanceReport)
    {
        if (clientPerformanceReport.request_id != _performanceReportRequestId)
        {
            return false;
        }

        UploadPacket("client_performance", clientPerformanceReport, (builder, report) =>
        {
            builder.Append(",steamid=");
            builder.Append(report.user_id);
            builder.Append(" memory=");
            builder.Append(report.memory_system);
            builder.Append("i,fps=");
            builder.Append(report.fps);
        });

        return true;
    }

    private void GatherPlayerSecondStats(BasePlayer player)
    {
        if (!player.IsReceivingSnapshot)
        {
            _perfReportDelayCounter.TryGetValue(player.userID, out var perfReportCounter);
            if (perfReportCounter < 4)
            {
                _perfReportDelayCounter[player.userID] = perfReportCounter + 1;
            }
            else
            {
                _perfReportDelayCounter[player.userID] = 0;
                player.ClientRPCPlayer(null, player, "GetPerformanceReport", "legacy", _performanceReportRequestId);
            }
        }

        UploadPacket("connection_latency", player, (builder, basePlayer) =>
        {
            var ip = basePlayer.net.connection.ipaddress;

            builder.Append(",steamid=");
            builder.Append(basePlayer.UserIDString);
            builder.Append(",ip=");
            builder.Append(ip[..ip.LastIndexOf(':')]);
            builder.Append(" ping=");
            builder.Append(Net.sv.GetAveragePing(basePlayer.net.connection));
            builder.Append("i,packet_loss=");
            builder.Append(Net.sv.GetStat(basePlayer.net.connection, BaseNetwork.StatTypeLong.PacketLossLastSecond));
            builder.Append("i ");
        });
    }

    private void LogPacketsProfiled()
    {
        if (_packetProfiles.Count < 1) return;
        var serverTag = Configuration.ServerTag;
        var stringBuilder = _stringBuilder.Value;

        int max = 1000;

        while (--max >= 0 && _packetProfiles.TryDequeue(out var packet))
        {
            try
            {
                stringBuilder.Clear();
                stringBuilder.Append("packet_profiler,server=");
                stringBuilder.Append(serverTag);
                stringBuilder.Append(" entity_type=\"");
                stringBuilder.Append(packet.EntityType);
                stringBuilder.Append("\",entity_id=");
                stringBuilder.Append(packet.NetId);
                stringBuilder.Append("i ");
                stringBuilder.Append(packet.Timestamp);

                string payload = stringBuilder.ToString();
                _reportUploader.AddToSendBuffer(payload);
            }
            finally
            {
                Pool.Free(ref packet);
            }
        }
    }

    private void LogNetworkUpdates()
    {
        if (_networkUpdates.Count < 1) return;
        var serverTag = Configuration.ServerTag;
        var epochNow = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var stringBuilder = _stringBuilder.Value;

        stringBuilder.Clear();
        stringBuilder.Append("network_updates,server=");
        stringBuilder.Append(serverTag);
        stringBuilder.Append(" ");

        var enumerator = _networkUpdates.GetEnumerator();
        if (enumerator.MoveNext())
        {
            var networkUpdate = enumerator.Current;
            var key = MessageTypeNames[networkUpdate.Key];
            var value = networkUpdate.Value;
            // Count first named {type}
            stringBuilder.Append(key);
            stringBuilder.Append("=");
            stringBuilder.Append(Interlocked.Exchange(ref value.Count, 0));
            stringBuilder.Append("i");

            // Bytes second named as "{type}_bytes"
            stringBuilder.Append(",");
            stringBuilder.Append(key);
            stringBuilder.Append("_bytes");
            stringBuilder.Append("=");
            stringBuilder.Append(Interlocked.Exchange(ref value.Bytes, 0));
            stringBuilder.Append("i");

            while (enumerator.MoveNext())
            {
                networkUpdate = enumerator.Current;
                key = MessageTypeNames[networkUpdate.Key];
                value = networkUpdate.Value;

                // Count first named {type}
                stringBuilder.Append(",");
                stringBuilder.Append(key);
                stringBuilder.Append("=");
                stringBuilder.Append(Interlocked.Exchange(ref value.Count, 0));
                stringBuilder.Append("i");

                // Bytes second named as "{type}_bytes"
                stringBuilder.Append(",");
                stringBuilder.Append(key);
                stringBuilder.Append("_bytes");
                stringBuilder.Append("=");
                stringBuilder.Append(Interlocked.Exchange(ref value.Bytes, 0));
                stringBuilder.Append("i");
            }
        }

        stringBuilder.Append(" ");
        stringBuilder.Append(epochNow);

        string payload = stringBuilder.ToString();
        _reportUploader.AddToSendBuffer(payload);
    }

    internal void OnPerformanceReportGenerated()
    {
        if (!Ready) return;
        if (!_firstReportGenerated)
        {
            _firstReportGenerated = true;
            return;
        }
        var current = Performance.current;

        if (current.frameID == _lastFrameID) return;
        _lastFrameID = current.frameID;

        var serverTag = Configuration.ServerTag;
        var epochNow = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();

        LogPerformanceReport(current, epochNow, serverTag);
    }

    private void LogPerformanceReport(Performance.Tick current, string epochNow, string serverTag)
    {
        var stringBuilder = _stringBuilder.Value;

        stringBuilder.Clear();
        stringBuilder.Append("framerate,server=");
        stringBuilder.Append(serverTag);
        stringBuilder.Append(" instant=");
        stringBuilder.Append(current.frameRate);
        stringBuilder.Append(",average=");
        stringBuilder.Append(current.frameRateAverage);
        stringBuilder.Append(" ");
        stringBuilder.Append(epochNow);
        stringBuilder.Append("\n");

        stringBuilder.Append("frametime,server=");
        stringBuilder.Append(serverTag);
        stringBuilder.Append(" instant=");
        stringBuilder.Append(current.frameTime);
        stringBuilder.Append(",average=");
        stringBuilder.Append(current.frameTimeAverage);
        stringBuilder.Append(" ");
        stringBuilder.Append(epochNow);
        stringBuilder.Append("\n");

        stringBuilder.Append("memory,server=");
        stringBuilder.Append(serverTag);
        stringBuilder.Append(" used=");
        stringBuilder.Append(GetMemoryUsage(current));
        stringBuilder.Append("i,collections=");
        stringBuilder.Append(current.memoryCollections);
        stringBuilder.Append("i,allocations=");
        stringBuilder.Append(current.memoryAllocations);
        stringBuilder.Append("i,gc=");
        stringBuilder.Append(current.gcTriggered);
        stringBuilder.Append(" ");
        stringBuilder.Append(epochNow);
        stringBuilder.Append("\n");

        stringBuilder.Append("tasks,server=");
        stringBuilder.Append(serverTag);
        stringBuilder.Append(" load_balancer=");
        stringBuilder.Append(current.loadBalancerTasks);
        stringBuilder.Append("i,invoke_handler=");
        stringBuilder.Append(current.invokeHandlerTasks);
        stringBuilder.Append("i,workshop_skins_queue=");
        stringBuilder.Append(current.workshopSkinsQueued);
        stringBuilder.Append("i ");
        stringBuilder.Append(epochNow);
        stringBuilder.Append("\n");

        var bytesReceivedLastSecond = Net.sv.GetStat(null, BaseNetwork.StatTypeLong.BytesReceived_LastSecond);
        var bytesSentLastSecond = Net.sv.GetStat(null, BaseNetwork.StatTypeLong.BytesSent_LastSecond);
        var packetLossLastSecond = Net.sv.GetStat(null, BaseNetwork.StatTypeLong.PacketLossLastSecond);

        stringBuilder.Append("network,server=");
        stringBuilder.Append(serverTag);
        stringBuilder.Append(" bytes_received=");
        stringBuilder.Append(bytesReceivedLastSecond);
        stringBuilder.Append("i,bytes_sent=");
        stringBuilder.Append(bytesSentLastSecond);
        stringBuilder.Append("i,packet_loss=");
        stringBuilder.Append(packetLossLastSecond);
        stringBuilder.Append("i ");
        stringBuilder.Append(epochNow);
        stringBuilder.Append("\n");

        stringBuilder.Append("players,server=");
        stringBuilder.Append(serverTag);
        stringBuilder.Append(" count=");
        stringBuilder.Append(BasePlayer.activePlayerList.Count);
        stringBuilder.Append("i,joining=");
        stringBuilder.Append(ServerMgr.Instance.connectionQueue.Joining);
        stringBuilder.Append("i,queued=");
        stringBuilder.Append(ServerMgr.Instance.connectionQueue.Queued);
        stringBuilder.Append("i ");
        stringBuilder.Append(epochNow);
        stringBuilder.Append("\n");

        stringBuilder.Append("entities,server=");
        stringBuilder.Append(serverTag);
        stringBuilder.Append(" count=");
        stringBuilder.Append(BaseNetworkable.serverEntities.Count);
        stringBuilder.Append("i ");
        stringBuilder.Append(epochNow);

        string payload = stringBuilder.ToString();
        _reportUploader.AddToSendBuffer(payload);
    }


    #region Helpers

    public void UploadPacket<T>(string id, T data, Action<StringBuilder, T> serializer)
    {
        var serverTag = Configuration.ServerTag;
        var epochNow = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var stringBuilder = _stringBuilder.Value;

        stringBuilder.Clear();
        stringBuilder.Append(id);
        stringBuilder.Append(",server=");
        stringBuilder.Append(serverTag);

        serializer.Invoke(stringBuilder, data);

        stringBuilder.Append(" ");
        stringBuilder.Append(epochNow);

        string payload = stringBuilder.ToString();
        AddToSendBuffer(payload);
    }

    public void AddToSendBuffer(string toString) => _reportUploader.AddToSendBuffer(toString);

    private long GetMemoryUsage(Performance.Tick performanceTick)
    {
        if (performanceTick.memoryUsageSystem > 0)
            return performanceTick.memoryUsageSystem;

        _currentProcess ??= System.Diagnostics.Process.GetCurrentProcess();

        _currentProcess.Refresh();
        return _currentProcess.WorkingSet64 / 1024 / 1024;
    }

    private static void AppendPluginNameSanitized(StringBuilder builder, string name)
    {
        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c))
                builder.Append(c);
        }
    }

    private static void LogMethodInfo(StringBuilder builder, MethodInfo info)
    {
        builder.Append(",behaviour=\"");
        builder.Append(info.DeclaringType?.Name);
        builder.Append("\",method=\"");
        builder.Append(info.Name);
    }

    private static void LogMethodName(StringBuilder builder, string info)
    {
        builder.Append(",behaviour=\"");

        var start = 0;
        var dot = info.IndexOf('.');
        while (dot >= 0)
        {
            builder.Append(info, start, dot - start);
            builder.Append("\",method=\"");
            start = dot + 1;
            dot = info.IndexOf('.', start);
        }
        builder.Append(info, start, info.Length - start);
    }
    #endregion
        
    #region Commands

    private void RegisterCommands()
    {
        const string commandPrefix = "servermetrics";
        var reloadCfgCommand = new ConsoleSystem.Command()
        {
            Name = "reloadcfg",
            Parent = commandPrefix,
            FullName = commandPrefix + "." + "reloadcfg",
            ServerAdmin = true,
            Variable = false,
            Call = ReloadCfgCommand
        };

        var statusCommand = new ConsoleSystem.Command()
        {
            Name = "status",
            Parent = commandPrefix,
            FullName = commandPrefix + "." + "status",
            ServerAdmin = true,
            Variable = false,
            Call = StatusCommand
        };

        ConsoleSystem.Index.Server.Dict[commandPrefix + "." + "reloadcfg"] = reloadCfgCommand;
        ConsoleSystem.Index.Server.Dict[commandPrefix + "." + "status"] = statusCommand;

        // Would be nice if this had a public setter, or better yet, a register command helper
        // update: now it does
        ConsoleSystem.Index.All = ConsoleSystem.Index.All.Concat(new[] { reloadCfgCommand, statusCommand }).ToArray();
    }

    private void StatusCommand(ConsoleSystem.Arg arg)
    {
        var stringBuilder = Pool.Get<StringBuilder>();

        try
        {
            stringBuilder.AppendLine("[ServerMetrics]: Status");
            stringBuilder.AppendLine("Overview");
            stringBuilder.Append("\tReady: "); stringBuilder.Append(Ready); stringBuilder.AppendLine();
            stringBuilder.AppendLine("Report Uploader:");
            stringBuilder.Append("\tRunning: "); stringBuilder.Append(_reportUploader.IsRunning); stringBuilder.AppendLine();
            stringBuilder.Append("\tIn Buffer: "); stringBuilder.Append(_reportUploader.BufferSize); stringBuilder.AppendLine();

            arg.ReplyWith(stringBuilder.ToString());
        }
        finally
        {
            Pool.FreeUnmanaged(ref stringBuilder);
        }
    }

    private void ReloadCfgCommand(ConsoleSystem.Arg arg)
    {
        LoadConfiguration();
        if (!ValidateConfiguration() || Configuration.Enabled == false)
        {
            Ready = false;

            // why is there no cancel all invokes method ...
            var list = new List<InvokeAction>();
            InvokeHandler.FindInvokes(this, list);
            foreach (var invoke in list)
            {
                CancelInvoke(invoke.action);
            }

            foreach (var player in _playerStatsActions)
            {
                var basePlayer = BasePlayer.FindByID(player.Key);
                if (basePlayer == null) continue;
                basePlayer.CancelInvoke(player.Value);
            }
            _reportUploader.Stop();

            if (!Configuration.Enabled)
            {
                arg.ReplyWith("[ServerMetrics]: Metrics gathering has been disabled in the configuration");
                return;
            }
        }
        else if (!Ready)
        {
            Ready = true;
            foreach (var player in BasePlayer.activePlayerList)
            {
                OnPlayerInit(player);
            }

            StartLoggingMetrics();
        }
        arg.ReplyWith("[ServerMetrics]: Configuration reloaded");
    }

    #endregion
        
    #region Configuration

    private bool ValidateConfiguration()
    {
        if (Configuration == null) return false;

        var valid = true;
        if (Configuration.DatabaseUrl == ConfigData.DefaultInfluxDbUrl)
        {
            Debug.LogError("[ServerMetrics]: Default database url detected in configuration, loading aborted");
            valid = false;
        }

        if (Configuration.DatabaseName == ConfigData.DefaultInfluxDBName)
        {
            Debug.LogError("[ServerMetrics]: Default database name detected in configuration, loading aborted");
            valid = false;
        }

        if (Configuration.ServerTag == ConfigData.DefaultServerTag)
        {
            Debug.LogError("[ServerMetrics]: Default server tag detected in configuration, loading aborted");
            valid = false;
        }

        return valid;
    }

    private void LoadConfiguration()
    {
        try
        {
            var configStr = File.ReadAllText(ConfigurationPath);
            Configuration = JsonConvert.DeserializeObject<ConfigData>(configStr) ?? new ConfigData();
            var uri = new Uri(Configuration.DatabaseUrl);
            _baseUri = new Uri(uri, $"/write?db={Configuration.DatabaseName}&precision=ms&u={Configuration.DatabaseUser}&p={Configuration.DatabasePassword}");
        }
        catch
        {
            Debug.LogError("[ServerMetrics]: The configuration seems to be missing or malformed. Defaults will be loaded.");
            Configuration = new ConfigData();

            if (File.Exists(ConfigurationPath))
            {
                return;
            }
        }
        SaveConfiguration();
    }

    private void SaveConfiguration()
    {
        try
        {
            var configFileInfo = new FileInfo(ConfigurationPath);
            if (configFileInfo.Directory is { Exists: false })
            {
                configFileInfo.Directory.Create();
            }
                
            var serializedConfiguration = JsonConvert.SerializeObject(Configuration, Formatting.Indented);
            File.WriteAllText(ConfigurationPath, serializedConfiguration);
        }
        catch (Exception ex)
        {
            Debug.LogError("[ServerMetrics]: Failed to write configuration file");
            Debug.LogException(ex);
        }
    }

    #endregion
}