﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using ServiceStack.Logging;

namespace ServiceStack.Redis
{
    public class RedisSentinelResolver : IRedisResolver, IRedisResolverExtended
    {
        static ILog log = LogManager.GetLogger(typeof(RedisResolver));

        public Func<RedisEndpoint, RedisClient> ClientFactory { get; set; }

        public int ReadWriteHostsCount { get; private set; }
        public int ReadOnlyHostsCount { get; private set; }

        HashSet<RedisEndpoint> allHosts = new HashSet<RedisEndpoint>();

        private RedisSentinel sentinel;

        private RedisEndpoint[] masters;
        private RedisEndpoint[] slaves;

        public RedisEndpoint[] Masters
        {
            get { return masters; }
        }
        public RedisEndpoint[] Slaves
        {
            get { return slaves; }
        }

        public RedisSentinelResolver(RedisSentinel sentinel)
            : this(sentinel, new RedisEndpoint[0], new RedisEndpoint[0]) { }

        public RedisSentinelResolver(RedisSentinel sentinel, IEnumerable<string> masters, IEnumerable<string> slaves)
            : this(sentinel, masters.ToRedisEndPoints(), slaves.ToRedisEndPoints()) { }

        public RedisSentinelResolver(RedisSentinel sentinel, IEnumerable<RedisEndpoint> masters, IEnumerable<RedisEndpoint> slaves)
        {
            this.sentinel = sentinel;
            ResetMasters(masters.ToList());
            ResetSlaves(slaves.ToList());
            ClientFactory = RedisConfig.ClientFactory;
        }

        public virtual void ResetMasters(IEnumerable<string> hosts)
        {
            ResetMasters(hosts.ToRedisEndPoints());
        }

        public virtual void ResetMasters(List<RedisEndpoint> newMasters)
        {
            if (newMasters == null || newMasters.Count == 0)
                throw new Exception("Must provide at least 1 master");

            masters = newMasters.ToArray();
            ReadWriteHostsCount = masters.Length;
            newMasters.Each(x => allHosts.Add(x));

            if (log.IsDebugEnabled)
                log.Debug("New Redis Masters: " + string.Join(", ", masters.Map(x => x.GetHostString())));
        }

        public virtual void ResetSlaves(IEnumerable<string> hosts)
        {
            ResetSlaves(hosts.ToRedisEndPoints());
        }

        public virtual void ResetSlaves(List<RedisEndpoint> newSlaves)
        {
            slaves = (newSlaves ?? new List<RedisEndpoint>()).ToArray();
            ReadOnlyHostsCount = slaves.Length;
            newSlaves.Each(x => allHosts.Add(x));

            if (log.IsDebugEnabled)
                log.Debug("New Redis Slaves: " + string.Join(", ", slaves.Map(x => x.GetHostString())));
        }

        public RedisEndpoint GetReadWriteHost(int desiredIndex)
        {
            return sentinel.GetMaster() ?? masters[desiredIndex % masters.Length];
        }

        public RedisEndpoint GetReadOnlyHost(int desiredIndex)
        {
            var slavesEndpoints = sentinel.GetSlaves();
            if (slavesEndpoints.Count > 0)
                return slavesEndpoints[desiredIndex % slavesEndpoints.Count];

            return ReadOnlyHostsCount > 0
                ? slaves[desiredIndex % slaves.Length]
                : GetReadWriteHost(desiredIndex);
        }

        public RedisClient CreateMasterClient(int desiredIndex)
        {
            return CreateRedisClient(GetReadWriteHost(desiredIndex), master: true);
        }

        public RedisClient CreateSlaveClient(int desiredIndex)
        {
            return CreateRedisClient(GetReadOnlyHost(desiredIndex), master: false);
        }

        object oLock = new object();
        private string lastInvalidMasterHost = null;
        private long lastValidMasterTicks = DateTime.UtcNow.Ticks;
 
        private DateTime lastValidMasterFromSentinelAt
        {
            get { return new DateTime(Interlocked.Read(ref lastValidMasterTicks), DateTimeKind.Utc); }
            set { Interlocked.Exchange(ref lastValidMasterTicks, value.Ticks); }
        }

        public virtual RedisClient CreateRedisClient(RedisEndpoint config, bool master)
        {
            var client = ClientFactory(config);
            if (master)
            {
                var role = RedisServerRole.Unknown;
                try
                {
                    role = client.GetServerRole();
                    if (role == RedisServerRole.Master)
                    {
                        lastValidMasterFromSentinelAt = DateTime.UtcNow;
                        return client;
                    }
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref RedisState.TotalInvalidMasters);

                    if (client.GetHostString() == lastInvalidMasterHost)
                    {
                        lock (oLock)
                        {
                            if (DateTime.UtcNow - lastValidMasterFromSentinelAt > sentinel.WaitBeforeForcingMasterFailover)
                            {
                                lastInvalidMasterHost = null;
                                lastValidMasterFromSentinelAt = DateTime.UtcNow;

                                log.Error("Valid master was not found at '{0}' within '{1}'. Sending SENTINEL failover...".Fmt(
                                    client.GetHostString(), sentinel.WaitBeforeForcingMasterFailover), ex);

                                Interlocked.Increment(ref RedisState.TotalForcedMasterFailovers);

                                sentinel.ForceMasterFailover();
                                Thread.Sleep(sentinel.WaitBetweenSentinelLookups);
                                role = client.GetServerRole();
                            }
                        }
                    }
                    else
                    {
                        lastInvalidMasterHost = client.GetHostString();
                    }
                }

                if (role != RedisServerRole.Master)
                {
                    try
                    {
                        var stopwatch = Stopwatch.StartNew();
                        while (true)
                        {
                            try
                            {
                                var masterConfig = sentinel.GetMaster();
                                var masterClient = ClientFactory(masterConfig);
                                masterClient.ConnectTimeout = sentinel.SentinelWorkerConnectTimeoutMs;

                                var masterRole = masterClient.GetServerRole();
                                if (masterRole == RedisServerRole.Master)
                                {
                                    lastValidMasterFromSentinelAt = DateTime.UtcNow;
                                    return masterClient;
                                }
                                else
                                {
                                    Interlocked.Increment(ref RedisState.TotalInvalidMasters);
                                }
                            }
                            catch { /* Ignore errors until MaxWait */ }

                            if (stopwatch.Elapsed > sentinel.MaxWaitBetweenSentinelLookups)
                                throw new TimeoutException("Max Wait Between Sentinel Lookups Elapsed: {0}"
                                    .Fmt(sentinel.MaxWaitBetweenSentinelLookups.ToString()));

                            Thread.Sleep(sentinel.WaitBetweenSentinelLookups);
                        }
                    }
                    catch (Exception ex)
                    {
                        log.Error("Redis Master Host '{0}' is {1}. Resetting allHosts...".Fmt(config.GetHostString(), role), ex);

                        var newMasters = new List<RedisEndpoint>();
                        var newSlaves = new List<RedisEndpoint>();
                        RedisClient masterClient = null;
                        foreach (var hostConfig in allHosts)
                        {
                            try
                            {
                                var testClient = ClientFactory(hostConfig);
                                testClient.ConnectTimeout = RedisConfig.HostLookupTimeoutMs;
                                var testRole = testClient.GetServerRole();
                                switch (testRole)
                                {
                                    case RedisServerRole.Master:
                                        newMasters.Add(hostConfig);
                                        if (masterClient == null)
                                            masterClient = testClient;
                                        break;
                                    case RedisServerRole.Slave:
                                        newSlaves.Add(hostConfig);
                                        break;
                                }

                            }
                            catch { /* skip past invalid master connections */ }
                        }

                        if (masterClient == null)
                        {
                            Interlocked.Increment(ref RedisState.TotalNoMastersFound);
                            var errorMsg = "No master found in: " + string.Join(", ", allHosts.Map(x => x.GetHostString()));
                            log.Error(errorMsg);
                            throw new Exception(errorMsg);
                        }

                        ResetMasters(newMasters);
                        ResetSlaves(newSlaves);
                        return masterClient;
                    }
                }
            }

            return client;
        }
    }
}